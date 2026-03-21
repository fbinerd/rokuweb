using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime.Publishing;

public sealed class BrowserPanelHlsService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1.0);
    private readonly BrowserSnapshotService _snapshotService;
    private readonly BrowserAudioCaptureService _audioCaptureService;
    private readonly string _rootDirectory;
    private readonly string _ffmpegPath;
    private readonly ConcurrentDictionary<Guid, WindowPanelHlsStream> _streams = new();

    public BrowserPanelHlsService(BrowserSnapshotService snapshotService, BrowserAudioCaptureService audioCaptureService)
    {
        _snapshotService = snapshotService;
        _audioCaptureService = audioCaptureService;
        _rootDirectory = Path.Combine(AppDataPaths.Root, "panel-hls");
        Directory.CreateDirectory(_rootDirectory);
        _ffmpegPath = ResolveFfmpegPath();
        if (!string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            AppLog.Write("PanelHls", $"ffmpeg detectado para HLS de painel: {_ffmpegPath}");
        }
        else
        {
            AppLog.Write("PanelHls", "ffmpeg nao encontrado. HLS unificado de painel desabilitado.");
        }
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_ffmpegPath);

    public void EnsureWindow(Guid windowId)
    {
        if (!IsAvailable)
        {
            return;
        }

        var stream = _streams.GetOrAdd(windowId, id => new WindowPanelHlsStream(id, Path.Combine(_rootDirectory, id.ToString("N"))));
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

                var process = Process.Start(new ProcessStartInfo
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

    private sealed class WindowPanelHlsStream : IDisposable
    {
        private readonly object _gate = new();
        private readonly Guid _windowId;
        private CancellationTokenSource? _cancellation;
        private Task? _worker;
        private DateTime _lastTouchedUtc;
        private bool _loggedPlaylistReady;

        public WindowPanelHlsStream(Guid windowId, string outputDirectory)
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
                    var wavBytes = audioCaptureService.CaptureWaveSnapshot(_windowId, TimeSpan.FromSeconds(12));
                    if (jpegBytes is null || jpegBytes.Length < 1024 || wavBytes is null || wavBytes.Length < 4096)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var imagePath = Path.Combine(OutputDirectory, "frame.jpg");
                    var audioPath = Path.Combine(OutputDirectory, "audio.wav");
                    File.WriteAllBytes(imagePath, jpegBytes);
                    File.WriteAllBytes(audioPath, wavBytes);

                    foreach (var stale in Directory.GetFiles(OutputDirectory, "*.ts"))
                    {
                        TryDelete(stale);
                    }

                    var playlistPath = Path.Combine(OutputDirectory, "index.m3u8");
                    var segmentPattern = Path.Combine(OutputDirectory, "segment-%03d.ts");
                    var arguments = string.Format(
                        CultureInfo.InvariantCulture,
                        "-hide_banner -loglevel error -y -loop 1 -framerate 24 -i \"{0}\" -i \"{1}\" -shortest -c:v libx264 -preset ultrafast -tune stillimage -pix_fmt yuv420p -c:a aac -b:a 128k -ar 44100 -ac 2 -f hls -hls_time 1 -hls_list_size 6 -hls_flags delete_segments+omit_endlist+independent_segments -hls_segment_filename \"{2}\" \"{3}\"",
                        imagePath,
                        audioPath,
                        segmentPattern,
                        playlistPath);

                    var process = Process.Start(new ProcessStartInfo
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
                        await Task.Delay(RefreshInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
                    if (process.ExitCode == 0 && File.Exists(playlistPath) && !_loggedPlaylistReady)
                    {
                        _loggedPlaylistReady = true;
                        AppLog.Write("PanelHls", $"Playlist HLS unificada pronta para janela {_windowId:N}");
                    }
                    else if (process.ExitCode != 0)
                    {
                        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                        AppLog.Write("PanelHls", $"ffmpeg falhou na janela {_windowId:N}: {error}");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Write("PanelHls", $"Erro no gerador HLS unificado da janela {_windowId:N}: {ex.Message}");
                }

                await Task.Delay(RefreshInterval, cancellationToken).ConfigureAwait(false);
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
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
