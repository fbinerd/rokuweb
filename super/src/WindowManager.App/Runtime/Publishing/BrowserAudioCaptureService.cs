using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.Handler;

namespace WindowManager.App.Runtime.Publishing;

public sealed class BrowserAudioCaptureService
{
    private static readonly TimeSpan AudioFreshnessWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBufferedAudio = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<Guid, WindowAudioBuffer> _buffers = new ConcurrentDictionary<Guid, WindowAudioBuffer>();

    public IAudioHandler CreateHandler(Guid windowId)
    {
        return new WindowAudioHandler(windowId, this);
    }

    public void Unregister(Guid windowId)
    {
        _buffers.TryRemove(windowId, out _);
    }

    public bool HasRecentAudio(Guid windowId)
    {
        return _buffers.TryGetValue(windowId, out var buffer) &&
               buffer.HasRecentAudio(AudioFreshnessWindow);
    }

    public byte[]? CaptureWaveSnapshot(Guid windowId, TimeSpan? maxDuration = null)
    {
        if (!_buffers.TryGetValue(windowId, out var buffer))
        {
            return null;
        }

        return buffer.BuildWaveSnapshot(maxDuration);
    }

    public bool TryGetLiveAudioFormat(Guid windowId, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 0;

        if (!_buffers.TryGetValue(windowId, out var buffer))
        {
            return false;
        }

        return buffer.TryGetFormat(out sampleRate, out channels);
    }

    public Task<LiveAudioPacket?> WaitForNextPacketAsync(Guid windowId, long lastSequence, CancellationToken cancellationToken)
    {
        if (!_buffers.TryGetValue(windowId, out var buffer))
        {
            return Task.FromResult<LiveAudioPacket?>(null);
        }

        return buffer.WaitForNextPacketAsync(lastSequence, cancellationToken);
    }

    internal bool TryConfigure(Guid windowId, CefSharp.Structs.AudioParameters parameters, int channels)
    {
        var sampleRate = Math.Max(1, parameters.SampleRate);
        var resolvedChannels = Math.Max(1, channels);
        var maxBytes = sampleRate * resolvedChannels * 2 * (int)Math.Max(1, MaxBufferedAudio.TotalSeconds);
        var buffer = _buffers.GetOrAdd(windowId, _ => new WindowAudioBuffer());
        buffer.Configure(sampleRate, resolvedChannels, maxBytes);
        AppLog.Write("BrowserAudio", string.Format("Audio stream iniciado: janela={0}, sampleRate={1}, channels={2}", windowId.ToString("N"), sampleRate, resolvedChannels));
        return true;
    }

    internal void AppendPacket(Guid windowId, IntPtr data, int frames)
    {
        if (!_buffers.TryGetValue(windowId, out var buffer))
        {
            return;
        }

        buffer.AppendPacket(data, frames);
    }

    internal void MarkStopped(Guid windowId)
    {
        if (_buffers.TryGetValue(windowId, out var buffer))
        {
            buffer.MarkStopped();
        }
    }

    private sealed class WindowAudioHandler : AudioHandler
    {
        private readonly Guid _windowId;
        private readonly BrowserAudioCaptureService _service;

        public WindowAudioHandler(Guid windowId, BrowserAudioCaptureService service)
        {
            _windowId = windowId;
            _service = service;
        }

        protected override bool GetAudioParameters(IWebBrowser chromiumWebBrowser, IBrowser browser, ref CefSharp.Structs.AudioParameters parameters)
        {
            return true;
        }

        protected override void OnAudioStreamStarted(IWebBrowser chromiumWebBrowser, IBrowser browser, CefSharp.Structs.AudioParameters parameters, int channels)
        {
            _service.TryConfigure(_windowId, parameters, channels);
        }

        protected override void OnAudioStreamPacket(IWebBrowser chromiumWebBrowser, IBrowser browser, IntPtr data, int noOfFrames, long pts)
        {
            _service.AppendPacket(_windowId, data, noOfFrames);
        }

        protected override void OnAudioStreamStopped(IWebBrowser chromiumWebBrowser, IBrowser browser)
        {
            _service.MarkStopped(_windowId);
        }

        protected override void OnAudioStreamError(IWebBrowser chromiumWebBrowser, IBrowser browser, string errorMessage)
        {
            AppLog.Write("BrowserAudio", string.Format("Erro no audio do browser: janela={0}, erro={1}", _windowId.ToString("N"), errorMessage));
        }
    }

    public sealed class LiveAudioPacket
    {
        public long Sequence { get; set; }

        public byte[] Bytes { get; set; } = Array.Empty<byte>();
    }

    private sealed class WindowAudioBuffer
    {
        private const int MaxLivePackets = 64;
        private readonly object _gate = new object();
        private readonly Queue<LiveAudioPacket> _livePackets = new Queue<LiveAudioPacket>();
        private readonly SemaphoreSlim _packetSignal = new SemaphoreSlim(0);
        private byte[] _pcmBytes = Array.Empty<byte>();
        private int _sampleRate;
        private int _channels;
        private int _maxBytes;
        private DateTime _lastPacketUtc = DateTime.MinValue;
        private long _sequence;
        private bool _loggedFirstPacket;

        public void Configure(int sampleRate, int channels, int maxBytes)
        {
            lock (_gate)
            {
                _sampleRate = sampleRate;
                _channels = channels;
                _maxBytes = Math.Max(4096, maxBytes);
                _pcmBytes = Array.Empty<byte>();
                _lastPacketUtc = DateTime.MinValue;
                _loggedFirstPacket = false;
            }
        }

