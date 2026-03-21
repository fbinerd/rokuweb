using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime.Publishing;

public sealed class ExperimentalAvMediaService : IDisposable
{
    private readonly bool _enabled;
    private readonly string _rootDirectory;
    private readonly string _ffmpegPath;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    public ExperimentalAvMediaService()
    {
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

    public void EnsureStarted()
    {
        if (!IsAvailable)
        {
            return;
        }

        if (_worker is not null && !_worker.IsCompleted)
        {
            return;
        }

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _worker = Task.Run(() => GenerateDiagnosticMp4Async(_cancellation.Token));
    }

    public bool TryGetMp4Path(out string path)
    {
        path = Path.Combine(_rootDirectory, "experimental-diagnostic.mp4");
        return IsAvailable && File.Exists(path);
    }

    private async Task GenerateDiagnosticMp4Async(CancellationToken cancellationToken)
    {
        try
        {
            var outputPath = Path.Combine(_rootDirectory, "experimental-diagnostic.mp4");
            var args =
                "-hide_banner -loglevel error -y " +
                "-f lavfi -i testsrc2=size=1280x720:rate=24 " +
                "-f lavfi -i sine=frequency=440:sample_rate=44100 " +
                "-map 0:v:0 -map 1:a:0 " +
                "-t 600 " +
                "-c:v libx264 -preset veryfast -profile:v baseline -level 3.1 -pix_fmt yuv420p " +
                "-c:a aac -profile:a aac_low -b:a 96k -ar 44100 -ac 2 " +
                "-movflags +faststart -brand mp42 " +
                "\"" + outputPath + "\"";

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

            AppLog.Write("ExpWebRtc", "Gerador de midia experimental MP4 iniciado.");
            await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                AppLog.Write("ExpWebRtc", "Arquivo MP4 diagnostico experimental pronto.");
            }
            else
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                AppLog.Write("ExpWebRtc", "ffmpeg experimental encerrou com erro: " + error);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write("ExpWebRtc", "Falha no gerador de midia experimental: " + ex.Message);
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
        try
        {
            _cancellation?.Cancel();
        }
        catch
        {
        }
    }
}
