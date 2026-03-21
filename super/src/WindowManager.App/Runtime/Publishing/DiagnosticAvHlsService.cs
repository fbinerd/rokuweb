using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime.Publishing;

public sealed class DiagnosticAvHlsService : IDisposable
{
    private readonly string _rootDirectory;
    private readonly string _ffmpegPath;
    private readonly bool _enabled;
    private readonly bool _useMp4Mode;
    private readonly bool _useTsMode;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    public DiagnosticAvHlsService()
    {
        _rootDirectory = Path.Combine(AppDataPaths.Root, "diagnostic-av-hls");
        Directory.CreateDirectory(_rootDirectory);
        _enabled = string.Equals(Environment.GetEnvironmentVariable("SUPERPAINEL_TEST_AV"), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("SUPERPAINEL_TEST_AV_MP4"), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("SUPERPAINEL_TEST_AV_TS"), "1", StringComparison.OrdinalIgnoreCase);
        _useMp4Mode = string.Equals(Environment.GetEnvironmentVariable("SUPERPAINEL_TEST_AV_MP4"), "1", StringComparison.OrdinalIgnoreCase);
        _useTsMode = string.Equals(Environment.GetEnvironmentVariable("SUPERPAINEL_TEST_AV_TS"), "1", StringComparison.OrdinalIgnoreCase);
        _ffmpegPath = ResolveFfmpegPath();

        if (_enabled)
        {
            if (!string.IsNullOrWhiteSpace(_ffmpegPath))
            {
                AppLog.Write("DiagAv", $"Modo diagnostico A/V habilitado com ffmpeg: {_ffmpegPath}");
                if (_useMp4Mode)
                {
                    AppLog.Write("DiagAv", "Modo diagnostico MP4 habilitado.");
                }
                else if (_useTsMode)
                {
                    AppLog.Write("DiagAv", "Modo diagnostico TS habilitado.");
                }
            }
            else
            {
                AppLog.Write("DiagAv", "Modo diagnostico A/V habilitado, mas ffmpeg nao foi encontrado.");
            }
        }
    }

