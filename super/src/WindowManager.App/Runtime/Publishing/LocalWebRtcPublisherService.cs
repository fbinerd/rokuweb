using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WindowManager.App.Runtime;
using WindowManager.Core.Models;

namespace WindowManager.App.Runtime.Publishing;

public sealed class LocalWebRtcPublisherService
{
    private readonly BrowserSnapshotService _browserSnapshotService;
    private readonly ExperimentalWebRtcAvService _experimentalWebRtcAvService;
    private readonly ExperimentalRealtimeTransportService _experimentalRealtimeTransportService;
    private readonly ExperimentalAvMediaService _experimentalAvMediaService;
    private readonly RokuDevDeploymentService _rokuDevDeploymentService;
    private readonly object _listenerGate = new object();
    private readonly ConcurrentDictionary<string, PublishedWindowRoute> _routes = new ConcurrentDictionary<string, PublishedWindowRoute>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string> _windowRouteKeys = new ConcurrentDictionary<Guid, string>();
    private readonly ConcurrentDictionary<Guid, BridgeWindowSnapshot> _windowSnapshots = new ConcurrentDictionary<Guid, BridgeWindowSnapshot>();
    private readonly ConcurrentDictionary<string, RegisteredDisplaySnapshot> _registeredDisplays = new ConcurrentDictionary<string, RegisteredDisplaySnapshot>(StringComparer.OrdinalIgnoreCase);

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCancellation;
    private string _activeListenerKey = string.Empty;

    public LocalWebRtcPublisherService(BrowserSnapshotService browserSnapshotService, ExperimentalWebRtcAvService experimentalWebRtcAvService, ExperimentalRealtimeTransportService experimentalRealtimeTransportService, ExperimentalAvMediaService experimentalAvMediaService, ExperimentalMediaHttpServer experimentalMediaHttpServer, AppUpdatePreferenceStore appUpdatePreferenceStore)
    {
        _browserSnapshotService = browserSnapshotService;
        _experimentalWebRtcAvService = experimentalWebRtcAvService;
        _experimentalRealtimeTransportService = experimentalRealtimeTransportService;
        _experimentalAvMediaService = experimentalAvMediaService;
        _rokuDevDeploymentService = new RokuDevDeploymentService(appUpdatePreferenceStore);
    }

    public async Task<string> PublishAsync(WindowSession session, int serverPort, WebRtcBindMode bindMode, string specificIp, CancellationToken cancellationToken)
    {
        if (session.InitialUri is null)
        {
            throw new InvalidOperationException("A janela precisa ter uma URL antes de publicar a rota local.");
        }

        var port = serverPort <= 0 ? 8088 : serverPort;
        var slug = LinkRtcAddressBuilder.NormalizeRouteSegment(session.Title, session.Id.ToString("N"));
        var endpoint = LinkRtcAddressBuilder.ResolveListenerEndpoint(bindMode, specificIp, port);
        var activePort = EnsureListener(endpoint);
        var publishedUrl = LinkRtcAddressBuilder.BuildPublishedUrl(session, activePort, bindMode, specificIp);
        var routeKey = BuildRouteKey(activePort, slug);
        RemoveExistingRoute(session.Id);

        _routes[routeKey] = new PublishedWindowRoute
        {
            WindowId = session.Id,
            Title = session.Title,
            SourceUrl = session.InitialUri.ToString(),
            RoutePath = slug,
            Port = activePort,
            PublishedUrl = publishedUrl
        };

        _windowRouteKeys[session.Id] = routeKey;
        await Task.CompletedTask;
        return publishedUrl;
    }

    public Task UnpublishAsync(WindowSession session, CancellationToken cancellationToken)
    {
        RemoveExistingRoute(session.Id);
        return Task.CompletedTask;
    }

