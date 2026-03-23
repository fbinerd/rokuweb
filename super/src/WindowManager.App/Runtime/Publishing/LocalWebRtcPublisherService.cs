using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
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
    private static readonly HttpClient DeviceProbeHttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private readonly BrowserSnapshotService _browserSnapshotService;
    private readonly BrowserAudioCaptureService _browserAudioCaptureService;
    private readonly BrowserAudioHlsService _browserAudioHlsService;
    private readonly BrowserPanelRollingHlsService _browserPanelRollingHlsService;
    private readonly RokuDevDeploymentService _rokuDevDeploymentService;
    private readonly object _listenerGate = new object();
    private readonly ConcurrentDictionary<string, PublishedWindowRoute> _routes = new ConcurrentDictionary<string, PublishedWindowRoute>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string> _windowRouteKeys = new ConcurrentDictionary<Guid, string>();
    private readonly ConcurrentDictionary<Guid, BridgeWindowSnapshot> _windowSnapshots = new ConcurrentDictionary<Guid, BridgeWindowSnapshot>();
    private readonly ConcurrentDictionary<string, BridgeActiveSessionSnapshot> _sessionSnapshots = new ConcurrentDictionary<string, BridgeActiveSessionSnapshot>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RegisteredDisplaySnapshot> _registeredDisplays = new ConcurrentDictionary<string, RegisteredDisplaySnapshot>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, DateTime> _lastAudioServeLogUtc = new ConcurrentDictionary<Guid, DateTime>();
    private readonly ConcurrentDictionary<string, DateTime> _lastPanelHlsLogUtc = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCancellation;
    private string _activeListenerKey = string.Empty;

    public LocalWebRtcPublisherService(BrowserSnapshotService browserSnapshotService, BrowserAudioCaptureService browserAudioCaptureService, BrowserAudioHlsService browserAudioHlsService, BrowserPanelRollingHlsService browserPanelRollingHlsService, AppUpdatePreferenceStore appUpdatePreferenceStore)
    {
        _browserSnapshotService = browserSnapshotService;
        _browserAudioCaptureService = browserAudioCaptureService;
        _browserAudioHlsService = browserAudioHlsService;
        _browserPanelRollingHlsService = browserPanelRollingHlsService;
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
        var listenerKey = string.Format("{0}:{1}", endpoint.Address, endpoint.Port);
        var publishedUrl = LinkRtcAddressBuilder.BuildPublishedUrl(session, port, bindMode, specificIp);
        var routeKey = BuildRouteKey(port, slug);

        EnsureListener(endpoint, listenerKey);
        RemoveExistingRoute(session.Id);

        _routes[routeKey] = new PublishedWindowRoute
        {
            WindowId = session.Id,
            Title = session.Title,
            SourceUrl = session.InitialUri.ToString(),
            RoutePath = slug,
            Port = port,
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

    public Task<int> ForceUpdateConnectedDisplaysAsync(CancellationToken cancellationToken)
    {
        return ForceUpdateConnectedDisplaysAsync(GetExpectedRokuChannelVersion(), cancellationToken);
    }

    public async Task<int> ForceUpdateConnectedDisplaysAsync(string expectedVersion, CancellationToken cancellationToken)
    {
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

    public Task<string> ForceUpdateDisplayTargetAsync(DisplayTarget target, CancellationToken cancellationToken)
    {
        return ForceUpdateDisplayTargetAsync(target, GetExpectedRokuChannelVersion(), cancellationToken);
    }

    public async Task<string> ForceUpdateDisplayTargetAsync(DisplayTarget target, string expectedVersion, CancellationToken cancellationToken)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.NetworkAddress))
        {
            return "tv_sem_ip";
        }

        cancellationToken.ThrowIfCancellationRequested();
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

    public async Task<RokuPowerBatchResult> SendPowerCommandToConnectedDisplaysAsync(bool powerOn, CancellationToken cancellationToken)
    {
        var displays = _registeredDisplays.Values.ToArray();
        var result = new RokuPowerBatchResult();

        foreach (var display in displays)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsPowerCompatibleRokuDisplay(display))
            {
                result.SkippedCount++;
                continue;
            }

            result.TargetedCount++;
            var commandResult = await _rokuDevDeploymentService.SendPowerCommandAsync(display, powerOn).ConfigureAwait(false);
            if (string.Equals(commandResult, "ok", StringComparison.OrdinalIgnoreCase))
            {
                result.SuccessCount++;
            }
            else
            {
                result.FailureCount++;
            }
        }

        return result;
    }

    public async Task<string> SendPowerCommandToDisplayTargetAsync(DisplayTarget target, bool powerOn, CancellationToken cancellationToken)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.NetworkAddress))
        {
            return "tv_sem_ip";
        }

        cancellationToken.ThrowIfCancellationRequested();

        var display = new RegisteredDisplaySnapshot
        {
            DeviceId = string.IsNullOrWhiteSpace(target.DeviceUniqueId)
                ? "roku-target-" + target.NetworkAddress.Replace(".", "-")
                : target.DeviceUniqueId,
            DeviceType = "roku",
            DeviceModel = target.Name,
            NetworkAddress = target.NetworkAddress,
            LastSeenUtc = DateTime.UtcNow.ToString("O")
        };

        var registered = _registeredDisplays.Values.FirstOrDefault(x =>
            string.Equals(x.NetworkAddress, target.NetworkAddress, StringComparison.OrdinalIgnoreCase));
        if (registered is not null)
        {
            display = registered;
        }

        return await _rokuDevDeploymentService.SendPowerCommandAsync(display, powerOn).ConfigureAwait(false);
    }

    public async Task<string> EnsureDisplayAppRunningAsync(DisplayTarget target, bool requirePowerOn, CancellationToken cancellationToken)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.NetworkAddress))
        {
            return "tv_sem_ip";
        }

        cancellationToken.ThrowIfCancellationRequested();

        var display = _registeredDisplays.Values.FirstOrDefault(x =>
            string.Equals(x.NetworkAddress, target.NetworkAddress, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(target.DeviceUniqueId) &&
             string.Equals(x.DeviceId, target.DeviceUniqueId, StringComparison.OrdinalIgnoreCase)))
            ?? new RegisteredDisplaySnapshot
            {
                DeviceId = string.IsNullOrWhiteSpace(target.DeviceUniqueId)
                    ? "roku-target-" + target.NetworkAddress.Replace(".", "-")
                    : target.DeviceUniqueId,
                DeviceType = "roku",
                DeviceModel = target.Name,
                ChannelVersion = string.Empty,
                NetworkAddress = target.NetworkAddress,
                LastSeenUtc = DateTime.UtcNow.ToString("O")
            };

        if (requirePowerOn)
        {
            var powerResult = await _rokuDevDeploymentService.SendPowerCommandAsync(display, powerOn: true).ConfigureAwait(false);
            AppLog.Write(
                "StreamKeepAlive",
                string.Format(
                    "PowerOn para TV '{0}' ({1}) => {2}",
                    target.Name,
                    target.NetworkAddress,
                    powerResult));

            var ready = await WaitForDisplayEcpReadyAsync(target.NetworkAddress, TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
            AppLog.Write(
                "RokuDeploy",
                string.Format(
                    "Espera de wake/ECP para TV '{0}' ({1}) => {2}",
                    target.Name,
                    target.NetworkAddress,
                    ready ? "pronta" : "timeout"));

            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        var expectedVersion = GetExpectedRokuChannelVersion();
        if (!string.IsNullOrWhiteSpace(expectedVersion) &&
            !string.IsNullOrWhiteSpace(display.ChannelVersion) &&
            !string.Equals(display.ChannelVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return await _rokuDevDeploymentService.DeployNowAsync(display, expectedVersion).ConfigureAwait(false);
        }

        return await LaunchDevChannelWithWakeRetryAsync(target, display, requirePowerOn, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ForceWakeDisplayAsync(DisplayTarget target, CancellationToken cancellationToken)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.NetworkAddress))
        {
            return "tv_sem_ip";
        }

        cancellationToken.ThrowIfCancellationRequested();

        var display = _registeredDisplays.Values.FirstOrDefault(x =>
            string.Equals(x.NetworkAddress, target.NetworkAddress, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(target.DeviceUniqueId) &&
             string.Equals(x.DeviceId, target.DeviceUniqueId, StringComparison.OrdinalIgnoreCase)))
            ?? new RegisteredDisplaySnapshot
            {
                DeviceId = string.IsNullOrWhiteSpace(target.DeviceUniqueId)
                    ? "roku-target-" + target.NetworkAddress.Replace(".", "-")
                    : target.DeviceUniqueId,
                DeviceType = "roku",
                DeviceModel = target.Name,
                NetworkAddress = target.NetworkAddress,
                LastSeenUtc = DateTime.UtcNow.ToString("O")
            };

        var powerResult = await _rokuDevDeploymentService.SendPowerToggleCommandAsync(display).ConfigureAwait(false);
        AppLog.Write(
            "StreamKeepAlive",
            string.Format(
                "Power fallback para TV '{0}' ({1}) => {2}",
                target.Name,
                target.NetworkAddress,
                powerResult));

        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        return await _rokuDevDeploymentService.LaunchDevChannelAsync(display).ConfigureAwait(false);
    }

    public IReadOnlyList<RegisteredDisplaySnapshot> GetRegisteredDisplaysSnapshot()
    {
        return _registeredDisplays.Values.ToList();
    }

    public bool IsDisplayRegisteredRecently(DisplayTarget target, TimeSpan maxAge)
    {
        if (target is null)
        {
            return false;
        }

        var display = _registeredDisplays.Values.FirstOrDefault(x =>
            (!string.IsNullOrWhiteSpace(target.NetworkAddress) &&
             string.Equals(x.NetworkAddress, target.NetworkAddress, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(target.DeviceUniqueId) &&
             string.Equals(x.DeviceId, target.DeviceUniqueId, StringComparison.OrdinalIgnoreCase)));

        if (display is null || !DateTime.TryParse(display.LastSeenUtc, out var lastSeenUtc))
        {
            return false;
        }

        return DateTime.UtcNow - lastSeenUtc.ToUniversalTime() < maxAge;
    }

    private async Task<string> LaunchDevChannelWithWakeRetryAsync(
        DisplayTarget target,
        RegisteredDisplaySnapshot display,
        bool requirePowerOn,
        CancellationToken cancellationToken)
    {
        var attempts = requirePowerOn ? 4 : 1;
        var delayBetweenAttempts = requirePowerOn ? TimeSpan.FromSeconds(2) : TimeSpan.Zero;
        var lastResult = "launch_nao_executado";

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lastResult = await _rokuDevDeploymentService.LaunchDevChannelAsync(display).ConfigureAwait(false);
            AppLog.Write(
                "RokuDeploy",
                string.Format(
                    "Tentativa {0}/{1} de abrir app Roku para TV '{2}' ({3}) => {4}",
                    attempt,
                    attempts,
                    target.Name,
                    target.NetworkAddress,
                    lastResult));

            if (!requirePowerOn)
            {
                return lastResult;
            }

            if (string.Equals(lastResult, "ok", StringComparison.OrdinalIgnoreCase) &&
                await WaitForRecentRegistrationAsync(target, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false))
            {
                return lastResult;
            }

            if (attempt < attempts)
            {
                await Task.Delay(delayBetweenAttempts, cancellationToken).ConfigureAwait(false);
            }
        }

        return lastResult;
    }

    private async Task<bool> WaitForRecentRegistrationAsync(DisplayTarget target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisplayRegisteredRecently(target, TimeSpan.FromSeconds(4)))
            {
                return true;
            }

            await Task.Delay(700, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> WaitForDisplayEcpReadyAsync(string networkAddress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(networkAddress))
        {
            return false;
        }

        var startedAt = DateTime.UtcNow;
        var probeUri = new Uri(string.Format("http://{0}:8060/query/device-info", networkAddress));
        while (DateTime.UtcNow - startedAt < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using (var response = await DeviceProbeHttpClient.GetAsync(probeUri, cancellationToken).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public void UpdateWindowSnapshots(IEnumerable<WindowSession> windows, IEnumerable<BridgeActiveSessionSnapshot> sessions, int serverPort, WebRtcBindMode bindMode, string specificIp)
    {
        var port = serverPort <= 0 ? 8090 : serverPort;
        var endpoint = LinkRtcAddressBuilder.ResolveListenerEndpoint(bindMode, specificIp, port);
        var listenerKey = string.Format("{0}:{1}", endpoint.Address, endpoint.Port);

        EnsureListener(endpoint, listenerKey);

        var activeIds = new HashSet<Guid>();
        foreach (var window in windows)
        {
            _browserAudioHlsService.EnsureWindow(window.Id);
            _browserPanelRollingHlsService.EnsureWindow(window.Id);
            var snapshot = BuildWindowSnapshot(window, port, bindMode, specificIp);
            _windowSnapshots[window.Id] = snapshot;
            activeIds.Add(window.Id);
        }

        foreach (var existingId in _windowSnapshots.Keys)
        {
            if (!activeIds.Contains(existingId))
            {
                _windowSnapshots.TryRemove(existingId, out _);
                _browserAudioHlsService.Unregister(existingId);
                _browserPanelRollingHlsService.Unregister(existingId);
            }
        }

        _sessionSnapshots.Clear();
        if (sessions is not null)
        {
            foreach (var session in sessions)
            {
                if (!string.IsNullOrWhiteSpace(session.Id))
                {
                    _sessionSnapshots[session.Id] = session;
                }
            }
        }
    }

    private void EnsureListener(IPEndPoint endpoint, string listenerKey)
    {
        lock (_listenerGate)
        {
            if (!string.Equals(_activeListenerKey, listenerKey, StringComparison.OrdinalIgnoreCase))
            {
                StopListener();
                StartListener(endpoint, listenerKey);
            }
        }
    }

    private void StartListener(IPEndPoint endpoint, string listenerKey)
    {
        var cancellation = new CancellationTokenSource();
        var listener = new TcpListener(endpoint);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
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
            string? requestLine;
            try
            {
                requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                string? headerLine;
                do
                {
                    headerLine = await reader.ReadLineAsync();
                }
                while (!string.IsNullOrEmpty(headerLine));
            }
            catch
            {
                return;
            }

            var path = ParsePath(requestLine);
            var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
            var normalizedPath = (path ?? "/").Split('?')[0];
            if (normalizedPath.StartsWith("/audio-hls/", StringComparison.OrdinalIgnoreCase))
            {
                var hlsResponseBytes = await BuildAudioHlsResponseAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(hlsResponseBytes, 0, hlsResponseBytes.Length, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (normalizedPath.StartsWith("/panel-roll/", StringComparison.OrdinalIgnoreCase))
            {
                var panelHlsResponseBytes = await BuildPanelHlsResponseAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(panelHlsResponseBytes, 0, panelHlsResponseBytes.Length, cancellationToken).ConfigureAwait(false);
                return;
            }

            var bytes = await BuildResponseAsync(path ?? "/", remoteAddress, cancellationToken);
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        }
    }

    private async Task<byte[]> BuildResponseAsync(string path, string remoteAddress, CancellationToken cancellationToken)
    {
        var requestTarget = path ?? "/";
        var normalizedPath = requestTarget.Split('?')[0];

        if (normalizedPath.StartsWith("/thumbnails/", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildThumbnailResponseAsync(normalizedPath, cancellationToken);
        }

        if (normalizedPath.StartsWith("/audio/", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildAudioResponseAsync(requestTarget, cancellationToken);
        }

        if (string.Equals(normalizedPath, "/health", StringComparison.OrdinalIgnoreCase))
        {
            return BuildHttpResponse(200, "ok", "text/plain; charset=utf-8");
        }

        if (string.Equals(normalizedPath, "/api/windows", StringComparison.OrdinalIgnoreCase))
        {
            return BuildHttpResponse(200, BuildWindowsJson(requestTarget, remoteAddress), "application/json; charset=utf-8");
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
                    "TV desatualizada detectada: id={0}, atual={1}, esperado={2}.",
                    snapshot.DeviceId,
                    snapshot.ChannelVersion,
                    snapshot.ExpectedChannelVersion));

            if (ShouldAutoSideloadOnRegistration(snapshot.ExpectedChannelVersion))
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Agendando sideload automatico para TV id={0}, atual={1}, esperado={2}.",
                        snapshot.DeviceId,
                        snapshot.ChannelVersion,
                        snapshot.ExpectedChannelVersion));
                _rokuDevDeploymentService.TryScheduleUpdate(snapshot, snapshot.ExpectedChannelVersion);
            }
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
                    "TV desatualizada detectada via input-log: id={0}, atual={1}, esperado={2}.",
                    snapshot.DeviceId,
                    snapshot.ChannelVersion,
                    snapshot.ExpectedChannelVersion));

            if (ShouldAutoSideloadOnRegistration(snapshot.ExpectedChannelVersion))
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Agendando sideload automatico via input-log para TV id={0}, atual={1}, esperado={2}.",
                        snapshot.DeviceId,
                        snapshot.ChannelVersion,
                        snapshot.ExpectedChannelVersion));
                _rokuDevDeploymentService.TryScheduleUpdate(snapshot, snapshot.ExpectedChannelVersion);
            }
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

    private async Task<byte[]> BuildAudioHlsResponseAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var relativePath = normalizedPath.Substring("/audio-hls/".Length);
        var slashIndex = relativePath.IndexOf('/');
        if (slashIndex <= 0 || slashIndex >= relativePath.Length - 1)
        {
            return BuildHttpResponse(404, "Playlist HLS nao encontrada.", "text/plain; charset=utf-8");
        }

        var windowPart = relativePath.Substring(0, slashIndex);
        var filePart = relativePath.Substring(slashIndex + 1);
        if (!Guid.TryParseExact(windowPart, "N", out var windowId))
        {
            return BuildHttpResponse(404, "Janela de audio HLS invalida.", "text/plain; charset=utf-8");
        }

        if (string.Equals(filePart, "index.m3u8", StringComparison.OrdinalIgnoreCase))
        {
            if (!_browserAudioHlsService.TryGetPlaylistPath(windowId, out var playlistPath))
            {
                return BuildHttpResponse(404, "Playlist HLS indisponivel.", "text/plain; charset=utf-8");
            }

            var playlistText = File.ReadAllText(playlistPath);
            return BuildHttpResponse(200, playlistText, "application/vnd.apple.mpegurl");
        }

        if (!_browserAudioHlsService.TryGetSegmentPath(windowId, filePart, out var segmentPath))
        {
            return BuildHttpResponse(404, "Segmento HLS indisponivel.", "text/plain; charset=utf-8");
        }

        var segmentBytes = File.ReadAllBytes(segmentPath);
        return BuildBinaryHttpResponse(200, segmentBytes, "video/mp2t");
    }

    private async Task<byte[]> BuildPanelHlsResponseAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var relativePath = normalizedPath.Substring("/panel-roll/".Length);
        var slashIndex = relativePath.IndexOf('/');
        if (slashIndex <= 0 || slashIndex >= relativePath.Length - 1)
        {
            return BuildHttpResponse(404, "Playlist HLS do painel nao encontrada.", "text/plain; charset=utf-8");
        }

        var windowPart = relativePath.Substring(0, slashIndex);
        var filePart = relativePath.Substring(slashIndex + 1);
        if (!Guid.TryParseExact(windowPart, "N", out var windowId))
        {
            return BuildHttpResponse(404, "Janela HLS do painel invalida.", "text/plain; charset=utf-8");
        }

        if (string.Equals(filePart, "index.m3u8", StringComparison.OrdinalIgnoreCase))
        {
            if (!_browserPanelRollingHlsService.TryGetPlaylistPath(windowId, out var playlistPath))
            {
                MaybeLogPanelHlsServe(windowId, filePart, false);
                return BuildHttpResponse(404, "Playlist HLS do painel indisponivel.", "text/plain; charset=utf-8");
            }

            var playlistText = File.ReadAllText(playlistPath);
            MaybeLogPanelHlsServe(windowId, filePart, true);
            return BuildHttpResponse(200, playlistText, "application/vnd.apple.mpegurl");
        }

        if (!_browserPanelRollingHlsService.TryGetSegmentPath(windowId, filePart, out var segmentPath))
        {
            MaybeLogPanelHlsServe(windowId, filePart, false);
            return BuildHttpResponse(404, "Segmento HLS do painel indisponivel.", "text/plain; charset=utf-8");
        }

        var segmentBytes = File.ReadAllBytes(segmentPath);
        MaybeLogPanelHlsServe(windowId, filePart, true);
        return BuildBinaryHttpResponse(200, segmentBytes, "video/mp2t");
    }

    private async Task<byte[]> BuildAudioResponseAsync(string requestTarget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = requestTarget.Split('?')[0];
        var fileName = normalizedPath.Substring("/audio/".Length);
        if (fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName.Substring(0, fileName.Length - 4);
        }

        if (!Guid.TryParseExact(fileName, "N", out var windowId))
        {
            return BuildHttpResponse(404, "Audio nao encontrado.", "text/plain; charset=utf-8");
        }

        var seconds = 2.5;
        var queryIndex = requestTarget.IndexOf('?');
        if (queryIndex >= 0 && queryIndex < requestTarget.Length - 1)
        {
            var values = ParseQueryString(requestTarget.Substring(queryIndex + 1));
            if (double.TryParse(GetValue(values, "seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedSeconds) &&
                parsedSeconds > 0.25 && parsedSeconds < 10)
            {
                seconds = parsedSeconds;
            }
        }

        var wavBytes = _browserAudioCaptureService.CaptureWaveSnapshot(windowId, TimeSpan.FromSeconds(seconds));
        if (wavBytes is null || wavBytes.Length == 0)
        {
            MaybeLogAudioServe(windowId, 0, seconds, false);
            return BuildHttpResponse(404, "Audio indisponivel.", "text/plain; charset=utf-8");
        }

        MaybeLogAudioServe(windowId, wavBytes.Length, seconds, true);
        return BuildBinaryHttpResponse(200, wavBytes, "audio/wav");
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

    private static byte[] BuildHttpResponse(int statusCode, string body, string contentType)
    {
        var reason = statusCode == 200 ? "OK" : statusCode == 404 ? "Not Found" : "Error";
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

    private string BuildWindowsJson(string requestTarget, string remoteAddress)
    {
        var payload = new WindowsBridgePayload();
        var filteredWindows = ResolveWindowsForBridgeRequest(requestTarget, remoteAddress);
        foreach (var snapshot in filteredWindows)
        {
            payload.Windows.Add(snapshot);
        }

        foreach (var display in _registeredDisplays.Values)
        {
            payload.Displays.Add(display);
        }

        foreach (var session in ResolveSessionsForBridgeRequest(filteredWindows))
        {
            payload.ActiveSessions.Add(session);
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

    private List<BridgeWindowSnapshot> ResolveWindowsForBridgeRequest(string requestTarget, string remoteAddress)
    {
        var allWindows = _windowSnapshots.Values.ToList();
        var queryIndex = requestTarget.IndexOf('?');
        if (queryIndex < 0 || queryIndex >= requestTarget.Length - 1)
        {
            return allWindows;
        }

        var values = ParseQueryString(requestTarget.Substring(queryIndex + 1));
        var deviceId = GetValue(values, "deviceId");
        RegisteredDisplaySnapshot? display = null;
        if (!string.IsNullOrWhiteSpace(deviceId) && _registeredDisplays.TryGetValue(deviceId, out var registered))
        {
            display = registered;
        }
        else if (!string.IsNullOrWhiteSpace(remoteAddress))
        {
            display = _registeredDisplays.Values.FirstOrDefault(x =>
                string.Equals(x.NetworkAddress, remoteAddress, StringComparison.OrdinalIgnoreCase));
        }

        if (display is null || string.IsNullOrWhiteSpace(display.NetworkAddress))
        {
            return allWindows;
        }

        var directlyAssignedWindows = allWindows
            .Where(x =>
                (!string.IsNullOrWhiteSpace(display.DeviceId) &&
                 string.Equals(x.AssignedDisplayId, display.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(display.NetworkAddress) &&
                 string.Equals(x.AssignedDisplayAddress, display.NetworkAddress, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var sessionIds = _sessionSnapshots.Values
            .Where(x => x.DisplayAddresses.Any(address => string.Equals(address, display.NetworkAddress, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sessionIds.Count == 0)
        {
            return directlyAssignedWindows;
        }

        return allWindows
            .Where(x => sessionIds.Contains(x.ActiveSessionId))
            .Concat(directlyAssignedWindows)
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    private List<BridgeActiveSessionSnapshot> ResolveSessionsForBridgeRequest(IEnumerable<BridgeWindowSnapshot> windows)
    {
        var sessionIds = windows
            .Select(x => x.ActiveSessionId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return _sessionSnapshots.Values
            .Where(x => sessionIds.Contains(x.Id))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        var unifiedPanelStreamUrl = _browserPanelRollingHlsService.IsAvailable
            ? string.Format("http://{0}:{1}/panel-roll/{2}/index.m3u8", LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), port, window.Id.ToString("N"))
            : string.Empty;

        return new BridgeWindowSnapshot
        {
            Id = window.Id.ToString("N"),
            Title = string.IsNullOrWhiteSpace(window.Title) ? "Janela sem titulo" : window.Title,
            State = window.State.ToString(),
            InitialUrl = window.InitialUri?.ToString() ?? string.Empty,
            PublishedWebRtcUrl = publishedUrl,
            StreamUrl = string.IsNullOrWhiteSpace(unifiedPanelStreamUrl) ? publishedUrl : unifiedPanelStreamUrl,
            IsPublishing = window.IsWebRtcPublishingEnabled,
            ServerUrl = string.Format("http://{0}:{1}", LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), port),
            ThumbnailUrl = string.Format("http://{0}:{1}/thumbnails/{2}.jpg", LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), port, window.Id.ToString("N")),
            AudioStreamUrl = _browserAudioHlsService.IsAvailable
                ? string.Format("http://{0}:{1}/audio-hls/{2}/index.m3u8", LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), port, window.Id.ToString("N"))
                : string.Format("http://{0}:{1}/audio/{2}.wav?seconds=2.5", LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), port, window.Id.ToString("N")),
            AudioAvailable = _browserAudioCaptureService.HasRecentAudio(window.Id),
            ProfileName = window.ProfileName ?? string.Empty,
            ActiveSessionId = window.ActiveSessionId == Guid.Empty ? string.Empty : window.ActiveSessionId.ToString("N"),
            ActiveSessionName = window.ActiveSessionName ?? string.Empty,
            AutoOpenFullscreen = window.IsPrimaryExclusive,
            AssignedDisplayId = window.AssignedTarget?.Id.ToString("N") ?? string.Empty,
            AssignedDisplayName = window.AssignedTarget?.Name ?? string.Empty,
            AssignedDisplayAddress = window.AssignedTarget?.NetworkAddress ?? string.Empty
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

    private static bool ShouldAutoSideloadOnRegistration(string expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return false;
        }

        var currentChannel = UpdateChannelNames.Normalize(BuildVersionInfo.CurrentBuildChannel);
        return !string.Equals(currentChannel, UpdateChannelNames.Local, StringComparison.OrdinalIgnoreCase);
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

    private void MaybeLogAudioServe(Guid windowId, int bytes, double seconds, bool ok)
    {
        var now = DateTime.UtcNow;
        if (_lastAudioServeLogUtc.TryGetValue(windowId, out var previous) && now - previous < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastAudioServeLogUtc[windowId] = now;
        AppLog.Write(
            "BrowserAudio",
            string.Format(
                "Snapshot de audio solicitado: janela={0}, ok={1}, bytes={2}, seconds={3:0.0}, bufferedPcm={4}",
                windowId.ToString("N"),
                ok,
                bytes,
                seconds,
                -1));
    }

    private void MaybeLogPanelHlsServe(Guid windowId, string fileName, bool ok)
    {
        var key = windowId.ToString("N") + "|" + fileName;
        var now = DateTime.UtcNow;
        if (_lastPanelHlsLogUtc.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastPanelHlsLogUtc[key] = now;
        AppLog.Write(
            "PanelHls",
            string.Format(
                "Requisicao HLS do painel: janela={0}, arquivo={1}, ok={2}",
                windowId.ToString("N"),
                fileName,
                ok));
    }

    private static bool IsPowerCompatibleRokuDisplay(RegisteredDisplaySnapshot display)
    {
        if (display is null || string.IsNullOrWhiteSpace(display.NetworkAddress))
        {
            return false;
        }

        if (string.Equals(display.DeviceType, "roku", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(display.DeviceId) &&
               display.DeviceId.StartsWith("roku-", StringComparison.OrdinalIgnoreCase);
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

    [DataMember(Name = "activeSessions", Order = 4)]
    public List<BridgeActiveSessionSnapshot> ActiveSessions { get; set; } = new List<BridgeActiveSessionSnapshot>();
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

    [DataMember(Name = "audioStreamUrl", Order = 10)]
    public string AudioStreamUrl { get; set; } = string.Empty;

    [DataMember(Name = "audioAvailable", Order = 11)]
    public bool AudioAvailable { get; set; }

    [DataMember(Name = "profileName", Order = 12)]
    public string ProfileName { get; set; } = string.Empty;

    [DataMember(Name = "activeSessionId", Order = 13)]
    public string ActiveSessionId { get; set; } = string.Empty;

    [DataMember(Name = "activeSessionName", Order = 14)]
    public string ActiveSessionName { get; set; } = string.Empty;

    [DataMember(Name = "autoOpenFullscreen", Order = 15)]
    public bool AutoOpenFullscreen { get; set; }

    [DataMember(Name = "assignedDisplayId", Order = 16)]
    public string AssignedDisplayId { get; set; } = string.Empty;

    [DataMember(Name = "assignedDisplayName", Order = 17)]
    public string AssignedDisplayName { get; set; } = string.Empty;

    [DataMember(Name = "assignedDisplayAddress", Order = 18)]
    public string AssignedDisplayAddress { get; set; } = string.Empty;
}

[DataContract]
public sealed class BridgeActiveSessionSnapshot
{
    [DataMember(Name = "id", Order = 1)]
    public string Id { get; set; } = string.Empty;

    [DataMember(Name = "name", Order = 2)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "profileName", Order = 3)]
    public string ProfileName { get; set; } = string.Empty;

    [DataMember(Name = "windowIds", Order = 4)]
    public List<string> WindowIds { get; set; } = new List<string>();

    [DataMember(Name = "displayAddresses", Order = 5)]
    public List<string> DisplayAddresses { get; set; } = new List<string>();
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

public sealed class RokuPowerBatchResult
{
    public int TargetedCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int SkippedCount { get; set; }
}

