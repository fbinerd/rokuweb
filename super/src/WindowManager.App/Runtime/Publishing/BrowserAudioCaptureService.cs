using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CefSharp;
using CefSharp.Handler;

namespace WindowManager.App.Runtime.Publishing;

public sealed class BrowserAudioCaptureService
{
    private static readonly StreamingTuning Tuning = StreamingTuning.Current;
    private static readonly TimeSpan AudioFreshnessWindow = TimeSpan.FromMilliseconds(Tuning.AudioFreshnessMs);
    private static readonly TimeSpan MaxBufferedAudio = TimeSpan.FromMilliseconds(Tuning.AudioMaxBufferMs);
    private static readonly TimeSpan MaxLiveReadLag = TimeSpan.FromMilliseconds(Tuning.AudioMaxLiveReadLagMs);
    private static readonly TimeSpan AudioSyncOffset = TimeSpan.FromMilliseconds(Tuning.AudioSyncOffsetMs);
    private static readonly TimeSpan AudioRestartCoalesceWindow = TimeSpan.FromSeconds(5);
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

    public byte[]? CaptureWaveSnapshot(Guid windowId)
    {
        if (!_buffers.TryGetValue(windowId, out var buffer))
        {
            return null;
        }

        return buffer.BuildWaveSnapshot();
    }

    public byte[]? CaptureWaveSnapshot(Guid windowId, TimeSpan maxDuration)
    {
        if (!_buffers.TryGetValue(windowId, out var buffer))
        {
            return null;
        }

        return buffer.BuildWaveSnapshot(maxDuration);
    }

    public AudioPcmChunk ReadPcmChunk(Guid windowId, long cursor, int maxBytes)
    {
        if (!_buffers.TryGetValue(windowId, out var buffer))
        {
            return AudioPcmChunk.Empty(cursor);
        }

        return buffer.ReadPcmChunk(cursor, maxBytes);
    }

    public AudioFormatInfo? GetAudioFormat(Guid windowId)
    {
        if (!_buffers.TryGetValue(windowId, out var buffer))
        {
            return null;
        }

        return buffer.GetAudioFormat();
    }