    public async Task<int> ForceUpdateConnectedDisplaysAsync(CancellationToken cancellationToken)
    {
        var expectedVersion = GetExpectedRokuChannelVersion();
        var displays = _registeredDisplays.Values.ToArray();
        var updatedCount = 0;

        foreach (var display in displays)
        {
            cancellationToken.ThrowIfCancellationRequested();

            display.ExpectedChannelVersion = expectedVersion;
            display.UpdateAvailable =
                !string.IsNullOrWhiteSpace(display.ExpectedChannelVersion) &&
                !string.Equals(display.ChannelVersion, display.ExpectedChannelVersion, StringComparison.OrdinalIgnoreCase);

            if (!display.UpdateAvailable)
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "TV ja esta atualizada e foi ignorada no disparo manual: id={0}, canal={1}",
                        display.DeviceId,
                        display.ChannelVersion));
                continue;
            }

            AppLog.Write(
                "RokuDeploy",
                string.Format(
                    "Atualizacao manual disparada para TV id={0}, atual={1}, esperado={2}",
                    display.DeviceId,
                    display.ChannelVersion,
                    display.ExpectedChannelVersion));

            var result = await _rokuDevDeploymentService.DeployNowAsync(display, display.ExpectedChannelVersion).ConfigureAwait(false);
            AppLog.Write(
                "RokuDeploy",
                string.Format(
                    "Resultado do sideload manual para TV id={0}: {1}",
                    display.DeviceId,
                    result));

            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                updatedCount++;
            }
        }

        return updatedCount;
    }

    public async Task<string> ForceUpdateDisplayTargetAsync(DisplayTarget target, CancellationToken cancellationToken)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.NetworkAddress))
        {
            return "tv_sem_ip";
        }

        cancellationToken.ThrowIfCancellationRequested();

        var expectedVersion = GetExpectedRokuChannelVersion();
        RegisteredDisplaySnapshot display;

        var registered = _registeredDisplays.Values.FirstOrDefault(x =>
            string.Equals(x.NetworkAddress, target.NetworkAddress, StringComparison.OrdinalIgnoreCase));

        if (registered is not null)
        {
            display = registered;
        }
        else
        {
            display = new RegisteredDisplaySnapshot
            {
                DeviceId = "roku-target-" + target.NetworkAddress.Replace(".", "-"),
                DeviceType = "roku",
                DeviceModel = target.Name,
                ChannelVersion = string.Empty,
                NetworkAddress = target.NetworkAddress,
                LastSeenUtc = DateTime.UtcNow.ToString("O")
            };
        }

        display.ExpectedChannelVersion = expectedVersion;
        display.UpdateAvailable =
            !string.IsNullOrWhiteSpace(display.ExpectedChannelVersion) &&
            !string.Equals(display.ChannelVersion, display.ExpectedChannelVersion, StringComparison.OrdinalIgnoreCase);

        AppLog.Write(
            "RokuDeploy",
            string.Format(
                "Atualizacao solicitada para TV descoberta: nome={0}, ip={1}, atual={2}, esperado={3}",
                target.Name,
                target.NetworkAddress,
                display.ChannelVersion,
                display.ExpectedChannelVersion));

        return await _rokuDevDeploymentService.DeployNowAsync(display, display.ExpectedChannelVersion).ConfigureAwait(false);
    }

    public void UpdateWindowSnapshots(IEnumerable<WindowSession> windows, int serverPort, WebRtcBindMode bindMode, string specificIp)
    {
        var port = serverPort <= 0 ? 8090 : serverPort;
        var endpoint = LinkRtcAddressBuilder.ResolveListenerEndpoint(bindMode, specificIp, port);
        var activePort = EnsureListener(endpoint);

        var activeIds = new HashSet<Guid>();
        foreach (var window in windows)
        {
            _experimentalAvMediaService.EnsureStarted(window.Id);
            var snapshot = BuildWindowSnapshot(window, activePort, bindMode, specificIp);
            _windowSnapshots[window.Id] = snapshot;
            activeIds.Add(window.Id);
        }

        foreach (var existingId in _windowSnapshots.Keys)
        {
            if (!activeIds.Contains(existingId))
            {
                _windowSnapshots.TryRemove(existingId, out _);
            }
        }
    }

    private int EnsureListener(IPEndPoint endpoint)
    {
        lock (_listenerGate)
        {
            var preferredPort = endpoint.Port;

            if (_listener is not null && !string.IsNullOrWhiteSpace(_activeListenerKey))
            {
                return _listener.LocalEndpoint is IPEndPoint currentEndpoint ? currentEndpoint.Port : preferredPort;
            }

            Exception? lastError = null;
            for (var candidatePort = preferredPort; candidatePort <= preferredPort + 3; candidatePort++)
            {
                var candidateEndpoint = new IPEndPoint(endpoint.Address, candidatePort);
                var listenerKey = string.Format("{0}:{1}", candidateEndpoint.Address, candidateEndpoint.Port);
                try
                {
                    StopListener();
                    StartListener(candidateEndpoint, listenerKey);
                    return candidatePort;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw new InvalidOperationException("Nao foi possivel iniciar o bridge local nas portas 8090-8093.", lastError);
        }
    }

    private void StartListener(IPEndPoint endpoint, string listenerKey)
    {
        var cancellation = new CancellationTokenSource();
        var listener = new TcpListener(endpoint);
        listener.Start();

        _listener = listener;
        _listenerCancellation = cancellation;
        _activeListenerKey = listenerKey;

        Task.Run(() => ListenLoopAsync(listener, cancellation.Token));
    }

    private void StopListener()
    {
        try
        {
            _listenerCancellation?.Cancel();
        }
        catch
        {
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
        }

        _listenerCancellation?.Dispose();
        _listenerCancellation = null;
        _listener = null;
        _activeListenerKey = string.Empty;
    }

    private async Task ListenLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch
            {
                client?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
        {
            var method = "GET";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? requestLine;
            var contentLength = 0;
            try
            {
                requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                method = ParseMethod(requestLine);

                string? headerLine;
                do
                {
                    headerLine = await reader.ReadLineAsync();
                    if (!string.IsNullOrEmpty(headerLine))
                    {
                        var separatorIndex = headerLine.IndexOf(':');
                        if (separatorIndex > 0)
                        {
                            var headerKey = headerLine.Substring(0, separatorIndex).Trim();
                            var headerValue = headerLine.Substring(separatorIndex + 1).Trim();
                            headers[headerKey] = headerValue;
                            if (string.Equals(headerKey, "Content-Length", StringComparison.OrdinalIgnoreCase))
                            {
                                _ = int.TryParse(headerValue, out contentLength);
                            }
                        }
                    }
                }
                while (!string.IsNullOrEmpty(headerLine));
            }
            catch
            {
                return;
            }

            string requestBody = string.Empty;
            if (contentLength > 0)
            {
                var bodyBuffer = new char[contentLength];
                var totalRead = 0;
                while (totalRead < contentLength)
                {
                    var justRead = await reader.ReadAsync(bodyBuffer, totalRead, contentLength - totalRead);
                    if (justRead <= 0)
                    {
                        break;
                    }

                    totalRead += justRead;
                }

                requestBody = new string(bodyBuffer, 0, totalRead);
            }

            var path = ParsePath(requestLine);
            var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
            var bytes = await BuildResponseAsync(method, path, requestBody, remoteAddress, headers, cancellationToken);
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        }
    }

    private async Task<byte[]> BuildResponseAsync(string method, string path, string requestBody, string remoteAddress, Dictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var requestTarget = path ?? "/";
        var normalizedPath = requestTarget.Split('?')[0];

        if (normalizedPath.StartsWith("/thumbnails/", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildThumbnailResponseAsync(normalizedPath, cancellationToken);
        }

        if (string.Equals(normalizedPath, "/health", StringComparison.OrdinalIgnoreCase))
        {
            return BuildHttpResponse(200, "ok", "text/plain; charset=utf-8");
        }

        if (string.Equals(normalizedPath, "/api/windows", StringComparison.OrdinalIgnoreCase))
        {
            return BuildHttpResponse(200, BuildWindowsJson(), "application/json; charset=utf-8");
        }

        if (string.Equals(normalizedPath, "/api/register-display", StringComparison.OrdinalIgnoreCase))
        {
            RegisterDisplay(requestTarget, remoteAddress);
            return BuildHttpResponse(200, "{\"ok\":true}", "application/json; charset=utf-8");
        }

        if (string.Equals(normalizedPath, "/api/input-log", StringComparison.OrdinalIgnoreCase))
        {
            LogInputRequest(requestTarget, remoteAddress);
            return BuildHttpResponse(200, "{\"ok\":true}", "application/json; charset=utf-8");
        }

        if (normalizedPath.StartsWith("/api/experimental-av/", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildExperimentalWebRtcResponseAsync(method, requestTarget, normalizedPath, requestBody, remoteAddress, headers, cancellationToken);
        }

        if (string.Equals(normalizedPath, "/api/control", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleControlRequestAsync(requestTarget, cancellationToken);
        }

        var rawSegment = Uri.UnescapeDataString(normalizedPath.Trim('/'));
        var slug = LinkRtcAddressBuilder.NormalizeRouteSegment(rawSegment, string.Empty);

        if (string.IsNullOrWhiteSpace(slug))
        {
            return BuildHttpResponse(200, BuildIndexPage(), "text/html; charset=utf-8");
        }

        var port = (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 8088;
        if (!_routes.TryGetValue(BuildRouteKey(port, slug), out var route))
        {
            return BuildHttpResponse(404, "<html><body style='font-family:Segoe UI;padding:24px'><h1>Rota nao encontrada</h1></body></html>", "text/html; charset=utf-8");
        }

        return BuildHttpResponse(200, BuildPublishedPage(route), "text/html; charset=utf-8");
    }

    private void RegisterDisplay(string requestTarget, string remoteAddress)
    {
        var queryIndex = requestTarget.IndexOf('?');
        if (queryIndex < 0 || queryIndex >= requestTarget.Length - 1)
        {
            return;
        }

        var values = ParseQueryString(requestTarget.Substring(queryIndex + 1));
        var key = GetValue(values, "deviceId");
        if (string.IsNullOrWhiteSpace(key))
        {
            key = string.IsNullOrWhiteSpace(remoteAddress) ? Guid.NewGuid().ToString("N") : remoteAddress;
        }

        var snapshot = new RegisteredDisplaySnapshot
        {
            DeviceId = key,
            DeviceType = GetValue(values, "deviceType"),
            DeviceModel = GetValue(values, "deviceModel"),
            FirmwareVersion = GetValue(values, "firmwareVersion"),
            ChannelVersion = GetValue(values, "channelVersion"),
            ScreenWidth = ParseInt(GetValue(values, "screenWidth"), 0),
            ScreenHeight = ParseInt(GetValue(values, "screenHeight"), 0),
            NetworkAddress = remoteAddress,
            LastSeenUtc = DateTime.UtcNow.ToString("O")
        };

        snapshot.ExpectedChannelVersion = GetExpectedRokuChannelVersion();
        snapshot.UpdateAvailable =
            !string.IsNullOrWhiteSpace(snapshot.ExpectedChannelVersion) &&
            !string.Equals(snapshot.ChannelVersion, snapshot.ExpectedChannelVersion, StringComparison.OrdinalIgnoreCase);

        var changed = true;
        if (_registeredDisplays.TryGetValue(key, out var previous))
        {
            changed =
                !string.Equals(previous.DeviceModel, snapshot.DeviceModel, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous.FirmwareVersion, snapshot.FirmwareVersion, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous.ChannelVersion, snapshot.ChannelVersion, StringComparison.OrdinalIgnoreCase) ||
                previous.ScreenWidth != snapshot.ScreenWidth ||
                previous.ScreenHeight != snapshot.ScreenHeight ||
                previous.UpdateAvailable != snapshot.UpdateAvailable ||
                !string.Equals(previous.NetworkAddress, snapshot.NetworkAddress, StringComparison.OrdinalIgnoreCase);
        }

        _registeredDisplays[key] = snapshot;

        if (changed)
        {
            AppLog.Write(
                "Roku",
                string.Format(
                    "TV registrada/atualizada: id={0}, modelo={1}, firmware={2}, canal={3}, esperado={4}, resolucao={5}x{6}, ip={7}",
                    snapshot.DeviceId,
                    snapshot.DeviceModel,
                    snapshot.FirmwareVersion,
                    snapshot.ChannelVersion,
                    snapshot.ExpectedChannelVersion,
                    snapshot.ScreenWidth,
                    snapshot.ScreenHeight,
                    snapshot.NetworkAddress));
        }

        if (snapshot.UpdateAvailable)
        {
            AppLog.Write(
                "RokuDeploy",
                string.Format(
                    "TV desatualizada detectada: id={0}, atual={1}, esperado={2}",
                    snapshot.DeviceId,
                    snapshot.ChannelVersion,
                    snapshot.ExpectedChannelVersion));

            _rokuDevDeploymentService.TryScheduleUpdate(snapshot, snapshot.ExpectedChannelVersion);
        }
    }

    private void LogInputRequest(string requestTarget, string remoteAddress)
    {
        var queryIndex = requestTarget.IndexOf('?');
        if (queryIndex < 0 || queryIndex >= requestTarget.Length - 1)
        {
            return;
        }

        var values = ParseQueryString(requestTarget.Substring(queryIndex + 1));
        var key = GetValue(values, "key");
        var fullscreen = GetValue(values, "fullscreen");
        var selected = GetValue(values, "selected");
        PromoteInputLogToRegisteredDisplay(values, remoteAddress);

        AppLog.Write(
            "RokuInput",
            string.Format(
                "Tecla capturada: key={0}, fullscreen={1}, selected={2}, ip={3}",
                key,
                fullscreen,
                selected,
                remoteAddress));
    }

    private void PromoteInputLogToRegisteredDisplay(Dictionary<string, string> values, string remoteAddress)
    {
        var deviceId = GetValue(values, "deviceId");
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            if (string.IsNullOrWhiteSpace(remoteAddress))
            {
                return;
            }

            deviceId = "roku-" + remoteAddress.Replace(".", "-");
        }

        var channelVersion = GetValue(values, "channelVersion");
        if (string.IsNullOrWhiteSpace(channelVersion) && _registeredDisplays.TryGetValue(deviceId, out var existing))
        {
            channelVersion = existing.ChannelVersion;
        }

        var snapshot = new RegisteredDisplaySnapshot
        {
            DeviceId = deviceId,
            DeviceType = "roku",
            DeviceModel = GetValue(values, "deviceModel"),
            FirmwareVersion = GetValue(values, "firmwareVersion"),
            ChannelVersion = channelVersion,
            NetworkAddress = remoteAddress,
            LastSeenUtc = DateTime.UtcNow.ToString("O")
        };

        snapshot.ExpectedChannelVersion = GetExpectedRokuChannelVersion();
        snapshot.UpdateAvailable =
            !string.IsNullOrWhiteSpace(snapshot.ExpectedChannelVersion) &&
            !string.Equals(snapshot.ChannelVersion, snapshot.ExpectedChannelVersion, StringComparison.OrdinalIgnoreCase);

        var changed = true;
        if (_registeredDisplays.TryGetValue(deviceId, out var previous))
        {
            changed =
                !string.Equals(previous.DeviceModel, snapshot.DeviceModel, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous.FirmwareVersion, snapshot.FirmwareVersion, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous.ChannelVersion, snapshot.ChannelVersion, StringComparison.OrdinalIgnoreCase) ||
                previous.UpdateAvailable != snapshot.UpdateAvailable ||
                !string.Equals(previous.NetworkAddress, snapshot.NetworkAddress, StringComparison.OrdinalIgnoreCase);
        }

        _registeredDisplays[deviceId] = snapshot;

        if (changed)
        {
            AppLog.Write(
                "Roku",
                string.Format(
                    "TV promovida via input-log: id={0}, modelo={1}, firmware={2}, canal={3}, esperado={4}, ip={5}",
                    snapshot.DeviceId,
                    snapshot.DeviceModel,
                    snapshot.FirmwareVersion,
                    snapshot.ChannelVersion,
                    snapshot.ExpectedChannelVersion,
                    snapshot.NetworkAddress));
        }

        if (snapshot.UpdateAvailable)
        {
            AppLog.Write(
                "RokuDeploy",
                string.Format(
                    "TV desatualizada detectada via input-log: id={0}, atual={1}, esperado={2}",
                    snapshot.DeviceId,
                    snapshot.ChannelVersion,
                    snapshot.ExpectedChannelVersion));

            _rokuDevDeploymentService.TryScheduleUpdate(snapshot, snapshot.ExpectedChannelVersion);
        }
    }

    private async Task<byte[]> HandleControlRequestAsync(string requestTarget, CancellationToken cancellationToken)
    {
        var queryIndex = requestTarget.IndexOf('?');
        if (queryIndex < 0 || queryIndex >= requestTarget.Length - 1)
        {
            return BuildHttpResponse(400, "{\"ok\":false,\"error\":\"missing_query\"}", "application/json; charset=utf-8");
        }

        var values = ParseQueryString(requestTarget.Substring(queryIndex + 1));
        var windowIdValue = GetValue(values, "windowId");
        var command = GetValue(values, "command");
        var x = ParseNullableInt(GetValue(values, "x"));
        var y = ParseNullableInt(GetValue(values, "y"));
        var text = GetValue(values, "text");

        if (!Guid.TryParseExact(windowIdValue, "N", out var windowId))
        {
            AppLog.Write("RokuControl", string.Format("windowId invalido em /api/control: {0}", windowIdValue));
            return BuildHttpResponse(400, "{\"ok\":false,\"error\":\"invalid_window\"}", "application/json; charset=utf-8");
        }

        var result = await _browserSnapshotService.SendRemoteCommandAsync(windowId, command, x, y, text, cancellationToken);
        AppLog.Write("RokuControl", string.Format("/api/control => janela={0}, comando={1}, x={2}, y={3}, ok={4}, editable={5}", windowId.ToString("N"), command, x, y, result.Ok, result.Editable));
        var body = SerializeJson(result);
        return BuildHttpResponse(result.Ok ? 200 : 404, body, "application/json; charset=utf-8");
    }

    private async Task<byte[]> BuildThumbnailResponseAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        var fileName = normalizedPath.Substring("/thumbnails/".Length);
        if (fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName.Substring(0, fileName.Length - 4);
        }

        if (!Guid.TryParseExact(fileName, "N", out var windowId))
        {
            return BuildHttpResponse(404, "Thumbnail nao encontrado.", "text/plain; charset=utf-8");
        }

        var jpegBytes = await _browserSnapshotService.CaptureJpegAsync(windowId, cancellationToken);
        if (jpegBytes is null || jpegBytes.Length == 0)
        {
            return BuildHttpResponse(404, "Thumbnail indisponivel.", "text/plain; charset=utf-8");
        }

        return BuildBinaryHttpResponse(200, jpegBytes, "image/jpeg");
    }

    private static string ParsePath(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            return "/";
        }

        return parts[1];
    }

    private static string ParseMethod(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return "GET";
        }

        return parts[0].Trim().ToUpperInvariant();
    }

    private static byte[] BuildHttpResponse(int statusCode, string body, string contentType)
    {
        var reason =
            statusCode == 200 ? "OK" :
            statusCode == 201 ? "Created" :
            statusCode == 400 ? "Bad Request" :
            statusCode == 404 ? "Not Found" :
            statusCode == 405 ? "Method Not Allowed" :
            "Error";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = string.Format(
            "HTTP/1.1 {0} {1}\r\nContent-Type: {2}\r\nContent-Length: {3}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n{4}",
            statusCode,
            reason,
            contentType,
            bodyBytes.Length,
            body);
        return Encoding.UTF8.GetBytes(header);
    }

    private static byte[] BuildBinaryHttpResponse(int statusCode, byte[] bodyBytes, string contentType)
    {
        var reason = statusCode == 200 ? "OK" : statusCode == 404 ? "Not Found" : "Error";
        var header = Encoding.ASCII.GetBytes(
            string.Format(
                "HTTP/1.1 {0} {1}\r\nContent-Type: {2}\r\nContent-Length: {3}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n",
                statusCode,
                reason,
                contentType,
                bodyBytes.Length));

        var response = new byte[header.Length + bodyBytes.Length];
        Buffer.BlockCopy(header, 0, response, 0, header.Length);
        Buffer.BlockCopy(bodyBytes, 0, response, header.Length, bodyBytes.Length);
        return response;
    }

    private static string BuildIndexPage()
    {
        return "<html><body style='font-family:Segoe UI;padding:24px'><h1>Publicacao local LinkRTC</h1><p>Servidor ativo.</p><p>Cada janela publicada usa o nickname dela como rota. Exemplo: <code>/sala1</code>.</p></body></html>";
    }

    private static string BuildPublishedPage(PublishedWindowRoute route)
    {
        return string.Format(
            "<html><head><title>{0}</title><style>html,body,iframe{{margin:0;padding:0;width:100%;height:100%;border:0;background:#111;color:#fff;font-family:Segoe UI}}</style></head><body><iframe src='{1}' allow='autoplay; fullscreen'></iframe></body></html>",
            WebUtility.HtmlEncode(route.Title),
            WebUtility.HtmlEncode(route.SourceUrl));
    }

    private string BuildWindowsJson()
    {
        var payload = new WindowsBridgePayload();
        foreach (var snapshot in _windowSnapshots.Values)
        {
            payload.Windows.Add(snapshot);
        }

        foreach (var display in _registeredDisplays.Values)
        {
            payload.Displays.Add(display);
        }

        payload.Windows.Sort((left, right) => string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase));
        payload.WindowCount = payload.Windows.Count;

        using (var stream = new MemoryStream())
        {
            var serializer = new DataContractJsonSerializer(typeof(WindowsBridgePayload));
            serializer.WriteObject(stream, payload);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = query.Split('&');
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            var separatorIndex = part.IndexOf('=');
            if (separatorIndex < 0)
            {
                values[Uri.UnescapeDataString(part)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(part.Substring(0, separatorIndex));
            var value = Uri.UnescapeDataString(part.Substring(separatorIndex + 1));
            values[key] = value;
        }

        return values;
    }

    private static string SerializeJson<T>(T value)
    {
        using (var stream = new MemoryStream())
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            serializer.WriteObject(stream, value);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static string GetValue(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static int ParseInt(string value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int? ParseNullableInt(string value)
    {
        return int.TryParse(value, out var parsed) ? parsed : (int?)null;
    }

    private static async Task<byte[]> BuildFileResponseAsync(string filePath, string contentType, string? rangeHeader, bool headOnly, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            return BuildHttpResponse(404, "Arquivo nao encontrado.", "text/plain; charset=utf-8");
        }

        var totalLength = fileInfo.Length;
        long start = 0;
        long end = totalLength - 1;
        var statusCode = 200;
        var extraHeaders = new StringBuilder();
        extraHeaders.Append("Accept-Ranges: bytes\r\n");

        if (TryParseRangeHeader(rangeHeader, totalLength, out start, out end))
        {
            statusCode = 206;
            extraHeaders.AppendFormat("Content-Range: bytes {0}-{1}/{2}\r\n", start, end, totalLength);
        }

        var contentLength = end - start + 1;
        var reason = statusCode == 206 ? "Partial Content" : "OK";
        var header = Encoding.ASCII.GetBytes(
            string.Format(
                "HTTP/1.1 {0} {1}\r\nContent-Type: {2}\r\nContent-Length: {3}\r\nConnection: close\r\nCache-Control: no-store\r\n{4}\r\n",
                statusCode,
                reason,
                contentType,
                contentLength,
                extraHeaders.ToString()));

        if (headOnly)
        {
            return header;
        }

        var bodyBytes = new byte[contentLength];
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(start, SeekOrigin.Begin);
            var offset = 0;
            while (offset < contentLength)
            {
                var read = await fs.ReadAsync(bodyBytes, offset, (int)(contentLength - offset), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }
        }

        var response = new byte[header.Length + bodyBytes.Length];
        Buffer.BlockCopy(header, 0, response, 0, header.Length);
        Buffer.BlockCopy(bodyBytes, 0, response, header.Length, bodyBytes.Length);
        return response;
    }

    private static bool TryParseRangeHeader(string? rangeHeader, long totalLength, out long start, out long end)
    {
        start = 0;
        end = totalLength - 1;

        if (string.IsNullOrWhiteSpace(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = rangeHeader.Substring("bytes=".Length);
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

    private string BuildExperimentalMediaUrl(Guid windowId)
    {
        var publicHost = LinkRtcAddressBuilder.ResolvePublicHost(WebRtcBindMode.Lan, string.Empty);
        var port = (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 8090;
        return _experimentalWebRtcAvService.BuildMediaUrl(windowId, publicHost, port);
    }

    private string BuildExperimentalTransportStatus(Guid windowId, RealtimeTransportCandidate? realtimeCandidate = null)
    {
        if (realtimeCandidate is not null && (realtimeCandidate.AudioPacketsReceived > 0 || realtimeCandidate.AudioPacketsSent > 0))
        {
            return "continuous-udp-rtp-active";
        }

        if (!_experimentalAvMediaService.IsAvailable)
        {
            return "ffmpeg-unavailable";
        }

        if (_experimentalAvMediaService.TryGetMp4Path(windowId, out _))
        {
            return "bridge-media-ready";
        }

        return "awaiting-browser-media";
    }

    private BridgeWindowSnapshot BuildWindowSnapshot(WindowSession window, int port, WebRtcBindMode bindMode, string specificIp)
    {
        var publicHost = LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp);
        var publishedUrl = string.IsNullOrWhiteSpace(window.PublishedWebRtcUrl)
            ? string.Empty
            : window.PublishedWebRtcUrl;
        var experimentalAvUrl = _experimentalWebRtcAvService.IsEnabled
            ? _experimentalWebRtcAvService.BuildSessionUrl(window.Id, publicHost, port)
            : string.Empty;

        return new BridgeWindowSnapshot
        {
            Id = window.Id.ToString("N"),
            Title = string.IsNullOrWhiteSpace(window.Title) ? "Janela sem titulo" : window.Title,
            State = window.State.ToString(),
            InitialUrl = window.InitialUri?.ToString() ?? string.Empty,
            PublishedWebRtcUrl = publishedUrl,
            StreamUrl = publishedUrl,
            IsPublishing = window.IsWebRtcPublishingEnabled,
            ServerUrl = string.Format("http://{0}:{1}", publicHost, port),
            ThumbnailUrl = string.Format("http://{0}:{1}/thumbnails/{2}.jpg", publicHost, port, window.Id.ToString("N")),
            ExperimentalAvUrl = experimentalAvUrl
        };
    }

    private async Task<byte[]> BuildExperimentalWebRtcResponseAsync(string method, string requestTarget, string normalizedPath, string requestBody, string remoteAddress, Dictionary<string, string> headers, CancellationToken cancellationToken)
    {
        if (!_experimentalWebRtcAvService.IsEnabled)
        {
            return BuildHttpResponse(404, "Experimento WebRTC A/V desabilitado.", "text/plain; charset=utf-8");
        }

        var relativePath = normalizedPath.Substring("/api/experimental-av/".Length);
        var segments = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return BuildHttpResponse(404, "Janela experimental nao encontrada.", "text/plain; charset=utf-8");
        }

        var windowIdText = segments[0];
        if (!Guid.TryParseExact(windowIdText, "N", out var windowId) || !_windowSnapshots.TryGetValue(windowId, out var snapshot))
        {
            return BuildHttpResponse(404, "Janela experimental nao encontrada.", "text/plain; charset=utf-8");
        }

        var routeSuffix = segments.Length > 1 ? segments[1] : string.Empty;
        var sessionState = _experimentalWebRtcAvService.GetOrCreateSession(windowId, snapshot.Title, snapshot.InitialUrl, snapshot.ExperimentalAvUrl);
        var realtimeCandidate = _experimentalRealtimeTransportService.GetOrCreate(windowId, LinkRtcAddressBuilder.ResolvePublicHost(WebRtcBindMode.Lan, string.Empty));
        sessionState.MediaUrl = BuildExperimentalMediaUrl(windowId);
        sessionState.RealtimeMode = realtimeCandidate?.Mode ?? string.Empty;
        sessionState.RealtimeProtocol = realtimeCandidate?.Protocol ?? string.Empty;
        sessionState.RealtimeHost = realtimeCandidate?.Host ?? string.Empty;
        sessionState.RealtimeAudioPort = realtimeCandidate?.AudioPort ?? 0;
        sessionState.RealtimeVideoPort = realtimeCandidate?.VideoPort ?? 0;
        if (realtimeCandidate is not null)
        {
            var sampleRate = realtimeCandidate.AudioSampleRate > 0 ? realtimeCandidate.AudioSampleRate : 44100;
            var channels = realtimeCandidate.AudioChannels > 0 ? realtimeCandidate.AudioChannels : 2;
            sessionState.AnswerSdp = ExperimentalWebRtcAvService.BuildAudioRtpAnswerSdp(
                windowId,
                realtimeCandidate.Host,
                realtimeCandidate.AudioPort,
                sampleRate,
                channels,
                realtimeCandidate.AudioPayloadType);
        }
        sessionState.RealtimeTransportReady = realtimeCandidate?.Ready ?? false;
        sessionState.RealtimeAudioPacketsReceived = realtimeCandidate?.AudioPacketsReceived ?? 0;
        sessionState.RealtimeAudioBytesReceived = realtimeCandidate?.AudioBytesReceived ?? 0;
        sessionState.RealtimeVideoPacketsReceived = realtimeCandidate?.VideoPacketsReceived ?? 0;
        sessionState.RealtimeVideoBytesReceived = realtimeCandidate?.VideoBytesReceived ?? 0;
        sessionState.RealtimeLastPacketUtc = realtimeCandidate?.LastPacketUtc ?? string.Empty;
        sessionState.RealtimeAudioPacketsSent = realtimeCandidate?.AudioPacketsSent ?? 0;
        sessionState.RealtimeAudioBytesSent = realtimeCandidate?.AudioBytesSent ?? 0;
        sessionState.MediaVersion = _experimentalAvMediaService.GetMediaVersion(windowId);
        _experimentalAvMediaService.EnsureStarted(windowId);
        sessionState.TransportStatus = BuildExperimentalTransportStatus(windowId, realtimeCandidate);
        sessionState.MediaTransportImplemented = _experimentalAvMediaService.TryGetMp4Path(windowId, out _);
        sessionState.MediaReady = sessionState.MediaTransportImplemented;

        if (string.Equals(routeSuffix, "state", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return BuildHttpResponse(405, "{\"ok\":false,\"error\":\"method_not_allowed\"}", "application/json; charset=utf-8");
            }

            AppLog.Write(
                "ExpWebRtc",
                string.Format(
                    "State experimental solicitado: janela={0}, offers={1}, status={2}",
                    windowId.ToString("N"),
                    sessionState.OfferCount,
                    sessionState.Status));
            return BuildHttpResponse(200, _experimentalWebRtcAvService.BuildStateJson(sessionState), "application/json; charset=utf-8");
        }

        if (string.Equals(routeSuffix, "media", StringComparison.OrdinalIgnoreCase))
        {
            var queryValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var queryIndex = requestTarget.IndexOf('?');
            if (queryIndex >= 0 && queryIndex < requestTarget.Length - 1)
            {
                queryValues = ParseQueryString(requestTarget.Substring(queryIndex + 1));
            }

            AppLog.Write(
                "ExpWebRtc",
                string.Format(
                    "Midia experimental solicitada: janela={0}, transportStatus={1}",
                    windowId.ToString("N"),
                    sessionState.TransportStatus));

            if (queryValues.TryGetValue("probe", out var probeValue) && string.Equals(probeValue, "1", StringComparison.OrdinalIgnoreCase))
            {
                var ready = _experimentalAvMediaService.TryGetMp4Path(windowId, out _);
                var mediaVersion = _experimentalAvMediaService.GetMediaVersion(windowId);
                return BuildHttpResponse(200, ready ? "{\"ready\":true,\"mediaVersion\":" + mediaVersion.ToString() + "}" : "{\"ready\":false,\"mediaVersion\":0}", "application/json; charset=utf-8");
            }

            if (_experimentalAvMediaService.TryGetMp4Path(windowId, out var mp4Path))
            {
                headers.TryGetValue("Range", out var rangeHeader);
                return await BuildFileResponseAsync(mp4Path, "video/mp4", rangeHeader, string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase), cancellationToken);
            }

            return BuildHttpResponse(501, "Experimental AV media transport not implemented yet.", "text/plain; charset=utf-8");
        }

        if (string.Equals(routeSuffix, "offer", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return BuildHttpResponse(405, "{\"ok\":false,\"error\":\"method_not_allowed\"}", "application/json; charset=utf-8");
            }

            ExperimentalWebRtcOfferPayload? payload = null;
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(requestBody ?? string.Empty)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ExperimentalWebRtcOfferPayload));
                    payload = serializer.ReadObject(stream) as ExperimentalWebRtcOfferPayload;
                }
            }
            catch
            {
                payload = null;
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.Type))
            {
                return BuildHttpResponse(400, "{\"ok\":false,\"error\":\"invalid_offer\"}", "application/json; charset=utf-8");
            }

            _experimentalAvMediaService.Invalidate(windowId);
            _experimentalRealtimeTransportService.Invalidate(windowId);
            var mediaUrl = BuildExperimentalMediaUrl(windowId);
            var updated = _experimentalWebRtcAvService.RegisterOffer(windowId, snapshot.Title, snapshot.InitialUrl, snapshot.ExperimentalAvUrl, mediaUrl, payload);
            var publicHost = LinkRtcAddressBuilder.ResolvePublicHost(WebRtcBindMode.Lan, string.Empty);
            var configuredRealtimeCandidate = _experimentalRealtimeTransportService.GetOrCreate(windowId, publicHost);
            if (configuredRealtimeCandidate is not null
                && payload.ReceiverAudioPort > 0
                && string.Equals(payload.ReceiverProtocol, "udp-rtp", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(remoteAddress))
            {
                _experimentalRealtimeTransportService.ConfigureRemoteAudioTarget(windowId, remoteAddress, payload.ReceiverAudioPort);
                configuredRealtimeCandidate = _experimentalRealtimeTransportService.GetOrCreate(windowId, publicHost);
            }

            if (configuredRealtimeCandidate is not null)
            {
                updated.RealtimeMode = configuredRealtimeCandidate.Mode;
                updated.RealtimeProtocol = configuredRealtimeCandidate.Protocol;
                updated.RealtimeHost = configuredRealtimeCandidate.Host;
                updated.RealtimeAudioPort = configuredRealtimeCandidate.AudioPort;
                updated.RealtimeVideoPort = configuredRealtimeCandidate.VideoPort;
                updated.RealtimeTransportReady = configuredRealtimeCandidate.Ready;
                updated.RealtimeAudioPacketsReceived = configuredRealtimeCandidate.AudioPacketsReceived;
                updated.RealtimeAudioBytesReceived = configuredRealtimeCandidate.AudioBytesReceived;
                updated.RealtimeVideoPacketsReceived = configuredRealtimeCandidate.VideoPacketsReceived;
                updated.RealtimeVideoBytesReceived = configuredRealtimeCandidate.VideoBytesReceived;
                updated.RealtimeLastPacketUtc = configuredRealtimeCandidate.LastPacketUtc ?? string.Empty;
                updated.RealtimeAudioPacketsSent = configuredRealtimeCandidate.AudioPacketsSent;
                updated.RealtimeAudioBytesSent = configuredRealtimeCandidate.AudioBytesSent;
                var sampleRate = configuredRealtimeCandidate.AudioSampleRate > 0 ? configuredRealtimeCandidate.AudioSampleRate : 44100;
                var channels = configuredRealtimeCandidate.AudioChannels > 0 ? configuredRealtimeCandidate.AudioChannels : 2;
                updated.AnswerSdp = ExperimentalWebRtcAvService.BuildAudioRtpAnswerSdp(
                    windowId,
                    configuredRealtimeCandidate.Host,
                    configuredRealtimeCandidate.AudioPort,
                    sampleRate,
                    channels,
                    configuredRealtimeCandidate.AudioPayloadType);
            }
            AppLog.Write(
                "ExpWebRtc",
                string.Format(
                    "Offer experimental recebida: janela={0}, tipo={1}, source={2}, count={3}",
                    windowId.ToString("N"),
                    updated.LastOfferType,
                    updated.LastOfferSource,
                    updated.OfferCount));

            var accepted = new ExperimentalWebRtcOfferAccepted
            {
                Ok = true,
                Status = updated.Status,
                WindowId = updated.WindowId,
                OfferCount = updated.OfferCount,
                StateUrl = snapshot.ExperimentalAvUrl + "/state",
                AnswerType = updated.AnswerType,
                AnswerSdp = updated.AnswerSdp,
                MediaUrl = updated.MediaUrl,
                TransportStatus = updated.TransportStatus,
                RealtimeMode = updated.RealtimeMode,
                RealtimeProtocol = updated.RealtimeProtocol,
                RealtimeHost = updated.RealtimeHost,
                RealtimeAudioPort = updated.RealtimeAudioPort,
                RealtimeVideoPort = updated.RealtimeVideoPort,
                RealtimeTransportReady = updated.RealtimeTransportReady,
                RealtimeAudioPacketsReceived = updated.RealtimeAudioPacketsReceived,
                RealtimeAudioBytesReceived = updated.RealtimeAudioBytesReceived,
                RealtimeVideoPacketsReceived = updated.RealtimeVideoPacketsReceived,
                RealtimeVideoBytesReceived = updated.RealtimeVideoBytesReceived,
                RealtimeLastPacketUtc = updated.RealtimeLastPacketUtc,
                RealtimeAudioPacketsSent = updated.RealtimeAudioPacketsSent,
                RealtimeAudioBytesSent = updated.RealtimeAudioBytesSent,
                MediaVersion = updated.MediaVersion,
                Notes = new List<string>
                {
                    "Offer recebida pelo super.",
                    "Resposta SDP placeholder gerada.",
                    "O transporte experimental atual usa frame atual do painel com audio recente capturado do navegador.",
                    "Candidatos UDP experimentais foram reservados para a proxima etapa de transporte continuo."
                }
            };

            return BuildHttpResponse(201, SerializeJson(accepted), "application/json; charset=utf-8");
        }

        if (!string.IsNullOrWhiteSpace(routeSuffix))
        {
            return BuildHttpResponse(404, "Rota experimental nao encontrada.", "text/plain; charset=utf-8");
        }

        AppLog.Write(
            "ExpWebRtc",
            string.Format(
                "Sessao experimental solicitada: janela={0}, titulo={1}",
                windowId.ToString("N"),
                snapshot.Title));

        var sessionInfo = new WindowSessionSessionInfo
        {
            WindowId = snapshot.Id,
            Title = snapshot.Title,
            InitialUrl = snapshot.InitialUrl,
            SignalingUrl = snapshot.ExperimentalAvUrl,
            OfferUrl = snapshot.ExperimentalAvUrl + "/offer",
            StateUrl = snapshot.ExperimentalAvUrl + "/state",
            SupportedTransports = new List<string>
            {
                "session-discovery",
                "offer-post",
                "state-poll",
                "media-endpoint",
                "continuous-udp-prototype"
            },
            MediaUrl = BuildExperimentalMediaUrl(windowId),
            TransportStatus = BuildExperimentalTransportStatus(windowId, realtimeCandidate),
            MediaTransportImplemented = _experimentalAvMediaService.TryGetMp4Path(windowId, out _),
            RealtimeMode = sessionState.RealtimeMode,
            RealtimeProtocol = sessionState.RealtimeProtocol,
            RealtimeHost = sessionState.RealtimeHost,
            RealtimeAudioPort = sessionState.RealtimeAudioPort,
            RealtimeVideoPort = sessionState.RealtimeVideoPort,
            RealtimeTransportReady = sessionState.RealtimeTransportReady,
            RealtimeAudioPacketsReceived = sessionState.RealtimeAudioPacketsReceived,
            RealtimeAudioBytesReceived = sessionState.RealtimeAudioBytesReceived,
            RealtimeVideoPacketsReceived = sessionState.RealtimeVideoPacketsReceived,
            RealtimeVideoBytesReceived = sessionState.RealtimeVideoBytesReceived,
            RealtimeLastPacketUtc = sessionState.RealtimeLastPacketUtc,
            RealtimeAudioPacketsSent = sessionState.RealtimeAudioPacketsSent,
            RealtimeAudioBytesSent = sessionState.RealtimeAudioBytesSent,
            MediaVersion = sessionState.MediaVersion,
            Notes = new List<string>
            {
                "Foundation plus signaling: esta branch ja aceita POST de offer e exibe state da sessao experimental.",
                "O transporte experimental atual usa frame atual do painel com audio recente capturado do navegador.",
                "O super agora publica candidatos UDP de fundacao para a proxima etapa de transporte continuo."
            }
        };

        return BuildHttpResponse(200, _experimentalWebRtcAvService.BuildSessionJson(sessionInfo), "application/json; charset=utf-8");
    }

    private void RemoveExistingRoute(Guid windowId)
    {
        if (_windowRouteKeys.TryRemove(windowId, out var previousRouteKey))
        {
            _routes.TryRemove(previousRouteKey, out _);
        }
    }

    private static string BuildRouteKey(int port, string slug)
    {
        return string.Format("{0}:{1}", port, slug);
    }

    private static string GetExpectedRokuChannelVersion()
    {
        var currentChannel = UpdateChannelNames.Normalize(BuildVersionInfo.CurrentBuildChannel);
        if (string.Equals(currentChannel, UpdateChannelNames.Local, StringComparison.OrdinalIgnoreCase))
        {
            var localPackageReleaseId = TryReadLocalRokuPackageReleaseId();
            if (!string.IsNullOrWhiteSpace(localPackageReleaseId))
            {
                return localPackageReleaseId;
            }
        }

        if (!string.IsNullOrWhiteSpace(BuildVersionInfo.ReleaseId))
        {
            return BuildVersionInfo.ReleaseId;
        }

        try
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var manifestPath = Path.Combine(current.FullName, "manifest");
                var superPath = Path.Combine(current.FullName, "super");
                if (File.Exists(manifestPath) && Directory.Exists(superPath))
                {
                    var major = "0";
                    var minor = "0";
                    var build = "0";

                    foreach (var line in File.ReadAllLines(manifestPath))
                    {
                        if (line.StartsWith("major_version=", StringComparison.OrdinalIgnoreCase))
                        {
                            major = line.Substring("major_version=".Length).Trim();
                        }
                        else if (line.StartsWith("minor_version=", StringComparison.OrdinalIgnoreCase))
                        {
                            minor = line.Substring("minor_version=".Length).Trim();
                        }
                        else if (line.StartsWith("build_version=", StringComparison.OrdinalIgnoreCase))
                        {
                            build = line.Substring("build_version=".Length).Trim();
                        }
                    }

                    return string.Format("{0}.{1}.{2}", major, minor, build);
                }

                current = current.Parent;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string TryReadLocalRokuPackageReleaseId()
    {
        try
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var manifestPath = Path.Combine(current.FullName, "manifest");
                var localPackagePath = Path.Combine(current.FullName, UpdateChannelNames.Local + "-roku.zip");
                if (File.Exists(manifestPath) && File.Exists(localPackagePath))
                {
                    using (var archive = ZipFile.OpenRead(localPackagePath))
                    {
                        var entry = archive.GetEntry("source/BuildInfo.brs");
                        if (entry is null)
                        {
                            return string.Empty;
                        }

                        using (var stream = entry.Open())
                        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                        {
                            var content = reader.ReadToEnd();
                            var marker = "return \"";
                            var startIndex = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                            if (startIndex < 0)
                            {
                                return string.Empty;
                            }

                            startIndex += marker.Length;
                            var endIndex = content.IndexOf('"', startIndex);
                            if (endIndex <= startIndex)
                            {
                                return string.Empty;
                            }

                            return content.Substring(startIndex, endIndex - startIndex).Trim();
                        }
                    }
                }

                current = current.Parent;
            }
        }
        catch
        {
        }

        return string.Empty;
    }
}

