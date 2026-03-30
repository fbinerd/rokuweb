using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime.Publishing;

public sealed class BrowserPanelInteractionHlsService
{
    private static readonly TimeSpan SegmentDuration = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan SegmentInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PlaylistTtl = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SegmentTtl = TimeSpan.FromSeconds(6);
    private const int PlaylistSize = 3;
    private static readonly bool UseSyntheticAudio = string.Equals(Environment.GetEnvironmentVariable("SUPERPAINEL_SYNTH_AUDIO"), "1", StringComparison.OrdinalIgnoreCase);

    private readonly BrowserSnapshotService _snapshotService;
    private readonly BrowserAudioCaptureService _audioCaptureService;
    private readonly string _rootDirectory;
    private readonly string _ffmpegPath;
    private readonly ConcurrentDictionary<Guid, WindowRollingStream> _streams = new();

    public BrowserPanelInteractionHlsService(BrowserSnapshotService snapshotService, BrowserAudioCaptureService audioCaptureService)
    {
        _snapshotService = snapshotService;
        _audioCaptureService = audioCaptureService;
        _rootDirectory = Path.Combine(AppDataPaths.Root, "panel-hls-interaction");
        Directory.CreateDirectory(_rootDirectory);
        _ffmpegPath = ResolveFfmpegPath();
        if (!string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            AppLog.Write("PanelInteractionHls", $"ffmpeg detectado para HLS de interacao do painel: {_ffmpegPath}");
            if (UseSyntheticAudio)
            {
                AppLog.Write("PanelInteractionHls", "Modo sintetico habilitado para diagnostico de audio.");
            }
        }
        else
        {
            AppLog.Write("PanelInteractionHls", "ffmpeg nao encontrado. HLS de interacao do painel desabilitado.");
        }
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_ffmpegPath);

    public void EnsureWindow(Guid windowId)
    {
        if (!IsAvailable)
        {
            return;
        }

        var stream = _streams.GetOrAdd(windowId, id => new WindowRollingStream(id, Path.Combine(_rootDirectory, id.ToString("N"))));
        stream.Touch();
        stream.EnsureStarted(_snapshotService, _audioCaptureService, _ffmpegPath);
    }

    public void Unregister(Guid windowId)
    {
        if (_streams.TryRemove(windowId, out var stream))
        {
            stream.Dispose();
        }
    }

    public bool HasPlaylist(Guid windowId)
    {
        if (!IsAvailable)
        {
            return false;
        }

        return _streams.TryGetValue(windowId, out var stream) && stream.HasArtifact("index.m3u8");
    }

    public bool TryGetPlaylistBytes(Guid windowId, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (!_streams.TryGetValue(windowId, out var stream))
        {
            return false;
        }

        return stream.TryReadArtifact("index.m3u8", out payload);
    }

    public bool TryGetSegmentBytes(Guid windowId, string fileName, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (!_streams.TryGetValue(windowId, out var stream))
        {
            return false;
        }

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return false;
        }

