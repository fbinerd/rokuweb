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
    private static readonly TimeSpan SegmentDuration = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan SegmentInterval = TimeSpan.FromMilliseconds(1800);
    private const int PlaylistSize = 6;

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
        private readonly object _gate = new();
        private readonly Guid _windowId;
        private readonly List<SegmentEntry> _segments = new();
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
                    var wavBytes = audioCaptureService.CaptureWaveSnapshot(_windowId);
                    if (jpegBytes is null || jpegBytes.Length < 1024 || wavBytes is null || wavBytes.Length < 4096)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
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
                        RegisterSegment(new SegmentEntry(segmentFileName, SegmentDuration));
                        WritePlaylist();
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

                    TryDelete(imagePath);
                    TryDelete(audioPath);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Write("PanelRollingHls", $"Erro no gerador rolling HLS da janela {_windowId:N}: {ex.Message}");
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
}