[DataContract]
public sealed class WindowsBridgePayload
{
    [DataMember(Name = "windowCount", Order = 1)]
    public int WindowCount { get; set; }

    [DataMember(Name = "windows", Order = 2)]
    public List<BridgeWindowSnapshot> Windows { get; set; } = new List<BridgeWindowSnapshot>();

    [DataMember(Name = "displays", Order = 3)]
    public List<RegisteredDisplaySnapshot> Displays { get; set; } = new List<RegisteredDisplaySnapshot>();
}

[DataContract]
public sealed class BridgeWindowSnapshot
{
    [DataMember(Name = "id", Order = 1)]
    public string Id { get; set; } = string.Empty;

    [DataMember(Name = "title", Order = 2)]
    public string Title { get; set; } = string.Empty;

    [DataMember(Name = "state", Order = 3)]
    public string State { get; set; } = string.Empty;

    [DataMember(Name = "initialUrl", Order = 4)]
    public string InitialUrl { get; set; } = string.Empty;

    [DataMember(Name = "publishedWebRtcUrl", Order = 5)]
    public string PublishedWebRtcUrl { get; set; } = string.Empty;

    [DataMember(Name = "streamUrl", Order = 6)]
    public string StreamUrl { get; set; } = string.Empty;

    [DataMember(Name = "isPublishing", Order = 7)]
    public bool IsPublishing { get; set; }

