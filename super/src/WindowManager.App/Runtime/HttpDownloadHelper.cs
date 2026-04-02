using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime;

internal static class HttpDownloadHelper
{
    public static async Task DownloadToFileAsync(
        HttpClient httpClient,
        string url,
        string destinationPath,
        string statusLabel,
        string detailPrefix,
        Action<string, double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        using var sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var destinationStream = File.Create(destinationPath);
        await CopyWithProgressAsync(
            sourceStream,
            destinationStream,
            response.Content.Headers.ContentLength,
            statusLabel,
            detailPrefix,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long? contentLength,
        string statusLabel,
        string detailPrefix,
        Action<string, double>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long totalRead = 0;
        long previousRead = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastUpdate = TimeSpan.Zero;
        double smoothedKbps = 0;
        string? lastMessage = null;

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
            totalRead += bytesRead;

            var elapsed = stopwatch.Elapsed;
            var deltaSeconds = Math.Max(0.1, (elapsed - lastUpdate).TotalSeconds);
            var deltaRead = totalRead - previousRead;
            var instantKbps = (deltaRead / 1024.0) / deltaSeconds;
            smoothedKbps = smoothedKbps <= 0
                ? instantKbps
                : ((smoothedKbps * 0.8) + (instantKbps * 0.2));

            var shouldReport = elapsed - lastUpdate >= TimeSpan.FromMilliseconds(700)
                || (contentLength.GetValueOrDefault() > 0 && totalRead >= contentLength.Value);

            if (!shouldReport)
            {
                if (contentLength.GetValueOrDefault() > 0)
                {
                    progress?.Invoke(statusLabel, Math.Min(100, totalRead * 100.0 / contentLength.Value));
                }

                continue;
            }

            previousRead = totalRead;
            lastUpdate = elapsed;
            string message;
            if (contentLength.GetValueOrDefault() > 0)
            {
                message = string.Format(
                    "{0} {1:N1} MB / {2:N1} MB - {3:N0} KB/s",
                    detailPrefix,
                    totalRead / 1024.0 / 1024.0,
                    contentLength.Value / 1024.0 / 1024.0,
                    smoothedKbps);
                if (!string.Equals(lastMessage, message, StringComparison.Ordinal))
                {
                    progress?.Invoke(
                        $"{statusLabel} {message}",
                        Math.Min(100, totalRead * 100.0 / contentLength.Value));
                    lastMessage = message;
                }
                else
                {
                    progress?.Invoke(statusLabel, Math.Min(100, totalRead * 100.0 / contentLength.Value));
                }
            }
            else
            {
                message = string.Format(
                    "{0} {1:N0} KB - {2:N0} KB/s",
                    detailPrefix,
                    totalRead / 1024.0,
                    smoothedKbps);
                if (!string.Equals(lastMessage, message, StringComparison.Ordinal))
                {
                    progress?.Invoke($"{statusLabel} {message}", 0);
                    lastMessage = message;
                }
                else
                {
                    progress?.Invoke(statusLabel, 0);
                }
            }
        }

        if (contentLength.GetValueOrDefault() > 0)
        {
            progress?.Invoke(statusLabel, 100);
        }
    }
}
