using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace WindowManager.App.Runtime.Publishing;

internal sealed class StreamingTuning
{
    private static readonly Lazy<StreamingTuning> CurrentLazy = new Lazy<StreamingTuning>(Load);

    public static StreamingTuning Current => CurrentLazy.Value;

    public double HlsSegmentDurationSeconds { get; private set; } = 0.75;
    public int HlsPlaylistSize { get; private set; } = 3;
    public double InteractionHlsSegmentDurationSeconds { get; private set; } = 0.5;
    public int InteractionHlsPlaylistSize { get; private set; } = 2;
    public string HlsResolution { get; private set; } = "854x480";
    public int HlsVideoBitrate { get; private set; } = 850_000;
    public int HlsAudioBitrate { get; private set; } = 96_000;
    public int HlsFrameRate { get; private set; } = 18;
    public string InteractionHlsResolution { get; private set; } = "512x288";
    public int InteractionHlsVideoBitrate { get; private set; } = 350_000;
    public int InteractionHlsAudioBitrate { get; private set; } = 48_000;
    public int InteractionHlsFrameRate { get; private set; } = 10;
    public double AudioChunkIntervalMs { get; private set; } = 100;
    public double AudioSyncOffsetMs { get; private set; } = 0;
    public double AudioFreshnessMs { get; private set; } = 450;
    public double AudioMaxBufferMs { get; private set; } = 900;
    public double AudioMaxLiveReadLagMs { get; private set; } = 900;
    public double SnapshotCacheLifetimeMs { get; private set; } = 140;
    public double SnapshotBackgroundCaptureIntervalMs { get; private set; } = 66;
    public string? LoadedFromPath { get; private set; }

    private static StreamingTuning Load()
    {
        var tuning = new StreamingTuning();
        var configPath = ResolveConfigPath();
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            AppLog.Write("StreamingConfig", "stream-tuning.ini nao encontrado; usando valores padrao.");
            return tuning;
        }