    [DataMember(Name = "serverUrl", Order = 8)]
    public string ServerUrl { get; set; } = string.Empty;

    [DataMember(Name = "thumbnailUrl", Order = 9)]
    public string ThumbnailUrl { get; set; } = string.Empty;

    [DataMember(Name = "experimentalAvUrl", Order = 10)]
    public string ExperimentalAvUrl { get; set; } = string.Empty;
}

[DataContract]
public sealed class RegisteredDisplaySnapshot
{
    [DataMember(Name = "deviceId", Order = 1)]
    public string DeviceId { get; set; } = string.Empty;

    [DataMember(Name = "deviceType", Order = 2)]
    public string DeviceType { get; set; } = string.Empty;

    [DataMember(Name = "deviceModel", Order = 3)]
    public string DeviceModel { get; set; } = string.Empty;

    [DataMember(Name = "firmwareVersion", Order = 4)]
    public string FirmwareVersion { get; set; } = string.Empty;

    [DataMember(Name = "channelVersion", Order = 5)]
    public string ChannelVersion { get; set; } = string.Empty;

    [DataMember(Name = "expectedChannelVersion", Order = 6)]
    public string ExpectedChannelVersion { get; set; } = string.Empty;

    [DataMember(Name = "updateAvailable", Order = 7)]
    public bool UpdateAvailable { get; set; }

    [DataMember(Name = "screenWidth", Order = 8)]
    public int ScreenWidth { get; set; }

    [DataMember(Name = "screenHeight", Order = 9)]
    public int ScreenHeight { get; set; }

    [DataMember(Name = "networkAddress", Order = 10)]
    public string NetworkAddress { get; set; } = string.Empty;

    [DataMember(Name = "lastSeenUtc", Order = 11)]
    public string LastSeenUtc { get; set; } = string.Empty;
}

