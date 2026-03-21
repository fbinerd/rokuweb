using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime.Publishing;

public sealed class DiagnosticMediaHttpServer : IDisposable
{
    private readonly DiagnosticAvHlsService _diagnosticAvHlsService;
    private readonly object _gate = new object();
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private int _activePort;
    private string _publicHost = string.Empty;

    public DiagnosticMediaHttpServer(DiagnosticAvHlsService diagnosticAvHlsService)
    {
        _diagnosticAvHlsService = diagnosticAvHlsService;
    }

    public bool TryEnsureStarted(string publicHost, int preferredPort, out int port)
    {
        lock (_gate)
        {
            port = 0;
            if (!_diagnosticAvHlsService.IsAvailable)
            {
                return false;
            }

            if (_listener is not null && _activePort > 0 && string.Equals(_publicHost, publicHost, StringComparison.OrdinalIgnoreCase))
            {
                port = _activePort;
                return true;
            }

            StopInternal();

            Exception? lastError = null;
            for (var candidatePort = preferredPort; candidatePort <= preferredPort + 3; candidatePort++)
            {
                try
                {
                    var listener = new HttpListener();
                    var prefix = $"http://+:{candidatePort}/diag-av/";
                    listener.Prefixes.Add(prefix);
                    listener.Start();

                    _listener = listener;
                    _cancellation = new CancellationTokenSource();
                    _activePort = candidatePort;
                    _publicHost = publicHost;
                    _ = Task.Run(() => ListenLoopAsync(listener, _cancellation.Token));
                    AppLog.Write("DiagMedia", $"Servidor diagnostico iniciado em {prefix}");
                    port = candidatePort;
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    AppLog.Write("DiagMedia", $"Falha ao iniciar servidor diagnostico na porta {candidatePort}: {ex.Message}");
                }
            }

            AppLog.Write("DiagMedia", "Nao foi possivel iniciar servidor diagnostico separado: " + lastError?.Message);
            return false;
        }
    }

    public string BuildUrl(int port)
    {
        if (_diagnosticAvHlsService.UsesMp4)
        {
            return $"http://{_publicHost}:{port}/diag-av/diagnostic.mp4";
        }

        if (_diagnosticAvHlsService.UsesTs)
        {
            return $"http://{_publicHost}:{port}/diag-av/diagnostic.ts";
        }

        return $"http://{_publicHost}:{port}/diag-av/index.m3u8";
    }

    private async Task ListenLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (context is null)
            {
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var fileName = path.StartsWith("/diag-av/", StringComparison.OrdinalIgnoreCase)
                ? path.Substring("/diag-av/".Length)
                : string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            string filePath;
            string contentType;

            if (string.Equals(fileName, "diagnostic.mp4", StringComparison.OrdinalIgnoreCase))
            {
                if (!_diagnosticAvHlsService.TryGetMp4Path(out filePath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                contentType = "video/mp4";
            }
            else if (string.Equals(fileName, "diagnostic.ts", StringComparison.OrdinalIgnoreCase))
            {
                if (!_diagnosticAvHlsService.TryGetTsPath(out filePath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                contentType = "video/mp2t";
            }
            else if (string.Equals(fileName, "index.m3u8", StringComparison.OrdinalIgnoreCase))
            {
                if (!_diagnosticAvHlsService.TryGetPlaylistPath(out filePath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                contentType = "application/vnd.apple.mpegurl";
            }
            else
            {
                if (!_diagnosticAvHlsService.TryGetSegmentPath(fileName, out filePath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var extension = Path.GetExtension(filePath);
                contentType = string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".m4s", StringComparison.OrdinalIgnoreCase)
                    ? "video/mp4"
                    : "video/mp2t";
            }

            await WriteFileResponseAsync(context, filePath, contentType, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Write("DiagMedia", "Falha ao responder servidor diagnostico: " + ex.Message);
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private static async Task WriteFileResponseAsync(HttpListenerContext context, string filePath, string contentType, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        var totalLength = fileInfo.Length;
        var request = context.Request;
        var response = context.Response;
        response.ContentType = contentType;
        response.AddHeader("Accept-Ranges", "bytes");
        response.AddHeader("Cache-Control", "no-store");

        long start = 0;
        long end = totalLength - 1;
        var hasRange = TryParseRangeHeader(request.Headers["Range"], totalLength, out start, out end);
        if (hasRange)
        {
            response.StatusCode = 206;
            response.AddHeader("Content-Range", $"bytes {start}-{end}/{totalLength}");
        }
        else
        {
            response.StatusCode = 200;
        }

        var contentLength = end - start + 1;
        response.ContentLength64 = contentLength;

        if (string.Equals(request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            response.OutputStream.Close();
            return;
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(start, SeekOrigin.Begin);
        var remaining = contentLength;
        var buffer = new byte[64 * 1024];
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await fs.ReadAsync(buffer, 0, toRead, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await response.OutputStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }

        response.OutputStream.Close();
    }

    private static bool TryParseRangeHeader(string? rangeHeader, long totalLength, out long start, out long end)
    {
        start = 0;
        end = totalLength - 1;

        if (string.IsNullOrWhiteSpace(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = rangeHeader!.Substring("bytes=".Length);
        var separatorIndex = value.IndexOf('-');
        if (separatorIndex < 0)
        {
            return false;
        }

        var startText = value.Substring(0, separatorIndex).Trim();
        var endText = value.Substring(separatorIndex + 1).Trim();

        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, out var suffixLength) || suffixLength <= 0)
            {
                return false;
            }

            start = Math.Max(0, totalLength - suffixLength);
            end = totalLength - 1;
            return true;
        }

        if (!long.TryParse(startText, out start) || start < 0)
        {
            return false;
        }

        if (endText.Length == 0)
        {
            end = totalLength - 1;
            return true;
        }

        return long.TryParse(endText, out end) && end >= start;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopInternal();
        }
    }

    private void StopInternal()
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
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
        }

        _cancellation?.Dispose();
        _cancellation = null;
        _listener = null;
        _activePort = 0;
        _publicHost = string.Empty;
    }
}
