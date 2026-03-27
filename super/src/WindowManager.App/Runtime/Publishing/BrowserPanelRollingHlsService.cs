using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime.Publishing;

public sealed class BrowserPanelRollingHlsService
{
    private static readonly TimeSpan SegmentDuration = TimeSpan.FromSeconds(0.75);
    private const int PlaylistSize = 3;
    private static readonly bool UseSyntheticAudio = string.Equals(Environment.GetEnvironmentVariable("SUPERPAINEL_SYNTH_AUDIO"), "1", StringComparison.OrdinalIgnoreCase);
    private static readonly RenditionProfile PrimaryRendition = new("medium", "854x480", 850_000, 96_000, 18);

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

        path = Path.Combine(stream.OutputDirectory, "medium.m3u8");
        return File.Exists(path);
    }

    public bool TryGetMasterPlaylistPath(Guid windowId, out string path)
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

        path = Path.Combine(stream.OutputDirectory, "master.m3u8");
        return File.Exists(path);
    }

    public bool TryGetOutputFilePath(Guid windowId, string fileName, out string path)
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
        private readonly Guid _windowId;
        private CancellationTokenSource? _cancellation;
        private Task? _worker;
        private DateTime _lastTouchedUtc;

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

        public void EnsureStarted(BrowserSnapshotService snapshotService, BrowserAudioCaptureService audioCaptureService, string ffmpegPath)
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
                    ResetOutputDirectory();
                    WriteMasterPlaylist();

                    using var session = new ContinuousEncoderSession(
                        _windowId,
                        OutputDirectory,
                        ffmpegPath,
                        snapshotService,
                        audioCaptureService,
                        firstFrame,
                        audioFormat,
                        cancellationToken);

                    AppLog.Write(
                        "PanelRollingHls",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Encoder HLS continuo iniciado: janela={0}, source={1}x{2}, target={3}, fps={4}, audio={5}/{6}",
                            _windowId.ToString("N"),
                            firstFrame.Width,
                            firstFrame.Height,
                            PrimaryRendition.Resolution,
                            PrimaryRendition.FrameRate,
                            audioFormat.SampleRate,
                            audioFormat.Channels));

                    await session.RunAsync().ConfigureAwait(false);

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        AppLog.Write("PanelRollingHls", $"Encoder HLS continuo reiniciando para janela {_windowId:N}.");
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                    }
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

        private void ResetOutputDirectory()
        {
            Directory.CreateDirectory(OutputDirectory);
            foreach (var file in Directory.GetFiles(OutputDirectory, "*.m3u8"))
            {
                TryDelete(file);
            }

            foreach (var file in Directory.GetFiles(OutputDirectory, "*.ts"))
            {
                TryDelete(file);
            }
        }

        private void WriteMasterPlaylist()
        {
            var builder = new StringBuilder();
            builder.AppendLine("#EXTM3U");
            builder.AppendLine("#EXT-X-VERSION:3");
            builder.AppendLine("#EXT-X-INDEPENDENT-SEGMENTS");
            builder.AppendLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "#EXT-X-STREAM-INF:BANDWIDTH={0},AVERAGE-BANDWIDTH={1},CODECS=\"mp4a.40.2,avc1.42E01E\",RESOLUTION={2},FRAME-RATE={3},CLOSED-CAPTIONS=NONE",
                    PrimaryRendition.VideoBitrate + PrimaryRendition.AudioBitrate,
                    PrimaryRendition.VideoBitrate + PrimaryRendition.AudioBitrate,
                    PrimaryRendition.Resolution,
                    PrimaryRendition.FrameRate.ToString(CultureInfo.InvariantCulture)));
            builder.AppendLine("medium.m3u8");
            File.WriteAllText(Path.Combine(OutputDirectory, "master.m3u8"), builder.ToString(), Encoding.ASCII);
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
    }

    private sealed class ContinuousEncoderSession : IDisposable
    {
        private static readonly TimeSpan VideoFrameInterval = TimeSpan.FromMilliseconds(1000.0 / 18.0);
        private static readonly TimeSpan AudioChunkInterval = TimeSpan.FromMilliseconds(100);

        private readonly Guid _windowId;
        private readonly string _outputDirectory;
        private readonly string _ffmpegPath;
        private readonly BrowserSnapshotService _snapshotService;
        private readonly BrowserAudioCaptureService _audioCaptureService;
        private readonly CancellationToken _cancellationToken;
        private readonly CachedBitmapFrame _initialFrame;
        private readonly AudioFormatInfo _audioFormat;
        private readonly string _videoPipeName;
        private readonly string _audioPipeName;
        private readonly NamedPipeServerStream _videoPipe;
        private readonly NamedPipeServerStream _audioPipe;

        private Process? _ffmpegProcess;
        private bool _lastAudioChunkUsedSilence;
        private int _consecutiveSilentAudioChunks;
        private int _consecutiveRealAudioChunks;
        private int _lastAudioGeneration = -1;

        public ContinuousEncoderSession(
            Guid windowId,
            string outputDirectory,
            string ffmpegPath,
            BrowserSnapshotService snapshotService,
            BrowserAudioCaptureService audioCaptureService,
            CachedBitmapFrame initialFrame,
            AudioFormatInfo audioFormat,
            CancellationToken cancellationToken)
        {
            _windowId = windowId;
            _outputDirectory = outputDirectory;
            _ffmpegPath = ffmpegPath;
            _snapshotService = snapshotService;
            _audioCaptureService = audioCaptureService;
            _initialFrame = initialFrame;
            _audioFormat = audioFormat;
            _cancellationToken = cancellationToken;
            _videoPipeName = "superpainel_video_" + windowId.ToString("N");
            _audioPipeName = "superpainel_audio_" + windowId.ToString("N");
            _videoPipe = new NamedPipeServerStream(_videoPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _audioPipe = new NamedPipeServerStream(_audioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        public async Task RunAsync()
        {
            var ffmpegArguments = BuildFfmpegArguments(_initialFrame, _audioFormat, _outputDirectory, _videoPipeName, _audioPipeName);
            var waitVideoConnectionTask = Task.Run(() => _videoPipe.WaitForConnection(), _cancellationToken);
            var waitAudioConnectionTask = Task.Run(() => _audioPipe.WaitForConnection(), _cancellationToken);

            _ffmpegProcess = Process.Start(new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = ffmpegArguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });

            if (_ffmpegProcess is null)
            {
                throw new InvalidOperationException("Nao foi possivel iniciar o ffmpeg para HLS continuo.");
            }

            var videoConnectionWinner = await Task.WhenAny(waitVideoConnectionTask, Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken)).ConfigureAwait(false);
            if (videoConnectionWinner != waitVideoConnectionTask)
            {
                var stderr = _ffmpegProcess.HasExited
                    ? await _ffmpegProcess.StandardError.ReadToEndAsync().ConfigureAwait(false)
                    : "ffmpeg nao conectou na pipe de video dentro do prazo.";
                AppLog.Write(
                    "PanelRollingHls",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "ffmpeg continuo nao conectou: janela={0}, args={1}, stderr={2}",
                        _windowId.ToString("N"),
                        ffmpegArguments,
                        stderr));
                return;
            }

            var videoPumpTask = Task.Run(PumpVideoAsync, _cancellationToken);

            var audioConnectionWinner = await Task.WhenAny(waitAudioConnectionTask, Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken)).ConfigureAwait(false);
            if (audioConnectionWinner != waitAudioConnectionTask)
            {
                var stderr = _ffmpegProcess.HasExited
                    ? await _ffmpegProcess.StandardError.ReadToEndAsync().ConfigureAwait(false)
                    : "ffmpeg nao conectou na pipe de audio dentro do prazo.";
                AppLog.Write(
                    "PanelRollingHls",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "ffmpeg continuo nao conectou audio: janela={0}, stderr={1}",
                        _windowId.ToString("N"),
                        stderr));
                return;
            }

            var audioPumpTask = Task.Run(PumpAudioAsync, _cancellationToken);
            var processTask = Task.Run(() => _ffmpegProcess.WaitForExit(), _cancellationToken);

            var completed = await Task.WhenAny(videoPumpTask, audioPumpTask, processTask).ConfigureAwait(false);
            if (completed == processTask && !_cancellationToken.IsCancellationRequested)
            {
                var stderr = await _ffmpegProcess.StandardError.ReadToEndAsync().ConfigureAwait(false);
                AppLog.Write("PanelRollingHls", $"ffmpeg continuo saiu para janela {_windowId:N}: code={_ffmpegProcess.ExitCode}, error={stderr}");
            }

            Dispose();
        }

        private async Task PumpVideoAsync()
        {
            var frame = _initialFrame;
            var frameIndex = 0;
            var sourceWidth = _initialFrame.Width;
            var sourceHeight = _initialFrame.Height;

            while (!_cancellationToken.IsCancellationRequested && _videoPipe.IsConnected)
            {
                var startedAtUtc = DateTime.UtcNow;
                try
                {
                    var latestFrame = await _snapshotService.CaptureBitmapFrameAsync(_windowId, _cancellationToken).ConfigureAwait(false);
                    if (latestFrame is not null && latestFrame.Width == sourceWidth && latestFrame.Height == sourceHeight && latestFrame.Pixels.Length == frame.Pixels.Length)
                    {
                        frame = latestFrame;
                    }

                    await _videoPipe.WriteAsync(frame.Pixels, 0, frame.Pixels.Length, _cancellationToken).ConfigureAwait(false);
                    await _videoPipe.FlushAsync(_cancellationToken).ConfigureAwait(false);
                    frameIndex++;
                    if (frameIndex % 90 == 0)
                    {
                        AppLog.Write(
                            "PanelRollingHls",
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Video continuo alimentado: janela={0}, frames={1}, frameBytes={2}, source={3}x{4}",
                                _windowId.ToString("N"),
                                frameIndex,
                                frame.Pixels.Length,
                                sourceWidth,
                                sourceHeight));
                    }
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
            var bytesPerChunk = AlignAudioBytes(_audioFormat.SampleRate * _audioFormat.Channels * 2 / 10, _audioFormat.Channels);
            var silence = new byte[bytesPerChunk];
            var cursor = 0L;
            var chunkIndex = 0;

            while (!_cancellationToken.IsCancellationRequested && _audioPipe.IsConnected)
            {
                var startedAtUtc = DateTime.UtcNow;
                try
                {
                    byte[] bytesToWrite;
                    bool usedSilence;
                    if (UseSyntheticAudio)
                    {
                        bytesToWrite = BuildSyntheticPcmChunk(_audioFormat.SampleRate, _audioFormat.Channels, bytesPerChunk, chunkIndex);
                        usedSilence = false;
                    }
                    else
                    {
                        var chunk = _audioCaptureService.ReadPcmChunk(_windowId, cursor, bytesPerChunk);
                        cursor = chunk.NextCursor;
                        if (_lastAudioGeneration != -1 && chunk.Generation != 0 && chunk.Generation != _lastAudioGeneration)
                        {
                            AppLog.Write(
                                "PanelRollingHls",
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Audio continuo trocou de geracao: janela={0}, anterior={1}, atual={2}, cursor={3}",
                                    _windowId.ToString("N"),
                                    _lastAudioGeneration,
                                    chunk.Generation,
                                    cursor));
                            bytesToWrite = silence;
                            usedSilence = true;
                        }
                        else
                        {
                            bytesToWrite = NormalizeAudioChunk(chunk.Bytes, silence, _audioFormat.Channels);
                            usedSilence = chunk.Bytes.Length == 0;
                        }

                        if (chunk.Generation != 0)
                        {
                            _lastAudioGeneration = chunk.Generation;
                        }
                    }

                    if (usedSilence)
                    {
                        _consecutiveSilentAudioChunks++;
                        _consecutiveRealAudioChunks = 0;
                        if (!_lastAudioChunkUsedSilence || _consecutiveSilentAudioChunks % 20 == 0)
                        {
                            AppLog.Write(
                                "PanelRollingHls",
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Audio continuo em silencio: janela={0}, chunksSilencio={1}, cursor={2}, bytes={3}",
                                    _windowId.ToString("N"),
                                    _consecutiveSilentAudioChunks,
                                    cursor,
                                    bytesToWrite.Length));
                        }
                    }
                    else
                    {
                        _consecutiveRealAudioChunks++;
                        if (_lastAudioChunkUsedSilence)
                        {
                            AppLog.Write(
                                "PanelRollingHls",
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Audio continuo voltou a dados reais: janela={0}, cursor={1}, bytes={2}",
                                    _windowId.ToString("N"),
                                    cursor,
                                    bytesToWrite.Length));
                        }

                        _consecutiveSilentAudioChunks = 0;
                    }

                    _lastAudioChunkUsedSilence = usedSilence;

                    await _audioPipe.WriteAsync(bytesToWrite, 0, bytesToWrite.Length, _cancellationToken).ConfigureAwait(false);
                    await _audioPipe.FlushAsync(_cancellationToken).ConfigureAwait(false);
                    chunkIndex++;
                    if (chunkIndex % 50 == 0)
                    {
                        AppLog.Write(
                            "PanelRollingHls",
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Audio continuo alimentado: janela={0}, chunks={1}, bytes={2}, sampleRate={3}, channels={4}",
                                _windowId.ToString("N"),
                                chunkIndex,
                                bytesToWrite.Length,
                                _audioFormat.SampleRate,
                                _audioFormat.Channels));
                    }
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

        private static string BuildFfmpegArguments(CachedBitmapFrame frame, AudioFormatInfo audioFormat, string outputDirectory, string videoPipeName, string audioPipeName)
        {
            var segmentPattern = Path.Combine(outputDirectory, "segment-%06d.ts");
            var playlistPath = Path.Combine(outputDirectory, "medium.m3u8");
            return string.Format(
                CultureInfo.InvariantCulture,
                "-hide_banner -loglevel error -y -fflags +genpts -thread_queue_size 512 -f rawvideo -pix_fmt bgra -video_size {0}x{1} -framerate {2} -i \"\\\\.\\pipe\\{3}\" -thread_queue_size 512 -f s16le -ar {4} -ac {5} -i \"\\\\.\\pipe\\{6}\" -map 0:v:0 -map 1:a:0 -vf \"{7}\" -c:v libx264 -preset veryfast -tune zerolatency -profile:v baseline -level 3.1 -g {2} -keyint_min {2} -sc_threshold 0 -b:v {8} -maxrate {8} -bufsize {9} -pix_fmt yuv420p -c:a aac -b:a {10} -ar 48000 -ac 2 -af aresample=async=1:first_pts=0:min_hard_comp=0.100 -f hls -hls_time {11:0.###} -hls_list_size {12} -hls_flags delete_segments+append_list+independent_segments+split_by_time+omit_endlist -hls_segment_filename \"{13}\" \"{14}\"",
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
                SegmentDuration.TotalSeconds,
                PlaylistSize,
                segmentPattern,
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

        private static byte[] NormalizeAudioChunk(byte[] bytes, byte[] silence, int channels)
        {
            if (bytes is null || bytes.Length == 0)
            {
                return silence;
            }

            var alignedLength = AlignAudioBytes(bytes.Length, channels);
            if (alignedLength <= 0)
            {
                return silence;
            }

            if (alignedLength >= silence.Length)
            {
                if (alignedLength == bytes.Length)
                {
                    return bytes;
                }

                var aligned = new byte[alignedLength];
                Buffer.BlockCopy(bytes, 0, aligned, 0, Math.Min(bytes.Length, alignedLength));
                return aligned;
            }

            var padded = new byte[silence.Length];
            Buffer.BlockCopy(bytes, 0, padded, 0, alignedLength);
            return padded;
        }

        private static int AlignAudioBytes(int byteCount, int channels)
        {
            var blockAlign = Math.Max(1, channels) * 2;
            return byteCount - (byteCount % blockAlign);
        }

        private static byte[] BuildSyntheticPcmChunk(int sampleRate, int channels, int byteCount, int chunkIndex)
        {
            var blockAlign = Math.Max(1, channels) * 2;
            var sampleCount = byteCount / blockAlign;
            var buffer = new byte[sampleCount * blockAlign];
            var phaseOffset = chunkIndex * sampleCount;

            var writeIndex = 0;
            for (var frameIndex = 0; frameIndex < sampleCount; frameIndex++)
            {
                var sample = (short)Math.Round(Math.Sin((2.0 * Math.PI * 440.0 * (phaseOffset + frameIndex)) / sampleRate) * (short.MaxValue * 0.2));
                for (var channelIndex = 0; channelIndex < channels; channelIndex++)
                {
                    buffer[writeIndex++] = (byte)(sample & 0xFF);
                    buffer[writeIndex++] = (byte)((sample >> 8) & 0xFF);
                }
            }

            return buffer;
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
