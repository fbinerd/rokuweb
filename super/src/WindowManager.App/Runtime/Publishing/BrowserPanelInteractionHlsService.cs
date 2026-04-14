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
    private static StreamingTuning Tuning => StreamingTuning.Current;
    private static TimeSpan SegmentDuration => TimeSpan.FromSeconds(Tuning.InteractionHlsSegmentDurationSeconds);
    private static TimeSpan SegmentInterval => SegmentDuration;
    private static TimeSpan PlaylistTtl => TimeSpan.FromSeconds(Math.Max(4, Tuning.InteractionHlsSegmentDurationSeconds * (Tuning.InteractionHlsPlaylistSize + 2)));
    private static TimeSpan SegmentTtl => TimeSpan.FromSeconds(Math.Max(8, Tuning.InteractionHlsSegmentDurationSeconds * (Tuning.InteractionHlsPlaylistSize + 8)));
    private static int PlaylistSize => Tuning.InteractionHlsPlaylistSize;
    private static int FrameRate => Tuning.InteractionHlsFrameRate;
    private static int VideoBitrate => Tuning.InteractionHlsVideoBitrate;
    private static int AudioBitrate => Tuning.InteractionHlsAudioBitrate;
    private static string VideoFilter => BuildVideoFilter(Tuning.InteractionHlsResolution);
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
            AppLog.Write(
                "PanelInteractionHls",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Perfil interacao HLS: segment={0:0.###}s, playlist={1}, playlistTtl={2:0.###}s, segmentTtl={3:0.###}s, resolution={4}, vbitrate={5}, abitrate={6}, fps={7}",
                    SegmentDuration.TotalSeconds,
                    PlaylistSize,
                    PlaylistTtl.TotalSeconds,
                    SegmentTtl.TotalSeconds,
                    Tuning.InteractionHlsResolution,
                    VideoBitrate,
                    AudioBitrate,
                    FrameRate));
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

    public bool HasWarmupSegments(Guid windowId, int minimumSegmentCount)
    {
        if (!IsAvailable)
        {
            return false;
        }

        return _streams.TryGetValue(windowId, out var stream) && stream.HasWarmupSegments(minimumSegmentCount);
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

    public string GetDiagnosticStatus(Guid windowId)
    {
        if (!_streams.TryGetValue(windowId, out var stream))
        {
            return "interaction-stream=missing";
        }

        return stream.GetDiagnosticStatus();
    }

    private static string ResolveFfmpegPath()
    {
        // Caminho direto relativo ao diretório base
        string baseDir = AppContext.BaseDirectory;
        string directPath = Path.Combine(baseDir, "tools", "ffmpeg", "ffmpeg.exe");
        AppLog.Write("PanelInteractionHls", $"[ffmpeg-detect] Testando caminho direto: {directPath}");
        if (File.Exists(directPath))
        {
            AppLog.Write("PanelInteractionHls", $"[ffmpeg-detect] Encontrado arquivo direto: {directPath}");
            return Path.GetFullPath(directPath);
        }

        // Caminho detectado por TryResolveRepositoryFfmpegPath
        string repoPath = TryResolveRepositoryFfmpegPath();
        AppLog.Write("PanelInteractionHls", $"[ffmpeg-detect] TryResolveRepositoryFfmpegPath retornou: {repoPath}");
        if (!string.IsNullOrWhiteSpace(repoPath) && File.Exists(repoPath))
        {
            AppLog.Write("PanelInteractionHls", $"[ffmpeg-detect] Encontrado arquivo repo: {repoPath}");
            return Path.GetFullPath(repoPath);
        }

        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("SUPERPAINEL_FFMPEG_PATH"),
            Environment.GetEnvironmentVariable("FFMPEG_PATH"),
            "ffmpeg"
        };

        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            try
            {
                AppLog.Write("PanelInteractionHls", $"[ffmpeg-detect] Testando candidato: {candidate}");
                if (File.Exists(candidate))
                {
                    AppLog.Write("PanelInteractionHls", $"[ffmpeg-detect] Encontrado arquivo: {candidate}");
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
                    AppLog.Write("PanelInteractionHls", $"[ffmpeg-detect] Falha ao iniciar processo para: {candidate}");
                    continue;
                }

                process.WaitForExit(1500);
                if (process.ExitCode == 0)
                {
                    AppLog.Write("PanelInteractionHls", $"[ffmpeg-detect] Processo OK para: {candidate}");
                    return candidate;
                }
                else
                {
                    AppLog.Write("PanelInteractionHls", $"[ffmpeg-detect] Processo retornou código {process.ExitCode} para: {candidate}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("PanelInteractionHls", $"[ffmpeg-detect] Exceção ao testar {candidate}: {ex.Message}");
            }
        }

        AppLog.Write("PanelInteractionHls", "[ffmpeg-detect] Nenhum candidato válido encontrado para ffmpeg.exe");
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

                    var frameRate = Math.Max(10, FrameRate);
                    var keyFrameInterval = Math.Max(1, (int)Math.Round(frameRate * SegmentDuration.TotalSeconds, MidpointRounding.AwayFromZero));
                    var arguments = string.Format(
                        CultureInfo.InvariantCulture,
                        "-hide_banner -loglevel error -y -loop 1 -framerate {0} -i \"{1}\" -i \"{2}\" -map 0:v:0 -map 1:a:0 -t {3:0.###} -vf \"{4}\" -c:v libx264 -preset ultrafast -profile:v baseline -level 3.1 -tune stillimage -g {5} -keyint_min {5} -sc_threshold 0 -pix_fmt yuv420p -b:v {6} -maxrate {6} -bufsize {7} -c:a aac -b:a {8} -ar 48000 -ac 2 -af aresample=async=1:first_pts=0 -shortest -fflags +genpts -avoid_negative_ts make_zero -muxpreload 0 -muxdelay 0 -mpegts_flags resend_headers -f mpegts \"{9}\"",
                        frameRate,
                        imagePath,
                        audioPath,
                        SegmentDuration.TotalSeconds,
                        VideoFilter,
                        keyFrameInterval,
                        VideoBitrate.ToString(CultureInfo.InvariantCulture),
                        (VideoBitrate * 2).ToString(CultureInfo.InvariantCulture),
                        AudioBitrate.ToString(CultureInfo.InvariantCulture),
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
                        var segmentBytes = File.ReadAllBytes(segmentPath);
                        _artifactStore.Put(segmentFileName, segmentBytes, SegmentTtl, removeAfterRead: false);
                        RegisterSegment(new SegmentEntry(segmentFileName, SegmentDuration));
                        WritePlaylist();
                        if (nextSequence <= 3 || nextSequence % 20 == 0)
                        {
                            AppLog.Write(
                                "PanelInteractionHls",
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Segmento pronto: janela={0:N}, seq={1}, arquivo={2}, bytes={3}, diag={4}",
                                    _windowId,
                                    nextSequence,
                                    segmentFileName,
                                    segmentBytes.Length,
                                    GetDiagnosticStatus()));
                        }
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
                    _segments.RemoveAt(0);
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
                builder.AppendLine("#EXTINF:" + segment.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + ",");
                builder.AppendLine(segment.FileName);
            }

            _artifactStore.Put("index.m3u8", Encoding.ASCII.GetBytes(builder.ToString()), PlaylistTtl, removeAfterRead: false);
        }

        public bool HasArtifact(string fileName)
        {
            return _artifactStore.Has(fileName);
        }

        public bool HasWarmupSegments(int minimumSegmentCount)
        {
            lock (_gate)
            {
                return _segments.Count >= Math.Max(1, minimumSegmentCount);
            }
        }

        public bool TryReadArtifact(string fileName, out byte[] payload)
        {
            return _artifactStore.TryRead(fileName, out payload);
        }

        public string GetDiagnosticStatus()
        {
            lock (_gate)
            {
                var count = _segments.Count;
                var first = count > 0 ? _segments[0].FileName : "<none>";
                var last = count > 0 ? _segments[count - 1].FileName : "<none>";
                var firstSequence = count > 0 ? ParseSequence(first) : -1;
                var lastSequence = count > 0 ? ParseSequence(last) : -1;
                var hasPlaylist = _artifactStore.Has("index.m3u8");
                var hasLastSegment = count > 0 && _artifactStore.Has(last);

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "interaction-stream=ready segments={0} seq={1}->{2} first={3} last={4} playlist={5} lastSegment={6}",
                    count,
                    firstSequence,
                    lastSequence,
                    first,
                    last,
                    hasPlaylist,
                    hasLastSegment);
            }
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

    private static string BuildVideoFilter(string resolution)
    {
        var normalizedResolution = string.IsNullOrWhiteSpace(resolution) ? "854x480" : resolution.Trim();
        var parts = normalizedResolution.Split('x');
        if (parts.Length != 2)
        {
            return "scale=854:480:force_original_aspect_ratio=decrease,pad=854:480:(ow-iw)/2:(oh-ih)/2";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "scale={0}:{1}:force_original_aspect_ratio=decrease,pad={0}:{1}:(ow-iw)/2:(oh-ih)/2",
            parts[0],
            parts[1]);
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
