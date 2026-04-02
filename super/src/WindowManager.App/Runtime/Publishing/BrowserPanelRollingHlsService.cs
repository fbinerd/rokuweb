using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime.Publishing;

public sealed class BrowserPanelRollingHlsService
{
    private static readonly StreamingTuning Tuning = StreamingTuning.Current;
    private static readonly TimeSpan SegmentTtl = TimeSpan.FromSeconds(Math.Max(20, Tuning.HlsSegmentDurationSeconds * (Tuning.HlsPlaylistSize + 16)));
    private static readonly TimeSpan PlaylistTtl = TimeSpan.FromSeconds(Math.Max(8, Tuning.HlsSegmentDurationSeconds * (Tuning.HlsPlaylistSize + 4)));
    private static readonly bool UseSyntheticAudio = string.Equals(Environment.GetEnvironmentVariable("SUPERPAINEL_SYNTH_AUDIO"), "1", StringComparison.OrdinalIgnoreCase);
    private static readonly RenditionProfile PrimaryRendition = new("medium", Tuning.HlsResolution, Tuning.HlsVideoBitrate, Tuning.HlsAudioBitrate, Tuning.HlsFrameRate);

    private readonly BrowserSnapshotService _snapshotService;
    private readonly BrowserAudioCaptureService _audioCaptureService;
    private readonly string _rootDirectory;
    private readonly string _ffmpegPath;
    private readonly InMemoryHlsPutServer _ingestServer;
    private readonly ConcurrentDictionary<Guid, WindowRollingStream> _streams = new();

    public BrowserPanelRollingHlsService(BrowserSnapshotService snapshotService, BrowserAudioCaptureService audioCaptureService)
    {
        _snapshotService = snapshotService;
        _audioCaptureService = audioCaptureService;
        _rootDirectory = Path.Combine(AppDataPaths.Root, "panel-hls-rolling");
        Directory.CreateDirectory(_rootDirectory);
        _ffmpegPath = ResolveFfmpegPath();
        _ingestServer = new InMemoryHlsPutServer();
        if (!string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            AppLog.Write("PanelRollingHls", $"ffmpeg detectado para HLS rolling do painel: {_ffmpegPath}");
            AppLog.Write("PanelRollingHls", $"Ingest HLS em memoria pronto em http://127.0.0.1:{_ingestServer.Port}/");
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

        var stream = _streams.GetOrAdd(windowId, id => new WindowRollingStream(id, Path.Combine(_rootDirectory, id.ToString("N")), _ingestServer));
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

    public bool HasOutputFile(Guid windowId, string fileName)
    {
        if (!IsAvailable)
        {
            return false;
        }

        if (!_streams.TryGetValue(windowId, out var stream))
        {
            return false;
        }

        return stream.HasArtifact(fileName);
    }

    public bool TryGetOutputBytes(Guid windowId, string fileName, out byte[] payload)
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
            return "stream-nao-criado";
        }

        return stream.GetDiagnosticStatus();
    }

    public int GetStreamGeneration(Guid windowId)
    {
        if (!_streams.TryGetValue(windowId, out var stream))
        {
            return 0;
        }

        return stream.GetGeneration();
    }

