using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime.Publishing;

public sealed class ExperimentalAvMediaService : IDisposable
{
    private readonly bool _enabled;
    private readonly string _rootDirectory;
    private readonly string _ffmpegPath;
    private readonly BrowserSnapshotService _browserSnapshotService;
    private readonly BrowserAudioCaptureService _browserAudioCaptureService;
    private readonly ConcurrentDictionary<Guid, Task> _windowBuilds = new ConcurrentDictionary<Guid, Task>();

    public ExperimentalAvMediaService(BrowserSnapshotService browserSnapshotService, BrowserAudioCaptureService browserAudioCaptureService)
    {
        _browserSnapshotService = browserSnapshotService;
        _browserAudioCaptureService = browserAudioCaptureService;
        _enabled = string.Equals(Environment.GetEnvironmentVariable("SUPERPAINEL_EXPERIMENT_WEBRTC_AV"), "1", StringComparison.OrdinalIgnoreCase);
        _rootDirectory = Path.Combine(AppDataPaths.Root, "experimental-av-media");
        Directory.CreateDirectory(_rootDirectory);
        _ffmpegPath = ResolveFfmpegPath();

        if (_enabled)
        {
            if (!string.IsNullOrWhiteSpace(_ffmpegPath))
            {
                AppLog.Write("ExpWebRtc", $"Midia experimental habilitada com ffmpeg: {_ffmpegPath}");
            }
            else
            {
                AppLog.Write("ExpWebRtc", "Midia experimental habilitada, mas ffmpeg nao foi encontrado.");
            }
        }
    }

    public bool IsAvailable => _enabled && !string.IsNullOrWhiteSpace(_ffmpegPath);

    public void EnsureStarted(Guid windowId)
    {
        if (!IsAvailable)
        {
            return;
        }

        if (TryGetMp4Path(windowId, out var existingPath))
        {
            try
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(existingPath);
                if (age <= TimeSpan.FromSeconds(2))
                {
                    return;
                }
            }
            catch
            {
            }
        }

        if (_windowBuilds.TryGetValue(windowId, out var activeBuild) && !activeBuild.IsCompleted)
        {
            return;
        }

        var buildTask = Task.Run(() => WaitForBrowserMediaAndGenerateAsync(windowId));
        _windowBuilds[windowId] = buildTask;
        _ = buildTask.ContinueWith(_ => _windowBuilds.TryRemove(windowId, out _), TaskScheduler.Default);
    }

    public bool TryGetMp4Path(Guid windowId, out string path)
    {
        path = Path.Combine(_rootDirectory, windowId.ToString("N"), "panel-experimental.mp4");
        return IsAvailable && File.Exists(path);
    }

    private async Task WaitForBrowserMediaAndGenerateAsync(Guid windowId)
    {
        var deadlineUtc = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadlineUtc)
        {
            if (_browserAudioCaptureService.HasRecentAudio(windowId))
            {
                await GenerateWindowMp4Async(windowId).ConfigureAwait(false);
                return;
            }

            await Task.Delay(300).ConfigureAwait(false);
        }

        AppLog.Write("ExpWebRtc", $"Audio do navegador nao ficou pronto a tempo para a janela {windowId:N}.");
    }

    private async Task GenerateWindowMp4Async(Guid windowId)
    {
        try
        {
            var waveBytes = _browserAudioCaptureService.CaptureWaveSnapshot(windowId);
            if (waveBytes is null || waveBytes.Length == 0)
            {
                AppLog.Write("ExpWebRtc", $"Sem audio recente para gerar midia experimental da janela {windowId:N}.");
                return;
            }

            var jpegBytes = await _browserSnapshotService.CaptureJpegAsync(windowId, default).ConfigureAwait(false);
            if (jpegBytes is null || jpegBytes.Length == 0)
            {
                AppLog.Write("ExpWebRtc", $"Sem frame recente para gerar midia experimental da janela {windowId:N}.");
                return;
            }

            var windowDirectory = Path.Combine(_rootDirectory, windowId.ToString("N"));
            Directory.CreateDirectory(windowDirectory);

            var stillPath = Path.Combine(windowDirectory, "panel.jpg");
            var wavePath = Path.Combine(windowDirectory, "panel.wav");
            var outputPath = Path.Combine(windowDirectory, "panel-experimental.mp4");
            var tempPath = Path.Combine(windowDirectory, "panel-experimental.tmp.mp4");

            File.WriteAllBytes(stillPath, jpegBytes);
            File.WriteAllBytes(wavePath, waveBytes);

            var args =
                "-hide_banner -loglevel error -y " +
                "-loop 1 -framerate 24 -i \"" + stillPath + "\" " +
                "-i \"" + wavePath + "\" " +
                "-map 0:v:0 -map 1:a:0 " +
                "-shortest " +
                "-c:v libx264 -preset veryfast -tune stillimage -profile:v baseline -level 3.1 -pix_fmt yuv420p " +
                "-c:a aac -profile:a aac_low -b:a 128k -ar 44100 -ac 2 " +
                "-f mp4 " +
                "-movflags +faststart -brand mp42 " +
                "\"" + tempPath + "\"";

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });

            if (process is null)
            {
                return;
            }

            AppLog.Write("ExpWebRtc", $"Gerador de midia experimental MP4 real iniciado: janela={windowId:N}");
            await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                File.Move(tempPath, outputPath);
                AppLog.Write("ExpWebRtc", $"Arquivo MP4 experimental real pronto: janela={windowId:N}");
            }
            else
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                AppLog.Write("ExpWebRtc", $"ffmpeg experimental encerrou com erro na janela {windowId:N}: {error}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ExpWebRtc", $"Falha no gerador de midia experimental da janela {windowId:N}: {ex.Message}");
        }
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

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

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

    public void Dispose()
    {
    }
}
