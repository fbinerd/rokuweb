using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime.Publishing;

internal sealed class InMemoryHlsPutServer : IDisposable
{
    internal sealed class GetResponse
    {
        public GetResponse(int statusCode, byte[] payload, string contentType)
        {
            StatusCode = statusCode;
            Payload = payload ?? Array.Empty<byte>();
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        }

        public int StatusCode { get; }

        public byte[] Payload { get; }

        public string ContentType { get; }
    }

    private sealed class RouteHandler
    {
        public RouteHandler(
            Func<string, byte[], CancellationToken, Task> putHandler,
            Func<string, CancellationToken, Task<GetResponse>> getHandler,
            Func<string, CancellationToken, Task> deleteHandler)
        {
            PutHandler = putHandler;
            GetHandler = getHandler;
            DeleteHandler = deleteHandler;
        }

        public Func<string, byte[], CancellationToken, Task> PutHandler { get; }

        public Func<string, CancellationToken, Task<GetResponse>> GetHandler { get; }

        public Func<string, CancellationToken, Task> DeleteHandler { get; }
    }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<string, RouteHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _acceptLoop;

    public InMemoryHlsPutServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
    }

    public int Port { get; }

    public IDisposable Register(
        string token,
        Func<string, byte[], CancellationToken, Task> putHandler,
        Func<string, CancellationToken, Task<GetResponse>> getHandler,
        Func<string, CancellationToken, Task> deleteHandler)
    {
        _handlers[token] = new RouteHandler(putHandler, getHandler, deleteHandler);
        return new Registration(() => _handlers.TryRemove(token, out _));
    }

    public void Dispose()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch
        {
        }

        try
        {
            _listener.Stop();
        }
        catch
        {
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                var acceptTask = _listener.AcceptTcpClientAsync();
                var completed = await Task.WhenAny(acceptTask, Task.Delay(250, cancellationToken)).ConfigureAwait(false);
                if (completed != acceptTask)
                {
                    continue;
                }

                client = acceptTask.Result;
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                client?.Dispose();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var network = client.GetStream())
        {
            try
            {
                var request = await ReadRequestAsync(network, cancellationToken).ConfigureAwait(false);
                if (request is null || string.IsNullOrWhiteSpace(request.RequestLine))
                {
                    await WriteResponseAsync(network, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var requestParts = request.RequestLine.Split(' ');
                if (requestParts.Length < 2)
                {
                    await WriteResponseAsync(network, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var method = requestParts[0];
                var path = requestParts[1];
                var contentLength = 0;
                var transferChunked = false;
                var expectContinue = false;

                foreach (var headerLine in request.HeaderLines)
                {
                    var separator = headerLine.IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    var headerName = headerLine.Substring(0, separator).Trim();
                    var headerValue = headerLine.Substring(separator + 1).Trim();
                    if (headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(headerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
                    }
                    else if (headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                             headerValue.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        transferChunked = true;
                    }
                    else if (headerName.Equals("Expect", StringComparison.OrdinalIgnoreCase) &&
                             headerValue.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        expectContinue = true;
                    }
                }

                var trimmedPath = path.Split('?')[0].Trim('/');
                var parts = trimmedPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !parts[0].Equals("hls", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(network, 404, "Not Found", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var token = parts[1];
                var relativePath = string.Join("/", parts, 2, parts.Length - 2);
                if (!_handlers.TryGetValue(token, out var handler))
                {
                    await WriteResponseAsync(network, 404, "Not Found", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if ((string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)) &&
                    (transferChunked || contentLength > 0))
                {
                    if (transferChunked)
                    {
                        await ReadChunkedBodyAsync(network, request.BodyPrefix, cancellationToken).ConfigureAwait(false);
                    }
                    else if (contentLength > 0)
                    {
                        await ReadContentLengthBodyAsync(network, request.BodyPrefix, contentLength, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    var response = await handler.GetHandler(relativePath, cancellationToken).ConfigureAwait(false);
                    await WriteBinaryResponseAsync(network, response.StatusCode, response.Payload, response.ContentType, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    await handler.DeleteHandler(relativePath, cancellationToken).ConfigureAwait(false);
                    await WriteResponseAsync(network, 200, "OK", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var acceptsBodyMethod =
                    string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);

                if (!acceptsBodyMethod || (!transferChunked && contentLength <= 0))
                {
                    AppLog.Write(
                        "PanelRollingHls",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Ingest rejeitou requisicao HTTP: method={0}, path={1}, contentLength={2}, chunked={3}",
                            method,
                            path,
                            contentLength,
                            transferChunked));
                    await WriteResponseAsync(network, 405, "Method Not Allowed", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (expectContinue)
                {
                    await WriteInterimContinueAsync(network, cancellationToken).ConfigureAwait(false);
                }

                var body = transferChunked
                    ? await ReadChunkedBodyAsync(network, request.BodyPrefix, cancellationToken).ConfigureAwait(false)
                    : await ReadContentLengthBodyAsync(network, request.BodyPrefix, contentLength, cancellationToken).ConfigureAwait(false);

                await handler.PutHandler(relativePath, body, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(network, 200, "OK", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Write("PanelRollingHls", $"Ingest falhou ao processar HTTP local: {ex.Message}");
                try
                {
                    await WriteResponseAsync(network, 500, "Internal Server Error", cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task<HttpRequestParts?> ReadRequestAsync(NetworkStream network, CancellationToken cancellationToken)
    {
        using (var buffer = new MemoryStream())
        {
            var temp = new byte[4096];
            while (true)
            {
                var read = await network.ReadAsync(temp, 0, temp.Length, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return null;
                }

                await buffer.WriteAsync(temp, 0, read, cancellationToken).ConfigureAwait(false);
                var bytes = buffer.ToArray();
                var headerEnd = FindHeaderEnd(bytes);
                if (headerEnd < 0)
                {
                    continue;
                }

                var headerText = Encoding.ASCII.GetString(bytes, 0, headerEnd);
                var headerLines = headerText.Replace("\r\n", "\n").Split('\n');
                if (headerLines.Length == 0)
                {
                    return null;
                }

                var bodyOffset = headerEnd + 4;
                var bodyPrefixLength = Math.Max(0, bytes.Length - bodyOffset);
                var bodyPrefix = new byte[bodyPrefixLength];
                if (bodyPrefixLength > 0)
                {
                    Buffer.BlockCopy(bytes, bodyOffset, bodyPrefix, 0, bodyPrefixLength);
                }

                var remainingHeaderLines = Array.Empty<string>();
                if (headerLines.Length > 1)
                {
                    remainingHeaderLines = new string[headerLines.Length - 1];
                    Array.Copy(headerLines, 1, remainingHeaderLines, 0, remainingHeaderLines.Length);
                }

                return new HttpRequestParts(headerLines[0], remainingHeaderLines, bodyPrefix);
            }
        }
    }

    private static int FindHeaderEnd(byte[] buffer)
    {
        for (var i = 0; i <= buffer.Length - 4; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task<byte[]> ReadContentLengthBodyAsync(NetworkStream network, byte[] bodyPrefix, int contentLength, CancellationToken cancellationToken)
    {
        var body = new byte[contentLength];
        var totalRead = 0;
        if (bodyPrefix.Length > 0)
        {
            var copied = Math.Min(bodyPrefix.Length, body.Length);
            Buffer.BlockCopy(bodyPrefix, 0, body, 0, copied);
            totalRead += copied;
        }

        while (totalRead < body.Length)
        {
            var read = await network.ReadAsync(body, totalRead, body.Length - totalRead, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new IOException("Corpo HTTP interrompido antes do fim.");
            }

            totalRead += read;
        }

        return body;
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(NetworkStream network, byte[] bodyPrefix, CancellationToken cancellationToken)
    {
        using (var buffered = new BufferedNetworkReader(network, bodyPrefix))
        using (var output = new MemoryStream())
        {
            while (true)
            {
                var sizeLine = await buffered.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sizeLine))
                {
                    continue;
                }

                var separator = sizeLine.IndexOf(';');
                var hexSize = separator >= 0 ? sizeLine.Substring(0, separator) : sizeLine;
                var chunkSize = int.Parse(hexSize, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (chunkSize == 0)
                {
                    while (await buffered.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } trailerLine && trailerLine.Length > 0)
                    {
                    }

                    return output.ToArray();
                }

                var chunk = await buffered.ReadBytesAsync(chunkSize, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
                await buffered.ReadBytesAsync(2, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Task WriteInterimContinueAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var payload = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
        return stream.WriteAsync(payload, 0, payload.Length, cancellationToken);
    }

    private static Task WriteResponseAsync(NetworkStream stream, int statusCode, string statusText, CancellationToken cancellationToken)
    {
        var payload = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        return stream.WriteAsync(payload, 0, payload.Length, cancellationToken);
    }

    private static async Task WriteBinaryResponseAsync(NetworkStream stream, int statusCode, byte[] payload, string contentType, CancellationToken cancellationToken)
    {
        var statusText = statusCode == 200 ? "OK" : statusCode == 404 ? "Not Found" : "Response";
        var header = Encoding.ASCII.GetBytes(
            string.Format(
                CultureInfo.InvariantCulture,
                "HTTP/1.1 {0} {1}\r\nContent-Type: {2}\r\nContent-Length: {3}\r\nConnection: close\r\n\r\n",
                statusCode,
                statusText,
                contentType,
                payload?.Length ?? 0));
        await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
        if (payload is { Length: > 0 })
        {
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly Action _dispose;
        private int _disposed;

        public Registration(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _dispose();
            }
        }
    }

    private sealed class HttpRequestParts
    {
        public HttpRequestParts(string requestLine, string[] headerLines, byte[] bodyPrefix)
        {
            RequestLine = requestLine;
            HeaderLines = headerLines;
            BodyPrefix = bodyPrefix;
        }

        public string RequestLine { get; }

        public string[] HeaderLines { get; }

        public byte[] BodyPrefix { get; }
    }

    private sealed class BufferedNetworkReader : IDisposable
    {
        private readonly NetworkStream _network;
        private readonly MemoryStream _prefix;

        public BufferedNetworkReader(NetworkStream network, byte[] prefix)
        {
            _network = network;
            _prefix = new MemoryStream(prefix ?? Array.Empty<byte>(), writable: false);
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            while (true)
            {
                var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (next < 0)
                {
                    return buffer.Length == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());
                }

                if (next == '\n')
                {
                    var lineBytes = buffer.ToArray();
                    if (lineBytes.Length > 0 && lineBytes[lineBytes.Length - 1] == '\r')
                    {
                        Array.Resize(ref lineBytes, lineBytes.Length - 1);
                    }

                    return Encoding.ASCII.GetString(lineBytes);
                }

                buffer.WriteByte((byte)next);
            }
        }

        public async Task<byte[]> ReadBytesAsync(int count, CancellationToken cancellationToken)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await ReadAsync(buffer, offset, count - offset, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new IOException("Fluxo HTTP interrompido antes do fim do corpo.");
                }

                offset += read;
            }

            return buffer;
        }

        private async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_prefix.Position < _prefix.Length)
            {
                return _prefix.Read(buffer, offset, count);
            }

            return await _network.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            var single = await ReadBytesInternalAsync(1, cancellationToken).ConfigureAwait(false);
            return single.Length == 0 ? -1 : single[0];
        }

        private async Task<byte[]> ReadBytesInternalAsync(int count, CancellationToken cancellationToken)
        {
            var buffer = new byte[count];
            var read = await ReadAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return Array.Empty<byte>();
            }

            if (read == count)
            {
                return buffer;
            }

            var partial = new byte[read];
            Buffer.BlockCopy(buffer, 0, partial, 0, read);
            return partial;
        }

        public void Dispose()
        {
            _prefix.Dispose();
        }
    }
}