        public void AppendPacket(IntPtr data, int frames)
        {
            lock (_gate)
            {
                if (_sampleRate <= 0 || _channels <= 0 || frames <= 0 || data == IntPtr.Zero)
                {
                    return;
                }

                var channelPointers = new IntPtr[_channels];
                Marshal.Copy(data, channelPointers, 0, _channels);

                var channelData = new float[_channels][];
                for (var channelIndex = 0; channelIndex < _channels; channelIndex++)
                {
                    channelData[channelIndex] = new float[frames];
                    if (channelPointers[channelIndex] != IntPtr.Zero)
                    {
                        Marshal.Copy(channelPointers[channelIndex], channelData[channelIndex], 0, frames);
                    }
                }

                var packetBytes = new byte[frames * _channels * 2];
                var writeIndex = 0;
                for (var frameIndex = 0; frameIndex < frames; frameIndex++)
                {
                    for (var channelIndex = 0; channelIndex < _channels; channelIndex++)
                    {
                        var sample = channelData[channelIndex][frameIndex];
                        if (sample > 1f)
                        {
                            sample = 1f;
                        }
                        else if (sample < -1f)
                        {
                            sample = -1f;
                        }

                        var pcm = (short)Math.Round(sample * short.MaxValue);
                        packetBytes[writeIndex++] = (byte)(pcm & 0xFF);
                        packetBytes[writeIndex++] = (byte)((pcm >> 8) & 0xFF);
                    }
                }

                AppendBytes(packetBytes);
                _sequence++;
                if (!_loggedFirstPacket)
                {
                    _loggedFirstPacket = true;
                    AppLog.Write("BrowserAudio", string.Format("Primeiro pacote de audio recebido: sampleRate={0}, channels={1}, bytes={2}", _sampleRate, _channels, packetBytes.Length));
                }
                _livePackets.Enqueue(new LiveAudioPacket
                {
                    Sequence = _sequence,
                    Bytes = packetBytes
                });
                while (_livePackets.Count > MaxLivePackets)
                {
                    _livePackets.Dequeue();
                }
                try
                {
                    _packetSignal.Release();
                }
                catch
                {
                }
                _lastPacketUtc = DateTime.UtcNow;
            }
        }

        public void MarkStopped()
        {
            lock (_gate)
            {
                _lastPacketUtc = DateTime.MinValue;
            }
        }

        public bool HasRecentAudio(TimeSpan freshness)
        {
            lock (_gate)
            {
                return _sampleRate > 0 &&
                       _channels > 0 &&
                       _pcmBytes.Length > 0 &&
                       DateTime.UtcNow - _lastPacketUtc <= freshness;
            }
        }

        public byte[]? BuildWaveSnapshot(TimeSpan? maxDuration = null)
        {
            lock (_gate)
            {
                if (_sampleRate <= 0 || _channels <= 0 || _pcmBytes.Length == 0)
                {
                    return null;
                }

                var pcmBytes = _pcmBytes;
                if (maxDuration.HasValue && maxDuration.Value > TimeSpan.Zero)
                {
                    var bytesPerSecond = _sampleRate * _channels * 2;
                    var maxBytes = (int)Math.Max(bytesPerSecond / 4, Math.Round(bytesPerSecond * maxDuration.Value.TotalSeconds));
                    if (pcmBytes.Length > maxBytes)
                    {
                        var trimmed = new byte[maxBytes];
                        Buffer.BlockCopy(pcmBytes, pcmBytes.Length - maxBytes, trimmed, 0, maxBytes);
                        pcmBytes = trimmed;
                    }
                }

                return BuildWaveFile(pcmBytes, _sampleRate, _channels);
            }
        }

        public bool TryGetFormat(out int sampleRate, out int channels)
        {
            lock (_gate)
            {
                sampleRate = _sampleRate;
                channels = _channels;
                return sampleRate > 0 && channels > 0;
            }
        }

        public async Task<LiveAudioPacket?> WaitForNextPacketAsync(long lastSequence, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    foreach (var packet in _livePackets)
                    {
                        if (packet.Sequence > lastSequence)
                        {
                            return packet;
                        }
                    }
                }

                await _packetSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        private void AppendBytes(byte[] packetBytes)
        {
            if (packetBytes.Length == 0)
            {
                return;
            }

            var existingLength = _pcmBytes.Length;
            var combinedLength = existingLength + packetBytes.Length;
            var targetLength = Math.Min(combinedLength, _maxBytes);
            var combined = new byte[targetLength];

            var bytesToKeepFromExisting = Math.Min(existingLength, Math.Max(0, targetLength - packetBytes.Length));
            if (bytesToKeepFromExisting > 0)
            {
                Buffer.BlockCopy(_pcmBytes, existingLength - bytesToKeepFromExisting, combined, 0, bytesToKeepFromExisting);
            }

            var bytesToCopyFromPacket = Math.Min(packetBytes.Length, targetLength);
            Buffer.BlockCopy(packetBytes, packetBytes.Length - bytesToCopyFromPacket, combined, targetLength - bytesToCopyFromPacket, bytesToCopyFromPacket);
            _pcmBytes = combined;
        }

        private static byte[] BuildWaveFile(byte[] pcmBytes, int sampleRate, int channels)
        {
            var bitsPerSample = 16;
            var blockAlign = channels * bitsPerSample / 8;
            var byteRate = sampleRate * blockAlign;

            using (var stream = new MemoryStream(44 + pcmBytes.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + pcmBytes.Length);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write((short)blockAlign);
                writer.Write((short)bitsPerSample);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(pcmBytes.Length);
                writer.Write(pcmBytes);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public int GetBufferedPcmBytes()
        {
            lock (_gate)
            {
                return _pcmBytes.Length;
            }
        }
    }

    public int GetBufferedPcmBytes(Guid windowId)
    {
        if (!_buffers.TryGetValue(windowId, out var buffer))
        {
            return 0;
        }

        return buffer.GetBufferedPcmBytes();
    }
}