        return stream.TryReadArtifact(safeName, out payload);
    }

    private static string ResolveFfmpegPath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("SUPERPAINEL_FFMPEG_PATH"),
            Environment.GetEnvironmentVariable("FFMPEG_PATH"),
            TryResolveRepositoryFfmpegPath(),
            "ffmpeg"
        };

        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }

                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-version",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (process is null)
                {
                    continue;
                }

                process.WaitForExit(1500);
                if (process.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static string TryResolveRepositoryFfmpegPath()
    {
        try
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var manifestPath = Path.Combine(current.FullName, "manifest");
                var superPath = Path.Combine(current.FullName, "super");
                if (File.Exists(manifestPath) && Directory.Exists(superPath))
                {
                    var localFfmpegPath = Path.Combine(current.FullName, "tools", "ffmpeg", "ffmpeg.exe");
                    if (File.Exists(localFfmpegPath))
                    {
                        return localFfmpegPath;
                    }

                    break;
                }

                current = current.Parent;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private sealed class WindowRollingStream : IDisposable
    {
        private readonly object _gate = new();
        private readonly Guid _windowId;
        private readonly List<SegmentEntry> _segments = new();
        private readonly HlsInMemoryArtifactStore _artifactStore = new();
        private CancellationTokenSource? _cancellation;
        private Task? _worker;
        private DateTime _lastTouchedUtc;
        private int _sequence;
        private bool _loggedPlaylistReady;

        public WindowRollingStream(Guid windowId, string outputDirectory)
        {
            _windowId = windowId;
            OutputDirectory = outputDirectory;
            Directory.CreateDirectory(OutputDirectory);
            _lastTouchedUtc = DateTime.UtcNow;
        }

        public string OutputDirectory { get; }

        public void Touch()
        {
            _lastTouchedUtc = DateTime.UtcNow;
        }

        public void ResetIfAwaitingFreshAudio(bool awaitingFreshAudio)
        {
            if (!awaitingFreshAudio)
            {
                return;
            }

            lock (_gate)
            {
                if (_segments.Count == 0 && !File.Exists(Path.Combine(OutputDirectory, "index.m3u8")))
                {
                    return;
                }

                _segments.Clear();
                _sequence = 0;
                _loggedPlaylistReady = false;
                TryDelete(Path.Combine(OutputDirectory, "index.m3u8"));
                foreach (var stale in Directory.GetFiles(OutputDirectory, "segment-*.ts"))
                {
                    TryDelete(stale);
                }
            }
        }

        public void EnsureStarted(BrowserSnapshotService snapshotService, BrowserAudioCaptureService audioCaptureService, string ffmpegPath)
        {
            lock (_gate)
            {
                if (_worker is not null && !_worker.IsCompleted)
                {
                    return;
                }

                _cancellation?.Cancel();
                _cancellation?.Dispose();
                _cancellation = new CancellationTokenSource();
                _worker = Task.Run(() => RunAsync(snapshotService, audioCaptureService, ffmpegPath, _cancellation.Token));
            }
        }

        private async Task RunAsync(BrowserSnapshotService snapshotService, BrowserAudioCaptureService audioCaptureService, string ffmpegPath, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (DateTime.UtcNow - _lastTouchedUtc > TimeSpan.FromMinutes(5))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var jpegBytes = await snapshotService.CaptureJpegAsync(_windowId, cancellationToken).ConfigureAwait(false);
                    var wavBytes = UseSyntheticAudio
                        ? BuildSineWaveSnapshot()
                        : audioCaptureService.CaptureWaveSnapshot(_windowId, SegmentDuration) ?? BuildSilentWaveSnapshot();
                    if (jpegBytes is null || jpegBytes.Length < 1024 || wavBytes.Length < 4096)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var nextSequence = Interlocked.Increment(ref _sequence);
                    var imagePath = Path.Combine(OutputDirectory, $"frame-{nextSequence:D6}.jpg");
                    var audioPath = Path.Combine(OutputDirectory, $"audio-{nextSequence:D6}.wav");
                    var segmentFileName = $"segment-{nextSequence:D6}.ts";
                    var segmentPath = Path.Combine(OutputDirectory, segmentFileName);

                    File.WriteAllBytes(imagePath, jpegBytes);
                    File.WriteAllBytes(audioPath, wavBytes);

                    var arguments = string.Format(
                        CultureInfo.InvariantCulture,
                        "-hide_banner -loglevel error -y -loop 1 -framerate 24 -i \"{0}\" -i \"{1}\" -map 0:v:0 -map 1:a:0 -t {2:0.###} -c:v libx264 -preset ultrafast -profile:v baseline -level 3.1 -tune stillimage -pix_fmt yuv420p -c:a aac -b:a 128k -ar 48000 -ac 2 -af aresample=async=1:first_pts=0 -shortest -fflags +genpts -avoid_negative_ts make_zero -muxpreload 0 -muxdelay 0 -mpegts_flags resend_headers -f mpegts \"{3}\"",
                        imagePath,
                        audioPath,
                        SegmentDuration.TotalSeconds,
                        segmentPath);

                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = arguments,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    });

                    if (process is null)
                    {
                        await Task.Delay(SegmentInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
                    if (process.ExitCode == 0 && File.Exists(segmentPath))
                    {
                        _artifactStore.Put(segmentFileName, File.ReadAllBytes(segmentPath), SegmentTtl, removeAfterRead: false);
                        RegisterSegment(new SegmentEntry(segmentFileName, SegmentDuration));
                        WritePlaylist();
                        if (!_loggedPlaylistReady)
                        {
                            _loggedPlaylistReady = true;
                            AppLog.Write("PanelInteractionHls", $"Playlist HLS de interacao pronta para janela {_windowId:N}");
                        }
                    }
                    else
                    {
                        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                        AppLog.Write("PanelInteractionHls", $"ffmpeg falhou na janela {_windowId:N}: {error}");
                    }

                    TryDelete(imagePath);
                    TryDelete(audioPath);
                    TryDelete(segmentPath);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Write("PanelInteractionHls", $"Erro no gerador HLS de interacao da janela {_windowId:N}: {ex.Message}");
                }

                await Task.Delay(SegmentInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        private void RegisterSegment(SegmentEntry entry)
        {
            lock (_gate)
            {
                _segments.Add(entry);
                while (_segments.Count > PlaylistSize)
                {
                    var stale = _segments[0];
                    _segments.RemoveAt(0);
                    _artifactStore.Remove(stale.FileName);
                    TryDelete(Path.Combine(OutputDirectory, stale.FileName));
                }
            }
        }

        private void WritePlaylist()
        {
            List<SegmentEntry> snapshot;
            lock (_gate)
            {
                snapshot = _segments.ToList();
            }

            if (snapshot.Count == 0)
            {
                return;
            }

            var firstSequence = ParseSequence(snapshot[0].FileName);
            var targetDuration = (int)Math.Ceiling(SegmentDuration.TotalSeconds);
            var builder = new StringBuilder();
            builder.AppendLine("#EXTM3U");
            builder.AppendLine("#EXT-X-VERSION:3");
            builder.AppendLine("#EXT-X-TARGETDURATION:" + targetDuration.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("#EXT-X-MEDIA-SEQUENCE:" + firstSequence.ToString(CultureInfo.InvariantCulture));

            for (var i = 0; i < snapshot.Count; i++)
            {
                var segment = snapshot[i];
                if (i > 0)
                {
                    builder.AppendLine("#EXT-X-DISCONTINUITY");
                }

                builder.AppendLine("#EXTINF:" + segment.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + ",");
                builder.AppendLine(segment.FileName);
            }

            _artifactStore.Put("index.m3u8", Encoding.ASCII.GetBytes(builder.ToString()), PlaylistTtl, removeAfterRead: false);
        }

        public bool HasArtifact(string fileName)
        {
            return _artifactStore.Has(fileName);
        }

        public bool TryReadArtifact(string fileName, out byte[] payload)
        {
            return _artifactStore.TryRead(fileName, out payload);
        }

        public void Dispose()
        {
            try
            {
                _cancellation?.Cancel();
            }
            catch
            {
            }

            try
            {
                lock (_gate)
                {
                    _segments.Clear();
                    _sequence = 0;
                    _loggedPlaylistReady = false;
                    _artifactStore.Clear();
                    TryDelete(Path.Combine(OutputDirectory, "index.m3u8"));
                    foreach (var stale in Directory.GetFiles(OutputDirectory, "segment-*.ts"))
                    {
                        TryDelete(stale);
                    }
                }
            }
            catch
            {
            }
        }

        private static int ParseSequence(string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(name))
            {
                return 0;
            }

            var parts = name.Split('-');
            if (parts.Length < 2)
            {
                return 0;
            }

            var lastPart = parts[parts.Length - 1];
            return int.TryParse(lastPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) ? sequence : 0;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class SegmentEntry
    {
        public SegmentEntry(string fileName, TimeSpan duration)
        {
            FileName = fileName;
            Duration = duration;
        }

        public string FileName { get; }

        public TimeSpan Duration { get; }
    }

    private static byte[] BuildSineWaveSnapshot()
    {
        const int sampleRate = 48000;
        const int channels = 2;
        const double frequency = 440.0;
        var totalFrames = (int)Math.Round(SegmentDuration.TotalSeconds * sampleRate);
        var pcmBytes = new byte[totalFrames * channels * 2];
        var writeIndex = 0;

        for (var frame = 0; frame < totalFrames; frame++)
        {
            var sample = (short)Math.Round(Math.Sin((2.0 * Math.PI * frequency * frame) / sampleRate) * (short.MaxValue * 0.25));
            for (var channel = 0; channel < channels; channel++)
            {
                pcmBytes[writeIndex++] = (byte)(sample & 0xFF);
                pcmBytes[writeIndex++] = (byte)((sample >> 8) & 0xFF);
            }
        }

        using (var stream = new MemoryStream(44 + pcmBytes.Length))
        using (var writer = new BinaryWriter(stream))
        {
            const int bitsPerSample = 16;
            var blockAlign = channels * bitsPerSample / 8;
            var byteRate = sampleRate * blockAlign;

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

    private static byte[] BuildSilentWaveSnapshot()
    {
        const int sampleRate = 48000;
        const int channels = 2;
        var totalFrames = (int)Math.Round(SegmentDuration.TotalSeconds * sampleRate);
        var pcmBytes = new byte[totalFrames * channels * 2];

        using (var stream = new MemoryStream(44 + pcmBytes.Length))
        using (var writer = new BinaryWriter(stream))
        {
            const int bitsPerSample = 16;
            var blockAlign = channels * bitsPerSample / 8;
            var byteRate = sampleRate * blockAlign;

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
