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

public sealed class BrowserPanelRollingHlsService
{
    private static readonly TimeSpan SegmentDuration = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan SegmentInterval = TimeSpan.FromMilliseconds(1000);
    private const int PlaylistSize = 6;
    private static readonly bool UseSyntheticAudio = string.Equals(Environment.GetEnvironmentVariable("SUPERPAINEL_SYNTH_AUDIO"), "1", StringComparison.OrdinalIgnoreCase);

    private readonly BrowserSnapshotService _snapshotService;
    private readonly BrowserAudioCaptureService _audioCaptureService;
    private readonly string _rootDirectory;
    private readonly string _ffmpegPath;
    private readonly ConcurrentDictionary<Guid, WindowRollingStream> _streams = new();

    public BrowserPanelRollingHlsService(BrowserSnapshotService snapshotService, BrowserAudioCaptureService audioCaptureService)
    {
        _snapshotService = snapshotService;
        _audioCaptureService = audioCaptureService;
        _rootDirectory = Path.Combine(AppDataPaths.Root, "panel-hls-rolling");
        Directory.CreateDirectory(_rootDirectory);
        _ffmpegPath = ResolveFfmpegPath();
        if (!string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            AppLog.Write("PanelRollingHls", $"ffmpeg detectado para HLS rolling do painel: {_ffmpegPath}");
            if (UseSyntheticAudio)
            {
                AppLog.Write("PanelRollingHls", "Modo sintetico habilitado para diagnostico de audio.");
            }
        }
        else
        {
            AppLog.Write("PanelRollingHls", "ffmpeg nao encontrado. HLS rolling do painel desabilitado.");
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
        stream.ResetIfAwaitingFreshAudio(!_audioCaptureService.HasRecentAudio(windowId));
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

    public bool TryGetPlaylistPath(Guid windowId, out string path)
    {
        path = string.Empty;
        if (!IsAvailable)
        {
            return false;
        }

        EnsureWindow(windowId);
        if (!_streams.TryGetValue(windowId, out var stream))
        {
            return false;
        }

        path = Path.Combine(stream.OutputDirectory, "index.m3u8");
        return File.Exists(path);
    }

    public bool TryGetSegmentPath(Guid windowId, string fileName, out string path)
    {
        path = string.Empty;
        if (!_streams.TryGetValue(windowId, out var stream))
        {
            return false;
        }

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return false;
        }

        path = Path.Combine(stream.OutputDirectory, safeName);
        return File.Exists(path);
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
        private static readonly EncodingProfile[] EncodingProfiles = new[]
        {
            new EncodingProfile("high", 6, 700, 850, 1200, "960:540"),
            new EncodingProfile("medium", 5, 500, 650, 900, "854:480"),
            new EncodingProfile("low", 4, 320, 420, 600, "640:360")
        };
        private readonly object _gate = new();
        private readonly Guid _windowId;
        private readonly List<SegmentEntry> _segments = new();
        private CancellationTokenSource? _cancellation;
        private Task? _worker;
        private DateTime _lastTouchedUtc;
        private int _sequence;
        private bool _loggedPlaylistReady;
        private int _profileIndex = 1;
        private int _slowSegmentStreak;
        private int _fastSegmentStreak;

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
                var cycleStartedAtUtc = DateTime.UtcNow;
                try
                {
                    if (DateTime.UtcNow - _lastTouchedUtc > TimeSpan.FromMinutes(5))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var framePaths = await CaptureFrameSequenceAsync(snapshotService, cancellationToken).ConfigureAwait(false);
                    var wavBytes = UseSyntheticAudio
                        ? BuildSineWaveSnapshot()
                        : audioCaptureService.CaptureWaveSnapshot(_windowId, SegmentDuration);
                    if (framePaths.Count == 0 || wavBytes is null || wavBytes.Length < 4096)
                    {
                        DeleteFiles(framePaths);
                        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var nextSequence = Interlocked.Increment(ref _sequence);
                    var profile = EncodingProfiles[_profileIndex];
                    var audioPath = Path.Combine(OutputDirectory, $"audio-{nextSequence:D6}.wav");
                    var segmentFileName = $"segment-{nextSequence:D6}.ts";
                    var segmentPath = Path.Combine(OutputDirectory, segmentFileName);
                    var segmentFramePattern = Path.Combine(OutputDirectory, $"frame-{nextSequence:D6}-%03d.jpg");

                    File.WriteAllBytes(audioPath, wavBytes);
                    for (var index = 0; index < framePaths.Count; index++)
                    {
                        var targetPath = Path.Combine(OutputDirectory, $"frame-{nextSequence:D6}-{index:D3}.jpg");
                        File.Copy(framePaths[index], targetPath, true);
                    }

                    var arguments = BuildFfmpegArguments(profile, segmentFramePattern, audioPath, segmentPath);
                    var stopwatch = Stopwatch.StartNew();

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
                    stopwatch.Stop();
                    if (process.ExitCode == 0 && File.Exists(segmentPath))
                    {
                        RegisterSegment(new SegmentEntry(segmentFileName, SegmentDuration));
                        WritePlaylist();
                        AppLog.Write("PanelRollingHls", $"Segmento com movimento gerado: janela={_windowId:N}, seq={nextSequence}, frames={framePaths.Count}, perfil={profile.Name}, buildMs={(int)stopwatch.Elapsed.TotalMilliseconds}");
                        UpdateAdaptiveProfile(stopwatch.Elapsed);
                        if (!_loggedPlaylistReady)
                        {
                            _loggedPlaylistReady = true;
                            AppLog.Write("PanelRollingHls", $"Playlist HLS rolling pronta para janela {_windowId:N}");
                        }
                    }
                    else
                    {
                        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                        AppLog.Write("PanelRollingHls", $"ffmpeg falhou na janela {_windowId:N}: {error}");
                    }

                    TryDelete(audioPath);
                    DeleteFiles(framePaths);
                    foreach (var generatedFrame in Directory.GetFiles(OutputDirectory, $"frame-{nextSequence:D6}-*.jpg"))
                    {
                        TryDelete(generatedFrame);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Write("PanelRollingHls", $"Erro no gerador rolling HLS da janela {_windowId:N}: {ex.Message}");
                }

                var remainingDelay = SegmentInterval - (DateTime.UtcNow - cycleStartedAtUtc);
                if (remainingDelay > TimeSpan.Zero)
                {
                    await Task.Delay(remainingDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task<List<string>> CaptureFrameSequenceAsync(BrowserSnapshotService snapshotService, CancellationToken cancellationToken)
        {
            var frames = new List<string>();
            var frameRate = EncodingProfiles[_profileIndex].FrameRate;
            var frameCount = Math.Max(2, Math.Min(6, (int)Math.Round(SegmentDuration.TotalSeconds * frameRate)));
            var frameStep = TimeSpan.FromMilliseconds(SegmentDuration.TotalMilliseconds / frameCount);

            for (var index = 0; index < frameCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var jpegBytes = await snapshotService.CaptureJpegAsync(_windowId, cancellationToken).ConfigureAwait(false);
                if (jpegBytes is not null && jpegBytes.Length >= 1024)
                {
                    var framePath = Path.Combine(OutputDirectory, $"capture-{_windowId:N}-{Guid.NewGuid():N}-{index:D3}.jpg");
                    File.WriteAllBytes(framePath, jpegBytes);
                    frames.Add(framePath);
                }

                if (index + 1 < frameCount)
                {
                    await Task.Delay(frameStep, cancellationToken).ConfigureAwait(false);
                }
            }

            return frames;
        }

        private string BuildFfmpegArguments(EncodingProfile profile, string segmentFramePattern, string audioPath, string segmentPath)
        {
            var gop = profile.FrameRate * 2;
            var scaleFilter = string.IsNullOrWhiteSpace(profile.Scale)
                ? string.Empty
                : string.Format(CultureInfo.InvariantCulture, " -vf \"scale={0}:flags=lanczos\"", profile.Scale);

            return string.Format(
                CultureInfo.InvariantCulture,
                "-hide_banner -loglevel error -y -framerate {0} -start_number 0 -i \"{1}\" -i \"{2}\" -map 0:v:0 -map 1:a:0 -t {3:0.###} -c:v libx264 -preset ultrafast -tune zerolatency -profile:v baseline -level 3.1 -pix_fmt yuv420p{4} -b:v {5}k -maxrate {6}k -bufsize {7}k -g {8} -keyint_min {8} -sc_threshold 0 -threads 2 -c:a aac -b:a 96k -ar 48000 -ac 2 -af aresample=async=1:first_pts=0 -shortest -fflags +genpts -avoid_negative_ts make_zero -muxpreload 0 -muxdelay 0 -mpegts_flags resend_headers -f mpegts \"{9}\"",
                profile.FrameRate,
                segmentFramePattern,
                audioPath,
                SegmentDuration.TotalSeconds,
                scaleFilter,
                profile.VideoBitrateKbps,
                profile.MaxRateKbps,
                profile.BufferSizeKbps,
                gop,
                segmentPath);
        }

        private void UpdateAdaptiveProfile(TimeSpan buildElapsed)
        {
            var slowThreshold = TimeSpan.FromMilliseconds(SegmentInterval.TotalMilliseconds * 0.7);
            var fastThreshold = TimeSpan.FromMilliseconds(SegmentInterval.TotalMilliseconds * 0.35);

            if (buildElapsed >= slowThreshold)
            {
                _slowSegmentStreak++;
                _fastSegmentStreak = 0;
            }
            else if (buildElapsed <= fastThreshold)
            {
                _fastSegmentStreak++;
                _slowSegmentStreak = 0;
            }
            else
            {
                _slowSegmentStreak = 0;
                _fastSegmentStreak = 0;
            }

            if (_slowSegmentStreak >= 2 && _profileIndex < EncodingProfiles.Length - 1)
            {
                _profileIndex++;
                _slowSegmentStreak = 0;
                _fastSegmentStreak = 0;
                AppLog.Write("PanelRollingHls", $"Perfil adaptativo reduzido: janela={_windowId:N}, perfil={EncodingProfiles[_profileIndex].Name}");
                return;
            }

            if (_fastSegmentStreak >= 6 && _profileIndex > 0)
            {
                _profileIndex--;
                _slowSegmentStreak = 0;
                _fastSegmentStreak = 0;
                AppLog.Write("PanelRollingHls", $"Perfil adaptativo elevado: janela={_windowId:N}, perfil={EncodingProfiles[_profileIndex].Name}");
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

            foreach (var segment in snapshot)
            {
                builder.AppendLine("#EXTINF:" + segment.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + ",");
                builder.AppendLine(segment.FileName);
            }

            File.WriteAllText(Path.Combine(OutputDirectory, "index.m3u8"), builder.ToString(), Encoding.ASCII);
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

        private static void DeleteFiles(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                TryDelete(path);
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

    private sealed class FfmpegStreamSession : IDisposable
    {
        private readonly Process _process;

        public FfmpegStreamSession(Process process)
        {
            _process = process;
            InputStream = process.StandardInput.BaseStream;
        }

        public Stream InputStream { get; }

        public bool IsRunning => !_process.HasExited;

        public int ExitCode => _process.HasExited ? _process.ExitCode : 0;

        public void Dispose()
        {
            try
            {
                InputStream.Dispose();
            }
            catch
            {
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch
            {
            }

            _process.Dispose();
        }
    }

    private sealed class EncodingProfile
    {
        public EncodingProfile(string name, int frameRate, int videoBitrateKbps, int maxRateKbps, int bufferSizeKbps, string scale)
        {
            Name = name;
            FrameRate = frameRate;
            VideoBitrateKbps = videoBitrateKbps;
            MaxRateKbps = maxRateKbps;
            BufferSizeKbps = bufferSizeKbps;
            Scale = scale;
        }

        public string Name { get; }

        public int FrameRate { get; }

        public int VideoBitrateKbps { get; }

        public int MaxRateKbps { get; }

        public int BufferSizeKbps { get; }

        public string Scale { get; }
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
}