    private static string ResolveFfmpegPath()
    {
        // Caminho direto relativo ao diretório base
        string baseDir = AppContext.BaseDirectory;
        string directPath = Path.Combine(baseDir, "tools", "ffmpeg", "ffmpeg.exe");
        AppLog.Write("PanelRollingHls", $"[ffmpeg-detect] Testando caminho direto: {directPath}");
        if (File.Exists(directPath))
        {
            AppLog.Write("PanelRollingHls", $"[ffmpeg-detect] Encontrado arquivo direto: {directPath}");
            return Path.GetFullPath(directPath);
        }

        // Caminho detectado por TryResolveRepositoryFfmpegPath
        string repoPath = TryResolveRepositoryFfmpegPath();
        AppLog.Write("PanelRollingHls", $"[ffmpeg-detect] TryResolveRepositoryFfmpegPath retornou: {repoPath}");
        if (!string.IsNullOrWhiteSpace(repoPath) && File.Exists(repoPath))
        {
            AppLog.Write("PanelRollingHls", $"[ffmpeg-detect] Encontrado arquivo repo: {repoPath}");
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
                AppLog.Write("PanelRollingHls", $"[ffmpeg-detect] Testando candidato: {candidate}");
                if (File.Exists(candidate))
                {
                    AppLog.Write("PanelRollingHls", $"[ffmpeg-detect] Encontrado arquivo: {candidate}");
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
                    AppLog.Write("PanelRollingHls", $"[ffmpeg-detect] Falha ao iniciar processo para: {candidate}");
                    continue;
                }

                process.WaitForExit(1500);
                if (process.ExitCode == 0)
                {
                    AppLog.Write("PanelRollingHls", $"[ffmpeg-detect] Processo OK para: {candidate}");
                    return candidate;
                }
                else
                {
                    AppLog.Write("PanelRollingHls", $"[ffmpeg-detect] Processo retornou código {process.ExitCode} para: {candidate}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("PanelRollingHls", $"[ffmpeg-detect] Exceção ao testar {candidate}: {ex.Message}");
            }
        }

        AppLog.Write("PanelRollingHls", "[ffmpeg-detect] Nenhum candidato válido encontrado para ffmpeg.exe");
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
        private readonly HlsInMemoryArtifactStore _artifactStore = new();
        private readonly InMemoryHlsPutServer _ingestServer;
        private readonly string _routeToken;
        private CancellationTokenSource? _cancellation;
        private Task? _worker;
        private DateTime _lastTouchedUtc;
        private bool _loggedPlaylistReady;
        private byte[]? _pendingPlaylistBytes;
        private string[] _pendingPlaylistSegments = Array.Empty<string>();
        private DateTime _lastPlaylistPutUtc;
        private DateTime _lastSegmentPutUtc;
        private DateTime _lastPlaylistPublishedUtc;
        private string _lastPutName = "<nenhum>";
        private string _lastPendingReason = "aguardando-primeiro-put";
        private int _segmentPutCount;
        private bool _loggedFirstSegmentPut;
        private int _generation;
        private readonly Dictionary<string, DateTime> _mirroredFiles = new(StringComparer.OrdinalIgnoreCase);

        public WindowRollingStream(Guid windowId, string outputDirectory, InMemoryHlsPutServer ingestServer)
        {
            _windowId = windowId;
            OutputDirectory = outputDirectory;
            _ingestServer = ingestServer;
            _routeToken = windowId.ToString("N");
            _lastTouchedUtc = DateTime.UtcNow;
        }

        public string OutputDirectory { get; }

        public void Touch()
        {
            _lastTouchedUtc = DateTime.UtcNow;
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

        public bool HasArtifact(string fileName)
        {
            return _artifactStore.Has(fileName);
        }

        public bool TryReadArtifact(string fileName, out byte[] payload)
        {
            return _artifactStore.TryRead(fileName, out payload);
        }

        public string GetDiagnosticStatus()
        {
            lock (_gate)
            {
                if (_artifactStore.Has("medium.m3u8"))
                {
                    return "playlist-publicada";
                }

                if (_pendingPlaylistBytes is not null)
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "playlist-pendente:{0}, segmentos={1}, ultimoPut={2}",
                        _lastPendingReason,
                        _pendingPlaylistSegments.Length,
                        _lastPutName);
                }

                if (_lastPlaylistPutUtc == default && _lastSegmentPutUtc == default)
                {
                    return "sem-put-do-ffmpeg";
                }

                if (_lastPlaylistPutUtc != default && _segmentPutCount == 0)
                {
                    return "playlist-chegou-sem-segmento";
                }

                if (_lastSegmentPutUtc != default)
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "segmentos-recebidos-sem-playlist: count={0}, ultimoPut={1}",
                        _segmentPutCount,
                        _lastPutName);
                }

                return "estado-indefinido";
            }
        }

        public int GetGeneration()
        {
            lock (_gate)
            {
                return _generation;
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

                    var firstFrame = await WaitForInitialFrameAsync(snapshotService, cancellationToken).ConfigureAwait(false);
                    if (firstFrame is null)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var audioFormat = audioCaptureService.GetAudioFormat(_windowId) ?? new AudioFormatInfo(44100, 2, 0);
                    lock (_gate)
                    {
                        _generation++;
                    }
                    ResetArtifactsForNewSession();
                    PrepareOutputDirectory();
                    using var session = new ContinuousEncoderSession(
                        _windowId,
                        ffmpegPath,
                        snapshotService,
                        audioCaptureService,
                        firstFrame,
                        audioFormat,
                        OutputDirectory,
                        cancellationToken);

                    using var mirrorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var mirrorTask = Task.Run(() => MirrorArtifactsFromDiskAsync(mirrorCancellation.Token), mirrorCancellation.Token);
                    try
                    {
                        AppLog.Write(
                            "PanelRollingHls",
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Encoder HLS em memoria iniciado: janela={0}, source={1}x{2}, target={3}, fps={4}, audio={5}/{6}",
                                _windowId.ToString("N"),
                                firstFrame.Width,
                                firstFrame.Height,
                                PrimaryRendition.Resolution,
                                PrimaryRendition.FrameRate,
                                audioFormat.SampleRate,
                                audioFormat.Channels));

                        await session.RunAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        mirrorCancellation.Cancel();
                        try
                        {
                            await mirrorTask.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        AppLog.Write("PanelRollingHls", $"Encoder continuo reiniciando para janela {_windowId:N}.");
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (AudioStreamRestartRequiredException ex)
                {
                    AppLog.Write("PanelRollingHls", $"Audio do painel reiniciou; recriando sessao HLS da janela {_windowId:N}: {ex.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Write("PanelRollingHls", $"Erro no encoder continuo da janela {_windowId:N}: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private void ResetArtifactsForNewSession()
        {
            lock (_gate)
            {
                _artifactStore.Clear();
                _pendingPlaylistBytes = null;
                _pendingPlaylistSegments = Array.Empty<string>();
                _lastPlaylistPutUtc = default;
                _lastSegmentPutUtc = default;
                _lastPlaylistPublishedUtc = default;
                _lastPutName = "<nenhum>";
                _lastPendingReason = "aguardando-primeiro-put";
                _segmentPutCount = 0;
                _loggedFirstSegmentPut = false;
                _loggedPlaylistReady = false;
                _mirroredFiles.Clear();
            }
        }

        private void PrepareOutputDirectory()
        {
            Directory.CreateDirectory(OutputDirectory);
            foreach (var filePath in Directory.GetFiles(OutputDirectory))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch
                {
                }
            }
        }

        private async Task MirrorArtifactsFromDiskAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    MirrorArtifactsFromDiskSnapshot();
                    await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private void MirrorArtifactsFromDiskSnapshot()
        {
            if (!Directory.Exists(OutputDirectory))
            {
                return;
            }

            foreach (var filePath in Directory.GetFiles(OutputDirectory, "*.*", SearchOption.TopDirectoryOnly))
            {
                var extension = Path.GetExtension(filePath);
                if (!extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var safeName = NormalizeArtifactName(Path.GetFileName(filePath));
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    continue;
                }

                DateTime lastWriteUtc;
                try
                {
                    lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
                }
                catch
                {
                    continue;
                }

                lock (_gate)
                {
                    if (_mirroredFiles.TryGetValue(safeName, out var mirroredAtUtc) && mirroredAtUtc == lastWriteUtc)
                    {
                        continue;
                    }
                }

                byte[] payload;
                try
                {
                    payload = File.ReadAllBytes(filePath);
                }
                catch
                {
                    continue;
                }

                if (payload.Length == 0)
                {
                    continue;
                }

                HandlePutAsync(safeName, payload, CancellationToken.None).GetAwaiter().GetResult();
                lock (_gate)
                {
                    _mirroredFiles[safeName] = lastWriteUtc;
                }
            }
        }

        private Task HandlePutAsync(string relativePath, byte[] payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeName = NormalizeArtifactName(Path.GetFileName(relativePath));
            if (string.IsNullOrWhiteSpace(safeName) || payload.Length == 0)
            {
                return Task.CompletedTask;
            }

            if (safeName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var text = Encoding.ASCII.GetString(payload);
                var normalized = NormalizePlaylist(text);
                lock (_gate)
                {
                    _pendingPlaylistBytes = Encoding.ASCII.GetBytes(normalized);
                    _pendingPlaylistSegments = ExtractSegmentNames(normalized);
                    _lastPlaylistPutUtc = DateTime.UtcNow;
                    _lastPutName = safeName;
                    _lastPendingReason = _pendingPlaylistSegments.Length == 0
                        ? "playlist-sem-segmentos"
                        : "aguardando-segmento-inicial";
                }

                TryPublishPendingPlaylist();
                return Task.CompletedTask;
            }

            if (safeName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                _artifactStore.Put(safeName, payload, SegmentTtl, removeAfterRead: false);
                lock (_gate)
                {
                    _lastSegmentPutUtc = DateTime.UtcNow;
                    _lastPutName = safeName;
                    _segmentPutCount++;
                }
                if (!_loggedFirstSegmentPut)
                {
                    _loggedFirstSegmentPut = true;
                    AppLog.Write("PanelRollingHls", $"Primeiro segmento HLS recebido em memoria para janela {_windowId:N}: {safeName}, bytes={payload.Length}");
                }
                TryPublishPendingPlaylist();
            }

            return Task.CompletedTask;
        }

        private Task<InMemoryHlsPutServer.GetResponse> HandleGetAsync(string relativePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeName = Path.GetFileName(relativePath);
            if (string.IsNullOrWhiteSpace(safeName) || !_artifactStore.TryRead(safeName, out var payload))
            {
                return Task.FromResult(new InMemoryHlsPutServer.GetResponse(404, Array.Empty<byte>(), "text/plain; charset=utf-8"));
            }

            var contentType = safeName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? "application/vnd.apple.mpegurl"
                : "video/mp2t";
            return Task.FromResult(new InMemoryHlsPutServer.GetResponse(200, payload, contentType));
        }

        private Task HandleDeleteAsync(string relativePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeName = Path.GetFileName(relativePath);
            if (!string.IsNullOrWhiteSpace(safeName))
            {
                _artifactStore.Remove(safeName);
            }

            return Task.CompletedTask;
        }

        private void TryPublishPendingPlaylist()
        {
            byte[]? playlistBytes = null;

            lock (_gate)
            {
                if (_pendingPlaylistBytes is null)
                {
                    return;
                }

                if (_pendingPlaylistSegments.Length == 0)
                {
                    playlistBytes = _pendingPlaylistBytes;
                    _pendingPlaylistBytes = null;
                    _pendingPlaylistSegments = Array.Empty<string>();
                    _lastPendingReason = "playlist-sem-segmentos";
                }
                else
                {
                    var readySegmentCount = 0;
                    foreach (var segmentName in _pendingPlaylistSegments)
                    {
                        if (_artifactStore.Has(segmentName))
                        {
                            readySegmentCount++;
                        }
                    }

                    if (readySegmentCount == 0)
                    {
                        _lastPendingReason = "aguardando-segmento-inicial";
                        return;
                    }

                    playlistBytes = _pendingPlaylistBytes;
                    _pendingPlaylistBytes = null;
                    _pendingPlaylistSegments = Array.Empty<string>();
                    _lastPendingReason = string.Format(CultureInfo.InvariantCulture, "segmento-inicial-pronto:{0}", readySegmentCount);
                }
            }

            _artifactStore.Put("medium.m3u8", playlistBytes, PlaylistTtl, removeAfterRead: false);
            _artifactStore.Put("index.m3u8", playlistBytes, PlaylistTtl, removeAfterRead: false);
            lock (_gate)
            {
                _lastPlaylistPublishedUtc = DateTime.UtcNow;
                _lastPendingReason = "playlist-publicada";
            }
            if (!_loggedPlaylistReady)
            {
                _loggedPlaylistReady = true;
                AppLog.Write("PanelRollingHls", $"Playlist HLS rolling pronta em memoria para janela {_windowId:N}");
            }
        }

        private static string NormalizePlaylist(string playlistText)
        {
            if (string.IsNullOrWhiteSpace(playlistText))
            {
                return "#EXTM3U\n";
            }

            var builder = new StringBuilder();
            using var reader = new StringReader(playlistText.Replace("\r\n", "\n"));
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    builder.AppendLine(Path.GetFileName(line));
                }
                else
                {
                    builder.AppendLine(line);
                }
            }

            return builder.ToString();
        }

        private static string[] ExtractSegmentNames(string playlistText)
        {
            var names = new System.Collections.Generic.List<string>();
            using var reader = new StringReader(playlistText);
            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                names.Add(NormalizeArtifactName(Path.GetFileName(trimmed)));
            }

            return names.ToArray();
        }

        private static string NormalizeArtifactName(string? name)
        {
            var safeName = name?.Trim() ?? string.Empty;
            if (safeName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                safeName = safeName.Substring(0, safeName.Length - 4);
            }

            return safeName;
        }

        private async Task<CachedBitmapFrame?> WaitForInitialFrameAsync(BrowserSnapshotService snapshotService, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 40 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                var frame = await snapshotService.CaptureBitmapFrameAsync(_windowId, cancellationToken).ConfigureAwait(false);
                if (frame is not null && frame.Pixels.Length >= 4096)
                {
                    return frame;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            return null;
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

            _artifactStore.Clear();
        }
    }

    private sealed class ContinuousEncoderSession : IDisposable
    {
        private static readonly TimeSpan VideoFrameInterval = TimeSpan.FromMilliseconds(1000.0 / 18.0);
        private static readonly TimeSpan AudioChunkInterval = TimeSpan.FromMilliseconds(Tuning.AudioChunkIntervalMs);

        private readonly Guid _windowId;
        private readonly string _ffmpegPath;
        private readonly BrowserSnapshotService _snapshotService;
        private readonly BrowserAudioCaptureService _audioCaptureService;
        private readonly CachedBitmapFrame _initialFrame;
        private readonly AudioFormatInfo _audioFormat;
        private readonly string _outputDirectory;
        private readonly CancellationToken _cancellationToken;
        private readonly string _videoPipeName;
        private readonly string _audioPipeName;
        private readonly NamedPipeServerStream _videoPipe;
        private readonly NamedPipeServerStream _audioPipe;

        private Process? _ffmpegProcess;
        private bool _lastAudioChunkUsedSilence;
        private int _consecutiveSilentAudioChunks;
        private int _lastAudioGeneration = -1;
        private byte[] _pendingAudioBytes = Array.Empty<byte>();
        private bool _hasProducedRealAudio;

        public ContinuousEncoderSession(
            Guid windowId,
            string ffmpegPath,
            BrowserSnapshotService snapshotService,
            BrowserAudioCaptureService audioCaptureService,
            CachedBitmapFrame initialFrame,
            AudioFormatInfo audioFormat,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            _windowId = windowId;
            _ffmpegPath = ffmpegPath;
            _snapshotService = snapshotService;
            _audioCaptureService = audioCaptureService;
            _initialFrame = initialFrame;
            _audioFormat = audioFormat;
            _outputDirectory = outputDirectory;
            _cancellationToken = cancellationToken;
            _videoPipeName = "superpainel_video_" + windowId.ToString("N");
            _audioPipeName = "superpainel_audio_" + windowId.ToString("N");
            _videoPipe = new NamedPipeServerStream(_videoPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _audioPipe = new NamedPipeServerStream(_audioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        public async Task RunAsync()
        {
            var ffmpegArguments = BuildFfmpegArguments(_initialFrame, _audioFormat, _videoPipeName, _audioPipeName, _outputDirectory);
            var waitVideoConnectionTask = Task.Run(() => _videoPipe.WaitForConnection(), _cancellationToken);
            var waitAudioConnectionTask = Task.Run(() => _audioPipe.WaitForConnection(), _cancellationToken);

            _ffmpegProcess = Process.Start(new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = ffmpegArguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            });

            if (_ffmpegProcess is null)
            {
                throw new InvalidOperationException("Nao foi possivel iniciar o ffmpeg para HLS rolling em memoria.");
            }

            await EnsurePipeConnectedAsync(waitVideoConnectionTask, "video", ffmpegArguments).ConfigureAwait(false);
            var videoPumpTask = Task.Run(PumpVideoAsync, _cancellationToken);
            await EnsurePipeConnectedAsync(waitAudioConnectionTask, "audio", ffmpegArguments).ConfigureAwait(false);
            var stderrPumpTask = Task.Run(PumpFfmpegStderrAsync, _cancellationToken);
            var audioPumpTask = Task.Run(PumpAudioAsync, _cancellationToken);
            var processTask = Task.Run(() => _ffmpegProcess.WaitForExit(), _cancellationToken);

            await Task.WhenAny(processTask, videoPumpTask, audioPumpTask).ConfigureAwait(false);
            if (!_ffmpegProcess.HasExited)
            {
                try
                {
                    _ffmpegProcess.Kill();
                }
                catch
                {
                }
            }

            await processTask.ConfigureAwait(false);
            try
            {
                await videoPumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            try
            {
                await audioPumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            try
            {
                await stderrPumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            if (!_cancellationToken.IsCancellationRequested && _ffmpegProcess.ExitCode != 0)
            {
                var stderr = await _ffmpegProcess.StandardError.ReadToEndAsync().ConfigureAwait(false);
                AppLog.Write("PanelRollingHls", $"ffmpeg HLS continuo saiu com erro para janela {_windowId:N}: code={_ffmpegProcess.ExitCode}, error={stderr}");
            }
        }

        private async Task EnsurePipeConnectedAsync(Task pipeTask, string label, string ffmpegArguments)
        {
            var winner = await Task.WhenAny(pipeTask, Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken)).ConfigureAwait(false);
            if (winner == pipeTask)
            {
                await pipeTask.ConfigureAwait(false);
                return;
            }

            var stderr = _ffmpegProcess is not null && _ffmpegProcess.HasExited
                ? await _ffmpegProcess.StandardError.ReadToEndAsync().ConfigureAwait(false)
                : $"ffmpeg nao conectou na pipe de {label} dentro do prazo.";
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "ffmpeg nao conectou na pipe de {0}: janela={1}, args={2}, stderr={3}",
                    label,
                    _windowId.ToString("N"),
                    ffmpegArguments,
                    stderr));
        }

        private async Task PumpVideoAsync()
        {
            var frame = _initialFrame;
            var sourceWidth = _initialFrame.Width;
            var sourceHeight = _initialFrame.Height;

            while (!_cancellationToken.IsCancellationRequested && _videoPipe.IsConnected)
            {
                var startedAtUtc = DateTime.UtcNow;
                try
                {
                    var latestFrame = await _snapshotService.CaptureBitmapFrameAsync(_windowId, _cancellationToken).ConfigureAwait(false);
                    if (latestFrame is not null &&
                        latestFrame.Width == sourceWidth &&
                        latestFrame.Height == sourceHeight &&
                        latestFrame.Pixels.Length == frame.Pixels.Length)
                    {
                        frame = latestFrame;
                    }

                    await _videoPipe.WriteAsync(frame.Pixels, 0, frame.Pixels.Length, _cancellationToken).ConfigureAwait(false);
                    await _videoPipe.FlushAsync(_cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                var delay = VideoFrameInterval - (DateTime.UtcNow - startedAtUtc);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task PumpAudioAsync()
        {
            var bytesPerSample = 2;
            var bytesPerFrame = Math.Max(1, _audioFormat.Channels) * bytesPerSample;
            var targetChunkBytes = Math.Max(
                bytesPerFrame,
                ((int)Math.Round(_audioFormat.SampleRate * AudioChunkInterval.TotalSeconds) * bytesPerFrame / bytesPerFrame) * bytesPerFrame);
            var cursor = 0L;

            while (!_cancellationToken.IsCancellationRequested && _audioPipe.IsConnected)
            {
                var startedAtUtc = DateTime.UtcNow;
                try
                {
                    var bytesToWrite = BuildAudioChunk(targetChunkBytes, ref cursor);

                    await _audioPipe.WriteAsync(bytesToWrite, 0, bytesToWrite.Length, _cancellationToken).ConfigureAwait(false);
                    await _audioPipe.FlushAsync(_cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                var delay = AudioChunkInterval - (DateTime.UtcNow - startedAtUtc);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private byte[] BuildAudioChunk(int targetChunkBytes, ref long cursor)
        {
            while (_pendingAudioBytes.Length < targetChunkBytes)
            {
                var requestedBytes = Math.Max(targetChunkBytes - _pendingAudioBytes.Length, targetChunkBytes);
                var chunk = _audioCaptureService.ReadPcmChunk(_windowId, cursor, requestedBytes);
                if (chunk.Bytes.Length <= 0)
                {
                    break;
                }

                cursor = chunk.NextCursor;
                if (_hasProducedRealAudio &&
                    _lastAudioGeneration != -1 &&
                    chunk.Generation > 0 &&
                    chunk.Generation != _lastAudioGeneration)
                {
                    throw new AudioStreamRestartRequiredException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "geracao de audio mudou de {0} para {1}",
                            _lastAudioGeneration,
                            chunk.Generation));
                }

                _lastAudioGeneration = chunk.Generation;
                AppendPendingAudio(chunk.Bytes);
            }

            if (_pendingAudioBytes.Length >= targetChunkBytes)
            {
                var bytesToWrite = ConsumePendingAudio(targetChunkBytes);
                if (_lastAudioChunkUsedSilence)
                {
                    AppLog.Write(
                        "PanelRollingHls",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Audio rolling retomou audio real: janela={0}, silenceChunks={1}, generation={2}",
                            _windowId.ToString("N"),
                            _consecutiveSilentAudioChunks,
                            _lastAudioGeneration));
                }

                _lastAudioChunkUsedSilence = false;
                _consecutiveSilentAudioChunks = 0;
                _hasProducedRealAudio = true;
                return bytesToWrite;
            }

            if (_pendingAudioBytes.Length > 0)
            {
                var bytesToWrite = new byte[targetChunkBytes];
                Buffer.BlockCopy(_pendingAudioBytes, 0, bytesToWrite, 0, _pendingAudioBytes.Length);
                _pendingAudioBytes = Array.Empty<byte>();
                _lastAudioChunkUsedSilence = false;
                _consecutiveSilentAudioChunks = 0;
                _hasProducedRealAudio = true;
                return bytesToWrite;
            }

            if (_hasProducedRealAudio && _lastAudioGeneration > 0)
            {
                var currentFormat = _audioCaptureService.GetAudioFormat(_windowId);
                if (currentFormat is not null &&
                    currentFormat.Generation > 0 &&
                    currentFormat.Generation != _lastAudioGeneration)
                {
                    throw new AudioStreamRestartRequiredException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "formato de audio reportou nova geracao {0} apos silencio (anterior={1})",
                            currentFormat.Generation,
                            _lastAudioGeneration));
                }
            }

            _lastAudioChunkUsedSilence = true;
            _consecutiveSilentAudioChunks++;
            return new byte[targetChunkBytes];
        }

        private void AppendPendingAudio(byte[] chunkBytes)
        {
            if (chunkBytes.Length == 0)
            {
                return;
            }

            if (_pendingAudioBytes.Length == 0)
            {
                _pendingAudioBytes = chunkBytes;
                return;
            }

            var combined = new byte[_pendingAudioBytes.Length + chunkBytes.Length];
            Buffer.BlockCopy(_pendingAudioBytes, 0, combined, 0, _pendingAudioBytes.Length);
            Buffer.BlockCopy(chunkBytes, 0, combined, _pendingAudioBytes.Length, chunkBytes.Length);
            _pendingAudioBytes = combined;
        }

        private byte[] ConsumePendingAudio(int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(_pendingAudioBytes, 0, slice, 0, count);
            var remaining = _pendingAudioBytes.Length - count;
            if (remaining <= 0)
            {
                _pendingAudioBytes = Array.Empty<byte>();
                return slice;
            }

            var next = new byte[remaining];
            Buffer.BlockCopy(_pendingAudioBytes, count, next, 0, remaining);
            _pendingAudioBytes = next;
            return slice;
        }

        private async Task PumpFfmpegStderrAsync()
        {
            if (_ffmpegProcess is null)
            {
                return;
            }

            while (!_ffmpegProcess.HasExited)
            {
                var line = await _ffmpegProcess.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    AppLog.Write("PanelRollingHls", $"ffmpeg: {line.Trim()}");
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (_ffmpegProcess is not null && !_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill();
                }
            }
            catch
            {
            }

            try
            {
                _videoPipe.Dispose();
            }
            catch
            {
            }

            try
            {
                _audioPipe.Dispose();
            }
            catch
            {
            }

            try
            {
                _ffmpegProcess?.Dispose();
            }
            catch
            {
            }
        }

        private static string BuildFfmpegArguments(CachedBitmapFrame frame, AudioFormatInfo audioFormat, string videoPipeName, string audioPipeName, string outputDirectory)
        {
            var segmentDurationSeconds = Tuning.HlsSegmentDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            var forceKeyFrames = $"expr:gte(t,n_forced*{segmentDurationSeconds})";
            Directory.CreateDirectory(outputDirectory);
            var playlistPath = Path.Combine(outputDirectory, "medium.m3u8");
            var segmentPathTemplate = Path.Combine(outputDirectory, "segment-%06d.ts");
            return string.Format(
                CultureInfo.InvariantCulture,
                "-hide_banner -loglevel warning -fflags +genpts -thread_queue_size 512 -f rawvideo -pix_fmt bgra -video_size {0}x{1} -framerate {2} -i \"\\\\.\\pipe\\{3}\" -thread_queue_size 512 -f s16le -ar {4} -ac {5} -i \"\\\\.\\pipe\\{6}\" -map 0:v:0 -map 1:a:0 -vf \"{7}\" -c:v libx264 -preset veryfast -tune zerolatency -profile:v baseline -level 3.1 -x264-params \"keyint={2}:min-keyint={2}:scenecut=0:force-cfr=1:aud=1:nal-hrd=cbr\" -force_key_frames \"{11}\" -b:v {8} -maxrate {8} -bufsize {9} -pix_fmt yuv420p -bsf:v h264_metadata=aud=insert -c:a aac -b:a {10} -ar 48000 -ac 2 -af aresample=async=1:first_pts=0:min_hard_comp=0.100 -muxpreload 0 -muxdelay 0 -f hls -hls_time {12} -hls_list_size {13} -hls_allow_cache 0 -hls_flags delete_segments+append_list+omit_endlist+independent_segments+program_date_time -hls_segment_type mpegts -hls_segment_filename \"{14}\" \"{15}\"",
                frame.Width,
                frame.Height,
                PrimaryRendition.FrameRate,
                videoPipeName,
                audioFormat.SampleRate,
                audioFormat.Channels,
                audioPipeName,
                BuildVideoFilter(PrimaryRendition.Resolution),
                PrimaryRendition.VideoBitrate.ToString(CultureInfo.InvariantCulture),
                (PrimaryRendition.VideoBitrate * 2).ToString(CultureInfo.InvariantCulture),
                PrimaryRendition.AudioBitrate.ToString(CultureInfo.InvariantCulture),
                forceKeyFrames,
                segmentDurationSeconds,
                Tuning.HlsPlaylistSize.ToString(CultureInfo.InvariantCulture),
                segmentPathTemplate,
                playlistPath);
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

    }

    private sealed class RenditionProfile
    {
        public RenditionProfile(string name, string resolution, int videoBitrate, int audioBitrate, int frameRate)
        {
            Name = name;
            Resolution = resolution;
            VideoBitrate = videoBitrate;
            AudioBitrate = audioBitrate;
            FrameRate = frameRate;
        }

        public string Name { get; }

        public string Resolution { get; }

        public int VideoBitrate { get; }

        public int AudioBitrate { get; }

        public int FrameRate { get; }
    }

    private sealed class AudioStreamRestartRequiredException : Exception
    {
        public AudioStreamRestartRequiredException(string message)
            : base(message)
        {
        }
    }
}