    internal bool TryConfigure(Guid windowId, CefSharp.Structs.AudioParameters parameters, int channels)
    {
        var sampleRate = Math.Max(1, parameters.SampleRate);
        var resolvedChannels = Math.Max(1, channels);
        var maxBytes = sampleRate * resolvedChannels * 2 * (int)Math.Max(1, MaxBufferedAudio.TotalSeconds);
        var buffer = _buffers.GetOrAdd(windowId, _ => new WindowAudioBuffer());
        var configureResult = buffer.Configure(sampleRate, resolvedChannels, maxBytes, AudioRestartCoalesceWindow);
        if (configureResult.ReusedExistingStream)
        {
            AppLog.Write(
                "BrowserAudio",
                string.Format(
                    "Audio stream reiniciado/reaproveitado: janela={0}, sampleRate={1}, channels={2}, generation={3}",
                    windowId.ToString("N"),
                    sampleRate,
                    resolvedChannels,
                    configureResult.Generation));
        }
        else
        {
            AppLog.Write(
                "BrowserAudio",
                string.Format(
                    "Audio stream iniciado: janela={0}, sampleRate={1}, channels={2}, generation={3}",
                    windowId.ToString("N"),
                    sampleRate,
                    resolvedChannels,
                    configureResult.Generation));
        }
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

    private sealed class WindowAudioBuffer
    {
        private readonly object _gate = new object();
        private byte[] _pcmBytes = Array.Empty<byte>();
        private int _sampleRate;
        private int _channels;
        private int _maxBytes;
        private long _totalBytesWritten;
        private int _streamGeneration;
        private DateTime _lastPacketUtc = DateTime.MinValue;

        public AudioConfigureResult Configure(int sampleRate, int channels, int maxBytes, TimeSpan restartCoalesceWindow)
        {
            lock (_gate)
            {
                var sameFormat = _sampleRate == sampleRate && _channels == channels;
                var recentlyActive = _lastPacketUtc != DateTime.MinValue && DateTime.UtcNow - _lastPacketUtc <= restartCoalesceWindow;
                if (sameFormat && recentlyActive)
                {
                    _maxBytes = Math.Max(4096, maxBytes);
                    return new AudioConfigureResult(_streamGeneration, true);
                }

                _sampleRate = sampleRate;
                _channels = channels;
                _maxBytes = Math.Max(4096, maxBytes);
                _pcmBytes = Array.Empty<byte>();
                _totalBytesWritten = 0;
                _streamGeneration++;
                _lastPacketUtc = DateTime.MinValue;
                return new AudioConfigureResult(_streamGeneration, false);
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
                _lastPacketUtc = DateTime.UtcNow;
            }
        }

        public void MarkStopped()
        {
            lock (_gate)
            {
                _pcmBytes = Array.Empty<byte>();
                _totalBytesWritten = 0;
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

        public byte[]? BuildWaveSnapshot()
        {
            return BuildWaveSnapshot(TimeSpan.Zero);
        }

        public byte[]? BuildWaveSnapshot(TimeSpan maxDuration)
        {
            lock (_gate)
            {
                if (_sampleRate <= 0 || _channels <= 0 || _pcmBytes.Length == 0)
                {
                    return null;
                }

                var pcmBytes = SliceRecentPcmBytes(maxDuration);
                if (pcmBytes.Length == 0)
                {
                    return null;
                }

                return BuildWaveFile(pcmBytes, _sampleRate, _channels);
            }
        }

        public AudioFormatInfo? GetAudioFormat()
        {
            lock (_gate)
            {
                if (_sampleRate <= 0 || _channels <= 0)
                {
                    return null;
                }

                return new AudioFormatInfo(_sampleRate, _channels, _streamGeneration);
            }
        }

        public AudioPcmChunk ReadPcmChunk(long cursor, int maxBytes)
        {
            lock (_gate)
            {
                if (_sampleRate <= 0 || _channels <= 0 || maxBytes <= 0)
                {
                    return AudioPcmChunk.Empty(cursor);
                }

                var bytesPerFrame = _channels * 2;
                if (bytesPerFrame <= 0)
                {
                    return AudioPcmChunk.Empty(cursor);
                }

                if (_pcmBytes.Length == 0)
                {
                    return new AudioPcmChunk(Array.Empty<byte>(), cursor, _sampleRate, _channels, _streamGeneration);
                }

                var availableEnd = _totalBytesWritten;
                if (_lastPacketUtc == DateTime.MinValue || DateTime.UtcNow - _lastPacketUtc > AudioFreshnessWindow)
                {
                    return new AudioPcmChunk(Array.Empty<byte>(), availableEnd, _sampleRate, _channels, _streamGeneration);
                }

                var availableStart = Math.Max(0, availableEnd - _pcmBytes.Length);
                var normalizedCursor = Math.Max(cursor, availableStart);
                if (normalizedCursor > availableEnd)
                {
                    normalizedCursor = availableEnd;
                }

                var maxLagBytes = AlignToFrameBoundary(
                    (int)Math.Round(_sampleRate * _channels * 2 * MaxLiveReadLag.TotalSeconds),
                    bytesPerFrame);
                if (maxLagBytes > 0)
                {
                    var liveCursorFloor = Math.Max(availableStart, availableEnd - maxLagBytes);
                    var syncOffsetBytes = AlignToFrameBoundary(
                        (int)Math.Round(_sampleRate * _channels * 2 * Math.Abs(AudioSyncOffset.TotalSeconds)),
                        bytesPerFrame);
                    if (syncOffsetBytes > 0)
                    {
                        if (AudioSyncOffset > TimeSpan.Zero)
                        {
                            liveCursorFloor = Math.Max(availableStart, liveCursorFloor - syncOffsetBytes);
                        }
                        else if (AudioSyncOffset < TimeSpan.Zero)
                        {
                            liveCursorFloor = Math.Min(availableEnd, liveCursorFloor + syncOffsetBytes);
                        }
                    }

                    if (normalizedCursor < liveCursorFloor)
                    {
                        var droppedBytes = liveCursorFloor - normalizedCursor;
                        AppLog.Write(
                            "BrowserAudio",
                            string.Format(
                                "Backlog de audio descartado para manter tempo real: bytes={0}, lagMs~={1:0}, generation={2}",
                                droppedBytes,
                                _sampleRate > 0 && _channels > 0 ? (droppedBytes * 1000.0) / (_sampleRate * _channels * 2) : 0,
                                _streamGeneration));
                        normalizedCursor = liveCursorFloor;
                    }
                }

                var availableBytes = (int)Math.Min(maxBytes, availableEnd - normalizedCursor);
                availableBytes -= availableBytes % bytesPerFrame;
                if (availableBytes <= 0)
                {
                    return new AudioPcmChunk(Array.Empty<byte>(), normalizedCursor, _sampleRate, _channels, _streamGeneration);
                }

                var startIndex = (int)(normalizedCursor - availableStart);
                var slice = new byte[availableBytes];
                Buffer.BlockCopy(_pcmBytes, startIndex, slice, 0, availableBytes);
                return new AudioPcmChunk(slice, normalizedCursor + availableBytes, _sampleRate, _channels, _streamGeneration);
            }
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
            _totalBytesWritten += packetBytes.Length;
        }

        private static int AlignToFrameBoundary(int byteCount, int bytesPerFrame)
        {
            if (byteCount <= 0 || bytesPerFrame <= 0)
            {
                return 0;
            }

            return byteCount - (byteCount % bytesPerFrame);
        }

        private byte[] SliceRecentPcmBytes(TimeSpan maxDuration)
        {
            if (maxDuration <= TimeSpan.Zero)
            {
                return _pcmBytes;
            }

            var bytesPerFrame = _channels * 2;
            if (bytesPerFrame <= 0)
            {
                return _pcmBytes;
            }

            var targetFrames = (int)Math.Round(_sampleRate * maxDuration.TotalSeconds);
            if (targetFrames <= 0)
            {
                return _pcmBytes;
            }

            var targetBytes = targetFrames * bytesPerFrame;
            if (targetBytes <= 0 || _pcmBytes.Length <= targetBytes)
            {
                return _pcmBytes;
            }

            var alignedBytes = targetBytes - (targetBytes % bytesPerFrame);
            if (alignedBytes <= 0)
            {
                return _pcmBytes;
            }

            var slice = new byte[alignedBytes];
            Buffer.BlockCopy(_pcmBytes, _pcmBytes.Length - alignedBytes, slice, 0, alignedBytes);
            return slice;
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
    }
}

public readonly struct AudioConfigureResult
{
    public AudioConfigureResult(int generation, bool reusedExistingStream)
    {
        Generation = generation;
        ReusedExistingStream = reusedExistingStream;
    }

    public int Generation { get; }

    public bool ReusedExistingStream { get; }
}

public sealed class AudioFormatInfo
{
    public AudioFormatInfo(int sampleRate, int channels, int generation)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Generation = generation;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public int Generation { get; }
}

public sealed class AudioPcmChunk
{
    public AudioPcmChunk(byte[] bytes, long nextCursor, int sampleRate, int channels, int generation)
    {
        Bytes = bytes ?? Array.Empty<byte>();
        NextCursor = nextCursor;
        SampleRate = sampleRate;
        Channels = channels;
        Generation = generation;
    }

    public byte[] Bytes { get; }

    public long NextCursor { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public int Generation { get; }

    public static AudioPcmChunk Empty(long nextCursor)
    {
        return new AudioPcmChunk(Array.Empty<byte>(), nextCursor, 0, 0, 0);
    }
}