    public bool IsAvailable => _enabled && !string.IsNullOrWhiteSpace(_ffmpegPath);
    public bool UsesMp4 => _useMp4Mode;
    public bool UsesTs => _useTsMode;

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
        _worker = Task.Run(() => RunAsync(_cancellation.Token));
    }

    public bool TryGetPlaylistPath(out string path)
    {
        path = Path.Combine(_rootDirectory, "index.m3u8");
        return IsAvailable && !_useMp4Mode && File.Exists(path);
    }

    public bool TryGetSegmentPath(string fileName, out string path)
    {
        path = string.Empty;
        if (!IsAvailable)
        {
            return false;
        }

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return false;
        }

        path = Path.Combine(_rootDirectory, safeName);
        return !_useMp4Mode && File.Exists(path);
    }

    public bool TryGetMp4Path(out string path)
    {
        path = Path.Combine(_rootDirectory, "diagnostic.mp4");
        return IsAvailable && _useMp4Mode && File.Exists(path);
    }

    public bool TryGetMp3Path(out string path)
    {
        path = Path.Combine(_rootDirectory, "diagnostic.mp3");
        return IsAvailable && _useMp4Mode && File.Exists(path);
    }

    public bool TryGetTsPath(out string path)
    {
        path = Path.Combine(_rootDirectory, "diagnostic.ts");
        return IsAvailable && _useTsMode && File.Exists(path);
    }

    public bool TryGetFfmpegPath(out string path)
    {
        path = _ffmpegPath;
        return !string.IsNullOrWhiteSpace(path);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_useMp4Mode)
        {
            await GenerateMp4Async(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_useTsMode)
        {
            await GenerateTsAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var playlistPath = Path.Combine(_rootDirectory, "index.m3u8");
            var runId = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var segmentPattern = Path.Combine(_rootDirectory, $"segment-{runId}-%03d.m4s");
            var initSegment = Path.Combine(_rootDirectory, $"init-{runId}.mp4");
            var args =
                "-hide_banner -loglevel error -y " +
                "-f lavfi -i testsrc2=size=1280x720:rate=24 " +
                "-f lavfi -i sine=frequency=440:sample_rate=44100 " +
                "-map 0:v:0 -map 1:a:0 " +
                "-t 60 " +
                "-c:v libx264 -preset veryfast -profile:v baseline -level 3.1 -pix_fmt yuv420p " +
                "-g 48 -keyint_min 48 -sc_threshold 0 " +
                "-c:a aac -profile:a aac_low -b:a 96k -ar 44100 -ac 2 " +
                "-af aresample=async=1:first_pts=0 " +
                "-fflags +genpts -avoid_negative_ts make_zero " +
                "-f hls -hls_time 4 -hls_list_size 0 -hls_playlist_type vod " +
                "-hls_flags independent_segments+temp_file " +
                "-hls_segment_type fmp4 " +
                "-hls_fmp4_init_filename \"" + Path.GetFileName(initSegment) + "\" " +
                "-hls_segment_filename \"" + segmentPattern + "\" " +
                "\"" + playlistPath + "\"";

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

            AppLog.Write("DiagAv", $"Gerador A/V diagnostico VOD iniciado. runId={runId}");

            await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            AppLog.Write("DiagAv", $"ffmpeg do diagnostico VOD encerrou. ExitCode={process.ExitCode}. stderr={error}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write("DiagAv", "Falha no gerador A/V diagnostico: " + ex.Message);
        }
    }

    private async Task GenerateMp4Async(CancellationToken cancellationToken)
    {
        try
        {
            var outputPath = Path.Combine(_rootDirectory, "diagnostic.mp4");
            var audioPath = Path.Combine(_rootDirectory, "diagnostic.mp3");
            var args =
                "-hide_banner -loglevel error -y " +
                "-f lavfi -i testsrc2=size=1280x720:rate=24 " +
                "-f lavfi -i sine=frequency=440:sample_rate=44100 " +
                "-map 0:v:0 -map 1:a:0 " +
                "-t 60 " +
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

            AppLog.Write("DiagAv", "Gerador A/V diagnostico MP4 iniciado.");
            await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                AppLog.Write("DiagAv", "Arquivo diagnostico MP4 pronto.");
                await GenerateMp3Async(audioPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                AppLog.Write("DiagAv", "ffmpeg do diagnostico MP4 encerrou com erro: " + error);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write("DiagAv", "Falha no gerador A/V diagnostico MP4: " + ex.Message);
        }
    }

    private async Task GenerateTsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var outputPath = Path.Combine(_rootDirectory, "diagnostic.ts");
            var args =
                "-hide_banner -loglevel error -y " +
                "-f lavfi -i testsrc2=size=1280x720:rate=24 " +
                "-f lavfi -i sine=frequency=440:sample_rate=44100 " +
                "-map 0:v:0 -map 1:a:0 " +
                "-t 60 " +
                "-c:v libx264 -preset veryfast -profile:v baseline -level 3.1 -pix_fmt yuv420p " +
                "-g 48 -keyint_min 48 -sc_threshold 0 " +
                "-c:a aac -profile:a aac_low -b:a 128k -ar 48000 -ac 2 " +
                "-af aresample=async=1:first_pts=0 " +
                "-fflags +genpts -avoid_negative_ts make_zero " +
                "-muxpreload 0 -muxdelay 0 " +
                "-mpegts_flags +resend_headers " +
                "-f mpegts " +
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

            AppLog.Write("DiagAv", "Gerador A/V diagnostico TS iniciado.");
            await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            AppLog.Write("DiagAv", $"ffmpeg do diagnostico TS encerrou. ExitCode={process.ExitCode}. stderr={error}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write("DiagAv", "Falha no gerador A/V diagnostico TS: " + ex.Message);
        }
    }

    private async Task GenerateMp3Async(string outputPath, CancellationToken cancellationToken)
    {
        var args =
            "-hide_banner -loglevel error -y " +
            "-f lavfi -i sine=frequency=440:sample_rate=44100 " +
            "-t 60 " +
            "-c:a libmp3lame -b:a 128k -ar 44100 -ac 2 " +
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

        await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
        if (process.ExitCode == 0)
        {
            AppLog.Write("DiagAv", "Arquivo diagnostico MP3 pronto.");
        }
        else
        {
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            AppLog.Write("DiagAv", "ffmpeg do diagnostico MP3 encerrou com erro: " + error);
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