        try
        {
            var values = ParseIni(configPath!);
            tuning.Apply(values);
            tuning.LoadedFromPath = configPath;
            AppLog.Write(
                "StreamingConfig",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "stream-tuning.ini carregado de {0}: hls={1:0.###}s, playlist={2}, audioChunk={3:0}ms, audioOffset={4:0}ms, freshness={5:0}ms, buffer={6:0}ms, liveLag={7:0}ms, snapshotCache={8:0}ms, snapshotLoop={9:0}ms",
                    configPath,
                    tuning.HlsSegmentDurationSeconds,
                    tuning.HlsPlaylistSize,
                    tuning.AudioChunkIntervalMs,
                    tuning.AudioSyncOffsetMs,
                    tuning.AudioFreshnessMs,
                    tuning.AudioMaxBufferMs,
                    tuning.AudioMaxLiveReadLagMs,
                    tuning.SnapshotCacheLifetimeMs,
                    tuning.SnapshotBackgroundCaptureIntervalMs));
            AppLog.Write(
                "StreamingConfig",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "stream-tuning.ini interaction: hls={0:0.###}s, playlist={1}, resolution={2}, vbitrate={3}, abitrate={4}, fps={5}",
                    tuning.InteractionHlsSegmentDurationSeconds,
                    tuning.InteractionHlsPlaylistSize,
                    tuning.InteractionHlsResolution,
                    tuning.InteractionHlsVideoBitrate,
                    tuning.InteractionHlsAudioBitrate,
                    tuning.InteractionHlsFrameRate));
            AppLog.Write(
                "StreamingConfig",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "stream-tuning.ini video: resolution={0}, vbitrate={1}, abitrate={2}, fps={3}",
                    tuning.HlsResolution,
                    tuning.HlsVideoBitrate,
                    tuning.HlsAudioBitrate,
                    tuning.HlsFrameRate));
        }
        catch (Exception ex)
        {
            AppLog.Write("StreamingConfig", $"Falha ao ler stream-tuning.ini; usando padrao. Motivo: {ex.Message}");
        }

        return tuning;
    }

    private void Apply(IReadOnlyDictionary<string, string> values)
    {
        HlsSegmentDurationSeconds = ReadDouble(values, "hls_segment_duration_seconds", HlsSegmentDurationSeconds, 0.3, 2.0);
        HlsPlaylistSize = ReadInt(values, "hls_playlist_size", HlsPlaylistSize, 2, 6);
        InteractionHlsSegmentDurationSeconds = ReadDouble(values, "interaction_hls_segment_duration_seconds", InteractionHlsSegmentDurationSeconds, 0.35, 2.0);
        InteractionHlsPlaylistSize = ReadInt(values, "interaction_hls_playlist_size", InteractionHlsPlaylistSize, 2, 6);
        HlsResolution = ReadResolution(values, "hls_resolution", HlsResolution);
        HlsVideoBitrate = ReadInt(values, "hls_video_bitrate", HlsVideoBitrate, 250_000, 2_500_000);
        HlsAudioBitrate = ReadInt(values, "hls_audio_bitrate", HlsAudioBitrate, 48_000, 192_000);
        HlsFrameRate = ReadInt(values, "hls_frame_rate", HlsFrameRate, 10, 24);
        InteractionHlsResolution = ReadResolution(values, "interaction_hls_resolution", InteractionHlsResolution);
        InteractionHlsVideoBitrate = ReadInt(values, "interaction_hls_video_bitrate", InteractionHlsVideoBitrate, 180_000, 1_500_000);
        InteractionHlsAudioBitrate = ReadInt(values, "interaction_hls_audio_bitrate", InteractionHlsAudioBitrate, 32_000, 128_000);
        InteractionHlsFrameRate = ReadInt(values, "interaction_hls_frame_rate", InteractionHlsFrameRate, 8, 24);
        AudioChunkIntervalMs = ReadDouble(values, "audio_chunk_interval_ms", AudioChunkIntervalMs, 30, 120);
        AudioSyncOffsetMs = ReadDouble(values, "audio_sync_offset_ms", AudioSyncOffsetMs, -5000, 5000);
        AudioFreshnessMs = ReadDouble(values, "audio_freshness_ms", AudioFreshnessMs, 100, 1000);
        AudioMaxBufferMs = ReadDouble(values, "audio_max_buffer_ms", AudioMaxBufferMs, 200, 2000);
        AudioMaxLiveReadLagMs = ReadDouble(values, "audio_max_live_read_lag_ms", AudioMaxLiveReadLagMs, 80, 1000);
        SnapshotCacheLifetimeMs = ReadDouble(values, "snapshot_cache_lifetime_ms", SnapshotCacheLifetimeMs, 40, 500);
        SnapshotBackgroundCaptureIntervalMs = ReadDouble(values, "snapshot_background_capture_interval_ms", SnapshotBackgroundCaptureIntervalMs, 30, 200);
    }

    private static Dictionary<string, string> ParseIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine?.Trim() ?? string.Empty;
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line.Substring(0, separatorIndex).Trim();
            var value = line.Substring(separatorIndex + 1).Trim();
            if (key.Length == 0)
            {
                continue;
            }

            values[key] = value;
        }

        return values;
    }

    private static double ReadDouble(IReadOnlyDictionary<string, string> values, string key, double fallback, double min, double max)
    {
        if (!values.TryGetValue(key, out var raw) ||
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return fallback;
        }

        return Math.Max(min, Math.Min(max, parsed));
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback, int min, int max)
    {
        if (!values.TryGetValue(key, out var raw) || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return fallback;
        }

        return Math.Max(min, Math.Min(max, parsed));
    }

    private static string ReadResolution(IReadOnlyDictionary<string, string> values, string key, string fallback)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        var normalized = (raw ?? string.Empty).Trim().ToLowerInvariant();
        var parts = normalized.Split('x');
        if (parts.Length != 2)
        {
            return fallback;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
        {
            return fallback;
        }

        if (width < 320 || width > 1920 || height < 180 || height > 1080)
        {
            return fallback;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0}x{1}", width, height);
    }

    private static string? ResolveConfigPath()
    {
        var envPath = Environment.GetEnvironmentVariable("SUPERPAINEL_STREAM_CONFIG");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        foreach (var candidate in EnumerateCandidatePaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            yield return Path.Combine(baseDirectory, "stream-tuning.ini");
        }

        var current = new DirectoryInfo(baseDirectory);
        while (current is not null)
        {
            yield return Path.Combine(current.FullName, "stream-tuning.ini");

            var manifestPath = Path.Combine(current.FullName, "manifest");
            var superPath = Path.Combine(current.FullName, "super");
            if (File.Exists(manifestPath) && Directory.Exists(superPath))
            {
                yield return Path.Combine(current.FullName, "stream-tuning.ini");
                yield return Path.Combine(current.FullName, "super", "stream-tuning.ini");
                yield break;
            }

            current = current.Parent;
        }
    }
}
