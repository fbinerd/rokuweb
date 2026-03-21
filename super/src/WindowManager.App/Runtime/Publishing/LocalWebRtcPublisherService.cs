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
    private static readonly string BridgeDebugLogPath = Path.Combine(AppDataPaths.Root, "bridge-debug.log");
    private readonly BrowserSnapshotService _browserSnapshotService;
    private readonly BrowserAudioCaptureService _browserAudioCaptureService;
    private readonly DiagnosticAvHlsService _diagnosticAvHlsService;
    private readonly DiagnosticMediaHttpServer _diagnosticMediaHttpServer;
    private readonly RokuDevDeploymentService _rokuDevDeploymentService;
    private readonly object _listenerGate = new object();
    private readonly ConcurrentDictionary<string, PublishedWindowRoute> _routes = new ConcurrentDictionary<string, PublishedWindowRoute>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string> _windowRouteKeys = new ConcurrentDictionary<Guid, string>();
    private readonly ConcurrentDictionary<Guid, BridgeWindowSnapshot> _windowSnapshots = new ConcurrentDictionary<Guid, BridgeWindowSnapshot>();
    private readonly ConcurrentDictionary<string, RegisteredDisplaySnapshot> _registeredDisplays = new ConcurrentDictionary<string, RegisteredDisplaySnapshot>(StringComparer.OrdinalIgnoreCase);

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCancellation;
    private string _activeListenerKey = string.Empty;
    private int _activeListenerPort;

    public LocalWebRtcPublisherService(BrowserSnapshotService browserSnapshotService, BrowserAudioCaptureService browserAudioCaptureService, DiagnosticAvHlsService diagnosticAvHlsService, DiagnosticMediaHttpServer diagnosticMediaHttpServer, AppUpdatePreferenceStore appUpdatePreferenceStore)
    {
        _browserSnapshotService = browserSnapshotService;
        _browserAudioCaptureService = browserAudioCaptureService;
        _diagnosticAvHlsService = diagnosticAvHlsService;
        _diagnosticMediaHttpServer = diagnosticMediaHttpServer;
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
            var snapshot = BuildWindowSnapshot(window, activePort, bindMode, specificIp);
            _windowSnapshots[window.Id] = snapshot;
            activeIds.Add(window.Id);
        }

        _diagnosticAvHlsService.EnsureStarted();
        if (_diagnosticAvHlsService.IsAvailable)
        {
            _diagnosticMediaHttpServer.TryEnsureStarted(LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), activePort + 20, out _);
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

            if (_listener is not null && _activeListenerPort > 0 && _activeListenerPort >= preferredPort && _activeListenerPort <= preferredPort + 3)
            {
                return _activeListenerPort;
            }

            StopListener();

            Exception? lastError = null;
            for (var candidatePort = preferredPort; candidatePort <= preferredPort + 3; candidatePort++)
            {
                var candidateEndpoint = new IPEndPoint(endpoint.Address, candidatePort);
                var listenerKey = string.Format("{0}:{1}", candidateEndpoint.Address, candidateEndpoint.Port);

                try
                {
                    StartListener(candidateEndpoint, listenerKey);
                    return candidatePort;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    WriteBridgeDebug("PORT fallback failed " + candidatePort.ToString() + " => " + ex.Message);
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
        WriteBridgeDebug("START listener " + listenerKey);

        _listener = listener;
        _listenerCancellation = cancellation;
        _activeListenerKey = listenerKey;
        _activeListenerPort = endpoint.Port;

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
        _activeListenerPort = 0;
    }

    private async Task ListenLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        WriteBridgeDebug("LISTEN loop active");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                WriteBridgeDebug("ACCEPT " + ((client.Client.RemoteEndPoint as IPEndPoint)?.ToString() ?? "(unknown)"));
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

                WriteBridgeDebug("SOCKET error in listen loop");
            }
            catch
            {
                WriteBridgeDebug("UNHANDLED error in listen loop");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
        {
            string? requestLine;
            Dictionary<string, string>? requestHeaders = null;
            try
            {
                requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? headerLine;
                do
                {
                    headerLine = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(headerLine))
                    {
                        var separatorIndex = headerLine.IndexOf(':');
                        if (separatorIndex > 0)
                        {
                            var key = headerLine.Substring(0, separatorIndex).Trim();
                            var value = headerLine.Substring(separatorIndex + 1).Trim();
                            requestHeaders[key] = value;
                        }
                    }
                }
                while (!string.IsNullOrEmpty(headerLine));
            }
            catch
            {
                return;
            }

            var method = ParseRequestMethod(requestLine);
            var path = ParseRequestPath(requestLine);
            var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
            try
            {
                WriteBridgeDebug("HANDLE " + path + " from " + (string.IsNullOrWhiteSpace(remoteAddress) ? "(unknown)" : remoteAddress));
                var response = await BuildResponseAsync(path, remoteAddress, requestHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), cancellationToken);
                var bytes = BuildRawHttpResponse(response, string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase));
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                WriteBridgeDebug("RESPONDED " + path + " bytes=" + response.BodyBytes.Length.ToString());
            }
            catch (Exception ex)
            {
                WriteBridgeDebug("ERROR " + path + " => " + ex.Message);
                AppLog.Write(
                    "Bridge",
                    string.Format(
                        "Falha ao responder requisicao '{0}' de {1}: {2}",
                        path,
                        string.IsNullOrWhiteSpace(remoteAddress) ? "(desconhecido)" : remoteAddress,
                        ex.Message));

                var response = BuildHttpResponse(500, "Bridge failure", "text/plain; charset=utf-8");
                var bytes = BuildRawHttpResponse(response, string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase));
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void WriteBridgeDebug(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BridgeDebugLogPath) ?? AppDataPaths.Root);
            File.AppendAllText(
                BridgeDebugLogPath,
                string.Format("[{0:HH:mm:ss.fff}] {1}{2}", DateTime.Now, message, Environment.NewLine));
        }
        catch
        {
        }
    }

    private async Task<BridgeHttpResponse> BuildResponseAsync(string path, string remoteAddress, Dictionary<string, string> requestHeaders, CancellationToken cancellationToken)
    {
        var requestTarget = path ?? "/";
        var normalizedPath = requestTarget.Split('?')[0];

        if (normalizedPath.StartsWith("/thumbnails/", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildThumbnailResponseAsync(normalizedPath, cancellationToken);
        }

        if (normalizedPath.StartsWith("/diag-av/", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildDiagnosticAvResponseAsync(normalizedPath, requestHeaders, cancellationToken);
        }

        if (normalizedPath.StartsWith("/audio/", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildPanelAudioResponseAsync(normalizedPath, cancellationToken);
        }

        if (string.Equals(normalizedPath, "/health", StringComparison.OrdinalIgnoreCase))
        {
            return BuildHttpResponse(200, "ok", "text/plain; charset=utf-8");
        }

        if (string.Equals(normalizedPath, "/api/windows", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Write(
                "Bridge",
                string.Format(
                    "/api/windows solicitado por {0}. Janelas={1}",
                    string.IsNullOrWhiteSpace(remoteAddress) ? "(desconhecido)" : remoteAddress,
                    _windowSnapshots.Count));
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

        var port = _activeListenerPort <= 0 ? 8088 : _activeListenerPort;
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

    private async Task<BridgeHttpResponse> HandleControlRequestAsync(string requestTarget, CancellationToken cancellationToken)
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

    private async Task<BridgeHttpResponse> BuildThumbnailResponseAsync(string normalizedPath, CancellationToken cancellationToken)
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

    private async Task<BridgeHttpResponse> BuildDiagnosticAvResponseAsync(string normalizedPath, Dictionary<string, string> requestHeaders, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_diagnosticAvHlsService.IsAvailable)
        {
            return BuildHttpResponse(404, "Stream A/V diagnostico indisponivel.", "text/plain; charset=utf-8");
        }

        var fileName = normalizedPath.Substring("/diag-av/".Length);
        if (string.Equals(fileName, "diagnostic.mp4", StringComparison.OrdinalIgnoreCase))
        {
            if (!_diagnosticAvHlsService.TryGetMp4Path(out var mp4Path))
            {
                return BuildHttpResponse(404, "Arquivo A/V diagnostico MP4 indisponivel.", "text/plain; charset=utf-8");
            }

            requestHeaders.TryGetValue("Range", out var rangeHeader);
            return await BuildFileHttpResponseAsync(mp4Path, "video/mp4", rangeHeader, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(fileName, "diagnostic.mp3", StringComparison.OrdinalIgnoreCase))
        {
            if (!_diagnosticAvHlsService.TryGetMp3Path(out var mp3Path))
            {
                return BuildHttpResponse(404, "Arquivo de audio diagnostico MP3 indisponivel.", "text/plain; charset=utf-8");
            }

            var mp3Bytes = await Task.Run(() => File.ReadAllBytes(mp3Path), cancellationToken).ConfigureAwait(false);
            return BuildBinaryHttpResponse(200, mp3Bytes, "audio/mpeg");
        }

        if (string.Equals(fileName, "diagnostic.ts", StringComparison.OrdinalIgnoreCase))
        {
            if (!_diagnosticAvHlsService.TryGetTsPath(out var tsPath))
            {
                return BuildHttpResponse(404, "Arquivo A/V diagnostico TS indisponivel.", "text/plain; charset=utf-8");
            }

            requestHeaders.TryGetValue("Range", out var rangeHeader);
            return await BuildFileHttpResponseAsync(tsPath, "video/mp2t", rangeHeader, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(fileName, "index.m3u8", StringComparison.OrdinalIgnoreCase))
        {
            if (!_diagnosticAvHlsService.TryGetPlaylistPath(out var playlistPath))
            {
                return BuildHttpResponse(404, "Playlist A/V diagnostica indisponivel.", "text/plain; charset=utf-8");
            }

            var bytes = await Task.Run(() => File.ReadAllBytes(playlistPath), cancellationToken).ConfigureAwait(false);
            return BuildBinaryHttpResponse(200, bytes, "application/vnd.apple.mpegurl");
        }

        if (!_diagnosticAvHlsService.TryGetSegmentPath(fileName, out var segmentPath))
        {
            return BuildHttpResponse(404, "Segmento A/V diagnostico indisponivel.", "text/plain; charset=utf-8");
        }

        var segmentBytes = await Task.Run(() => File.ReadAllBytes(segmentPath), cancellationToken).ConfigureAwait(false);
        var extension = Path.GetExtension(segmentPath);
        var contentType = string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".m4s", StringComparison.OrdinalIgnoreCase)
            ? "video/mp4"
            : "video/mp2t";
        return BuildBinaryHttpResponse(200, segmentBytes, contentType);
    }

    private async Task<BridgeHttpResponse> BuildPanelAudioResponseAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fileName = normalizedPath.Substring("/audio/".Length);
        if (fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName.Substring(0, fileName.Length - 4);
        }

        if (!Guid.TryParseExact(fileName, "N", out var windowId))
        {
            return BuildHttpResponse(404, "Audio do painel nao encontrado.", "text/plain; charset=utf-8");
        }

        var waveBytes = _browserAudioCaptureService.CaptureWaveSnapshot(windowId);
        if (waveBytes is null || waveBytes.Length == 0)
        {
            return BuildHttpResponse(404, "Audio do painel indisponivel.", "text/plain; charset=utf-8");
        }

        if (!_diagnosticAvHlsService.TryGetFfmpegPath(out var ffmpegPath))
        {
            return BuildHttpResponse(503, "ffmpeg indisponivel para audio MP3 do painel.", "text/plain; charset=utf-8");
        }

        var mp3Bytes = await Task.Run(() => EncodeWaveToMp3(ffmpegPath, waveBytes), cancellationToken).ConfigureAwait(false);
        if (mp3Bytes is null || mp3Bytes.Length == 0)
        {
            return BuildHttpResponse(404, "Falha ao gerar audio MP3 do painel.", "text/plain; charset=utf-8");
        }

        return BuildBinaryHttpResponse(200, mp3Bytes, "audio/mpeg");
    }

    private static string ParseRequestMethod(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 1)
        {
            return "GET";
        }

        return parts[0];
    }

    private static string ParseRequestPath(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            return "/";
        }

        return parts[1];
    }

    private static byte[] BuildRawHttpResponse(BridgeHttpResponse response, bool headOnly = false)
    {
        var reason = response.StatusCode == 200 ? "OK"
            : response.StatusCode == 206 ? "Partial Content"
            : response.StatusCode == 404 ? "Not Found"
            : response.StatusCode == 400 ? "Bad Request"
            : response.StatusCode == 416 ? "Range Not Satisfiable"
            : "Error";
        var headerBuilder = new StringBuilder();
        headerBuilder.AppendFormat("HTTP/1.1 {0} {1}\r\n", response.StatusCode, reason);
        headerBuilder.AppendFormat("Content-Type: {0}\r\n", response.ContentType);
        headerBuilder.AppendFormat("Content-Length: {0}\r\n", response.BodyBytes.Length);
        headerBuilder.Append("Connection: close\r\n");
        headerBuilder.Append("Cache-Control: no-store\r\n");
        foreach (var responseHeader in response.Headers)
        {
            headerBuilder.AppendFormat("{0}: {1}\r\n", responseHeader.Key, responseHeader.Value);
        }
        headerBuilder.Append("\r\n");
        var header = Encoding.ASCII.GetBytes(headerBuilder.ToString());

        if (headOnly)
        {
            return header;
        }

        var result = new byte[header.Length + response.BodyBytes.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(response.BodyBytes, 0, result, header.Length, response.BodyBytes.Length);
        return result;
    }

    private static byte[]? EncodeWaveToMp3(string ffmpegPath, byte[] waveBytes)
    {
        var tempRoot = Path.Combine(AppDataPaths.Root, "panel-audio-mp3");
        Directory.CreateDirectory(tempRoot);
        var baseName = Guid.NewGuid().ToString("N");
        var wavePath = Path.Combine(tempRoot, baseName + ".wav");
        var mp3Path = Path.Combine(tempRoot, baseName + ".mp3");

        try
        {
            File.WriteAllBytes(wavePath, waveBytes);
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = string.Format(
                    "-hide_banner -loglevel error -y -i \"{0}\" -c:a libmp3lame -b:a 128k -ar 44100 -ac 2 \"{1}\"",
                    wavePath,
                    mp3Path),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using (var process = System.Diagnostics.Process.Start(startInfo))
            {
                if (process is null)
                {
                    return null;
                }

                process.WaitForExit(5000);
                if (!process.HasExited || process.ExitCode != 0 || !File.Exists(mp3Path))
                {
                    return null;
                }
            }

            return File.ReadAllBytes(mp3Path);
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(wavePath))
                {
                    File.Delete(wavePath);
                }
            }
            catch
            {
            }

            try
            {
                if (File.Exists(mp3Path))
                {
                    File.Delete(mp3Path);
                }
            }
            catch
            {
            }
        }
    }

    private static BridgeHttpResponse BuildHttpResponse(int statusCode, string body, string contentType)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        return new BridgeHttpResponse(statusCode, contentType, bodyBytes);
    }

    private static BridgeHttpResponse BuildBinaryHttpResponse(int statusCode, byte[] bodyBytes, string contentType)
    {
        return new BridgeHttpResponse(statusCode, contentType, bodyBytes);
    }

    private static async Task<BridgeHttpResponse> BuildFileHttpResponseAsync(string filePath, string contentType, string? rangeHeader, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            return BuildHttpResponse(404, "Arquivo nao encontrado.", "text/plain; charset=utf-8");
        }

        var totalLength = fileInfo.Length;
        if (string.IsNullOrWhiteSpace(rangeHeader) || !TryParseRangeHeader(rangeHeader!, totalLength, out var start, out var end))
        {
            var fullBytes = await Task.Run(() => File.ReadAllBytes(filePath), cancellationToken).ConfigureAwait(false);
            return new BridgeHttpResponse(200, contentType, fullBytes, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept-Ranges"] = "bytes"
            });
        }

        if (start < 0 || end < start || end >= totalLength)
        {
            return new BridgeHttpResponse(416, contentType, Array.Empty<byte>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept-Ranges"] = "bytes",
                ["Content-Range"] = $"bytes */{totalLength}"
            });
        }

        var length = checked((int)(end - start + 1));
        var buffer = new byte[length];
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(start, SeekOrigin.Begin);
            var offset = 0;
            while (offset < length)
            {
                var read = await fs.ReadAsync(buffer, offset, length - offset, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }
        }

        return new BridgeHttpResponse(206, contentType, buffer, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept-Ranges"] = "bytes",
            ["Content-Range"] = $"bytes {start}-{start + buffer.Length - 1}/{totalLength}"
        });
    }

    private static bool TryParseRangeHeader(string rangeHeader, long totalLength, out long start, out long end)
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

        return long.TryParse(endText, out end);
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

    private BridgeWindowSnapshot BuildWindowSnapshot(WindowSession window, int port, WebRtcBindMode bindMode, string specificIp)
    {
        var publishedUrl = string.IsNullOrWhiteSpace(window.PublishedWebRtcUrl)
            ? string.Empty
            : window.PublishedWebRtcUrl;
        var diagnosticStreamUrl = string.Empty;
        if (_diagnosticAvHlsService.IsAvailable)
        {
            var publicHost = LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp);
            if (_diagnosticMediaHttpServer.TryEnsureStarted(publicHost, port + 20, out var diagnosticPort))
            {
                diagnosticStreamUrl = _diagnosticMediaHttpServer.BuildUrl(diagnosticPort);
            }
            else
            {
                diagnosticStreamUrl = _diagnosticAvHlsService.UsesMp4
                    ? string.Format("http://{0}:{1}/diag-av/diagnostic.mp4", publicHost, port)
                    : _diagnosticAvHlsService.UsesTs
                        ? string.Format("http://{0}:{1}/diag-av/diagnostic.ts", publicHost, port)
                    : string.Format("http://{0}:{1}/diag-av/index.m3u8", publicHost, port);
            }
        }

        return new BridgeWindowSnapshot
        {
            Id = window.Id.ToString("N"),
            Title = string.IsNullOrWhiteSpace(window.Title) ? "Janela sem titulo" : window.Title,
            State = window.State.ToString(),
            InitialUrl = window.InitialUri?.ToString() ?? string.Empty,
            PublishedWebRtcUrl = publishedUrl,
            StreamUrl = string.IsNullOrWhiteSpace(diagnosticStreamUrl) ? publishedUrl : diagnosticStreamUrl,
            IsPublishing = window.IsWebRtcPublishingEnabled,
            ServerUrl = string.Format("http://{0}:{1}", LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), port),
            ThumbnailUrl = string.Format("http://{0}:{1}/thumbnails/{2}.jpg", LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), port, window.Id.ToString("N")),
            AudioUrl = string.Format("http://{0}:{1}/audio/{2}.mp3", LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), port, window.Id.ToString("N")),
            AudioAvailable = _browserAudioCaptureService.HasRecentAudio(window.Id)
        };
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

    [DataMember(Name = "audioUrl", Order = 10)]
    public string AudioUrl { get; set; } = string.Empty;

    [DataMember(Name = "audioAvailable", Order = 11)]
    public bool AudioAvailable { get; set; }
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

internal sealed class BridgeHttpResponse
{
    public BridgeHttpResponse(int statusCode, string contentType, byte[] bodyBytes)
        : this(statusCode, contentType, bodyBytes, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
    {
    }

    public BridgeHttpResponse(int statusCode, string contentType, byte[] bodyBytes, Dictionary<string, string> headers)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        BodyBytes = bodyBytes;
        Headers = headers;
    }

    public int StatusCode { get; }

    public string ContentType { get; }

    public byte[] BodyBytes { get; }

    public Dictionary<string, string> Headers { get; }
}

