using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WindowManager.App.Runtime;
using WindowManager.Core.Models;

namespace WindowManager.App.Runtime.Publishing;

public sealed class LocalWebRtcPublisherService
{
    private const string InteractionStreamingMode = "Interacao";
    private const string VideoStreamingMode = "Video";
    private const bool AutomaticRokuSideloadEnabled = false;
    private const int InteractionFullscreenWarmupSegments = 1;
    private static readonly TimeSpan ModeSwitchAckTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan DirectYoutubeResolveSuccessTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan DirectYoutubeResolveFailureTtl = TimeSpan.FromSeconds(45);
    private static readonly HttpClient DeviceProbeHttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private readonly BrowserSnapshotService _browserSnapshotService;
    private readonly BrowserAudioCaptureService _browserAudioCaptureService;
    private readonly BrowserAudioHlsService _browserAudioHlsService;
    private readonly BrowserPanelInteractionHlsService _browserPanelInteractionHlsService;
    private readonly BrowserPanelRollingHlsService _browserPanelRollingHlsService;
    private readonly RokuDevDeploymentService _rokuDevDeploymentService;
    private readonly object _listenerGate = new object();
    private readonly ConcurrentDictionary<string, PublishedWindowRoute> _routes = new ConcurrentDictionary<string, PublishedWindowRoute>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string> _windowRouteKeys = new ConcurrentDictionary<Guid, string>();
    private readonly ConcurrentDictionary<Guid, BridgeWindowSnapshot> _windowSnapshots = new ConcurrentDictionary<Guid, BridgeWindowSnapshot>();
    private readonly ConcurrentDictionary<Guid, WindowSession> _runtimeWindows = new ConcurrentDictionary<Guid, WindowSession>();
    private readonly ConcurrentDictionary<string, BridgeActiveSessionSnapshot> _sessionSnapshots = new ConcurrentDictionary<string, BridgeActiveSessionSnapshot>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RegisteredDisplaySnapshot> _registeredDisplays = new ConcurrentDictionary<string, RegisteredDisplaySnapshot>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _displayReadyUtcByDeviceId = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DisplayModeSwitchState> _displayModeSwitchStates = new ConcurrentDictionary<string, DisplayModeSwitchState>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, DateTime> _lastAudioServeLogUtc = new ConcurrentDictionary<Guid, DateTime>();
    private readonly ConcurrentDictionary<string, DateTime> _lastPanelHlsLogUtc = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _lastPanelHlsStatusByKey = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastPanelHlsRequestUtcByDisplay = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _bridgeDiagnosticsCountByDevice = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string> _lastBridgeSnapshotLogByWindow = new ConcurrentDictionary<Guid, string>();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastBridgeSnapshotLogUtcByWindow = new ConcurrentDictionary<Guid, DateTime>();
    private readonly ConcurrentDictionary<Guid, int> _streamReloadVersions = new ConcurrentDictionary<Guid, int>();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastInteractionReloadUtc = new ConcurrentDictionary<Guid, DateTime>();
    private readonly ConcurrentDictionary<string, CachedDirectPlaybackResolveResult> _cachedDirectPlaybackByYoutubeUrl = new ConcurrentDictionary<string, CachedDirectPlaybackResolveResult>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<CachedDirectPlaybackResolveResult>> _directPlaybackResolveTasks = new ConcurrentDictionary<string, Task<CachedDirectPlaybackResolveResult>>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string> _lastDirectOverlayLogByWindow = new ConcurrentDictionary<Guid, string>();
    private static readonly TimeSpan InteractionReloadDebounce = TimeSpan.FromMilliseconds(700);

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCancellation;
    private string _activeListenerKey = string.Empty;
    private int _currentServerPort = 8090;
    private WebRtcBindMode _currentBindMode;
    private string _currentSpecificIp = string.Empty;

    public LocalWebRtcPublisherService(BrowserSnapshotService browserSnapshotService, BrowserAudioCaptureService browserAudioCaptureService, BrowserAudioHlsService browserAudioHlsService, BrowserPanelInteractionHlsService browserPanelInteractionHlsService, BrowserPanelRollingHlsService browserPanelRollingHlsService, AppUpdatePreferenceStore appUpdatePreferenceStore)
    {
        _browserSnapshotService = browserSnapshotService;
        _browserAudioCaptureService = browserAudioCaptureService;
        _browserAudioHlsService = browserAudioHlsService;
        _browserPanelInteractionHlsService = browserPanelInteractionHlsService;
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

    public void RequestStreamReload(Guid windowId)
    {
        var version = _streamReloadVersions.AddOrUpdate(windowId, 1, (_, current) => current + 1);
        AppLog.Write("RokuControl", string.Format("Solicitado recarregamento do stream na TV para janela {0}. versao={1}", windowId.ToString("N"), version));
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

            if (!IsDisplayLinkedToAnyStream(display))
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "TV ignorada no sideload manual em lote por nao possuir stream vinculado: id={0}, ip={1}",
                        display.DeviceId,
                        display.NetworkAddress));
                continue;
            }

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

            var result = await _rokuDevDeploymentService.DeployNowAsync(display, display.ExpectedChannelVersion, "manual_batch_update").ConfigureAwait(false);
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

        if (!IsDisplayTargetLinkedToAnyStream(target))
        {
            AppLog.Write(
                "RokuDeploy",
                string.Format(
                    "Atualizacao ignorada para TV descoberta sem stream vinculado: nome={0}, ip={1}",
                    target.Name,
                    target.NetworkAddress));
            return "tv_sem_stream_vinculado";
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

        return await _rokuDevDeploymentService.DeployNowAsync(display, display.ExpectedChannelVersion, "manual_target_update").ConfigureAwait(false);
    }

    public async Task<RokuPowerBatchResult> SendPowerCommandToConnectedDisplaysAsync(bool powerOn, CancellationToken cancellationToken)
    {
        var displays = _registeredDisplays.Values.ToArray();
        var result = new RokuPowerBatchResult();

        foreach (var display in displays)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsDisplayLinkedToAnyStream(display))
            {
                result.SkippedCount++;
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "TV ignorada no comando de energia por nao possuir stream vinculado: id={0}, ip={1}",
                        display.DeviceId,
                        display.NetworkAddress));
                continue;
            }

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
            if (IsDisplayStreamingRecently(target, TimeSpan.FromSeconds(20)))
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Keepalive ignorado para TV '{0}' ({1}) porque ela esta consumindo HLS recentemente.",
                        target.Name,
                        target.NetworkAddress));
                return "streaming_recente";
            }

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
            if (!AutomaticRokuSideloadEnabled)
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Sideload automatico desabilitado. Ignorando mismatch para TV '{0}' ({1}): atual={2}, esperado={3}.",
                        target.Name,
                        target.NetworkAddress,
                        display.ChannelVersion,
                        expectedVersion));
                return "auto_sideload_disabled";
            }

            if (IsDisplayStreamingRecently(target, TimeSpan.FromSeconds(20)))
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Sideload ignorado para TV '{0}' ({1}) porque ela esta consumindo HLS recentemente.",
                        target.Name,
                        target.NetworkAddress));
                return "streaming_recente";
            }

            return await _rokuDevDeploymentService.DeployNowAsync(display, expectedVersion, "ensure_display_version_mismatch").ConfigureAwait(false);
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

        if (IsDisplayStreamingRecently(target, TimeSpan.FromSeconds(20)))
        {
            AppLog.Write(
                "RokuDeploy",
                string.Format(
                    "ForceWake ignorado para TV '{0}' ({1}) porque ela esta consumindo HLS recentemente.",
                    target.Name,
                    target.NetworkAddress));
            return "streaming_recente";
        }

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
        return await _rokuDevDeploymentService.LaunchDevChannelAsync(display, "force_wake").ConfigureAwait(false);
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

    public bool IsDisplayStreamingRecently(DisplayTarget target, TimeSpan maxAge)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.NetworkAddress))
        {
            return false;
        }

        if (!_lastPanelHlsRequestUtcByDisplay.TryGetValue(target.NetworkAddress, out var lastRequestUtc))
        {
            return false;
        }

        return DateTime.UtcNow - lastRequestUtc < maxAge;
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

            if (IsDisplayStreamingRecently(target, TimeSpan.FromSeconds(20)))
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Tentativa {0}/{1} de abrir app Roku ignorada para TV '{2}' ({3}) porque ela esta consumindo HLS recentemente.",
                        attempt,
                        attempts,
                        target.Name,
                        target.NetworkAddress));
                return "streaming_recente";
            }

            lastResult = await _rokuDevDeploymentService.LaunchDevChannelAsync(display, requirePowerOn ? "ensure_display_with_power" : "ensure_display").ConfigureAwait(false);
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
        _currentServerPort = port;
        _currentBindMode = bindMode;
        _currentSpecificIp = specificIp;
        EnsureListener(endpoint, listenerKey);

        var activeIds = new HashSet<Guid>();
        foreach (var window in windows)
        {
            var desiredStreamingMode = NormalizeStreamingMode(window.StreamingMode);
            EnsureDisplayModeSwitchSeed(window, desiredStreamingMode);
            _browserAudioHlsService.EnsureWindow(window.Id);
            if (string.Equals(desiredStreamingMode, InteractionStreamingMode, StringComparison.OrdinalIgnoreCase))
            {
                _browserPanelInteractionHlsService.EnsureWindow(window.Id);
                _browserPanelRollingHlsService.Unregister(window.Id);
            }
            else
            {
                _browserPanelRollingHlsService.EnsureWindow(window.Id);
                _browserPanelInteractionHlsService.Unregister(window.Id);
            }
            var snapshot = BuildWindowSnapshot(window, port, bindMode, specificIp, desiredStreamingMode);
            _runtimeWindows[window.Id] = window;
            _windowSnapshots[window.Id] = snapshot;
            activeIds.Add(window.Id);
        }

        foreach (var existingId in _windowSnapshots.Keys)
        {
            if (!activeIds.Contains(existingId))
            {
                _windowSnapshots.TryRemove(existingId, out _);
                _runtimeWindows.TryRemove(existingId, out _);
                _browserAudioHlsService.Unregister(existingId);
                _browserPanelInteractionHlsService.Unregister(existingId);
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
                if (!string.IsNullOrWhiteSpace(remoteAddress))
                {
                    _lastPanelHlsRequestUtcByDisplay[remoteAddress] = DateTime.UtcNow;
                }

                var panelHlsResponseBytes = await BuildPanelHlsResponseAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(panelHlsResponseBytes, 0, panelHlsResponseBytes.Length, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (normalizedPath.StartsWith("/panel-interaction/", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(remoteAddress))
                {
                    _lastPanelHlsRequestUtcByDisplay[remoteAddress] = DateTime.UtcNow;
                }

                var panelHlsResponseBytes = await BuildPanelInteractionHlsResponseAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(normalizedPath, "/api/display-ready", StringComparison.OrdinalIgnoreCase))
        {
            MarkDisplayReady(requestTarget, remoteAddress);
            return BuildHttpResponse(200, "{\"ok\":true}", "application/json; charset=utf-8");
        }

        if (string.Equals(normalizedPath, "/api/mode-switch-applied", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedPath, "/api/mode-switch-stopped", StringComparison.OrdinalIgnoreCase))
        {
            MarkModeSwitchApplied(requestTarget, remoteAddress);
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
        var key = BuildRegisteredDisplayDeviceId(GetValue(values, "deviceId"), remoteAddress);

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
        if (ShouldSuppressNonExclusiveDisplay(remoteAddress))
        {
            snapshot.ExpectedChannelVersion = string.Empty;
            snapshot.UpdateAvailable = false;
        }

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

        if (_registeredDisplays.TryGetValue(key, out var previousRegistered) &&
            !string.Equals(previousRegistered.ChannelVersion, snapshot.ChannelVersion, StringComparison.OrdinalIgnoreCase))
        {
            _displayReadyUtcByDeviceId.TryRemove(key, out _);
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

            if (ShouldAutoSideloadOnRegistration(snapshot.ExpectedChannelVersion) && IsDisplayLinkedToAnyStream(snapshot))
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Agendando sideload automatico para TV id={0}, atual={1}, esperado={2}.",
                        snapshot.DeviceId,
                        snapshot.ChannelVersion,
                        snapshot.ExpectedChannelVersion));
                _rokuDevDeploymentService.TryScheduleUpdate(snapshot, snapshot.ExpectedChannelVersion, "register_display");
            }
            else if (!IsDisplayLinkedToAnyStream(snapshot))
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Sideload automatico ignorado para TV sem stream vinculado: id={0}, ip={1}",
                        snapshot.DeviceId,
                        snapshot.NetworkAddress));
            }
        }
    }

    private void MarkDisplayReady(string requestTarget, string remoteAddress)
    {
        var queryIndex = requestTarget.IndexOf('?');
        if (queryIndex < 0 || queryIndex >= requestTarget.Length - 1)
        {
            return;
        }

        var values = ParseQueryString(requestTarget.Substring(queryIndex + 1));
        var deviceId = BuildRegisteredDisplayDeviceId(GetValue(values, "deviceId"), remoteAddress);
        if (string.IsNullOrWhiteSpace(deviceId) && !string.IsNullOrWhiteSpace(remoteAddress))
        {
            deviceId = _registeredDisplays.Values
                .FirstOrDefault(x => string.Equals(x.NetworkAddress, remoteAddress, StringComparison.OrdinalIgnoreCase))
                ?.DeviceId ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        _displayReadyUtcByDeviceId[deviceId] = DateTime.UtcNow;
        AppLog.Write(
            "RokuDeploy",
            string.Format(
                "Display-ready recebido: id={0}, ip={1}",
                deviceId,
                remoteAddress));
    }

    private void MarkModeSwitchApplied(string requestTarget, string remoteAddress)
    {
        var queryIndex = requestTarget.IndexOf('?');
        if (queryIndex < 0 || queryIndex >= requestTarget.Length - 1)
        {
            return;
        }

        var values = ParseQueryString(requestTarget.Substring(queryIndex + 1));
        var deviceId = BuildRegisteredDisplayDeviceId(GetValue(values, "deviceId"), remoteAddress);
        var windowId = GetValue(values, "windowId");
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(windowId))
        {
            return;
        }

        var key = BuildDisplayWindowKey(deviceId, windowId);
        if (!_displayModeSwitchStates.TryGetValue(key, out var state) || string.IsNullOrWhiteSpace(state.PendingTargetMode))
        {
            return;
        }

        var targetMode = NormalizeStreamingMode(state.PendingTargetMode);
        state.LastUpdatedUtc = DateTime.UtcNow;

        if (Guid.TryParse(windowId, out var runtimeWindowId) && _runtimeWindows.TryGetValue(runtimeWindowId, out var runtimeWindow))
        {
            _browserAudioHlsService.EnsureWindow(runtimeWindow.Id);
            if (string.Equals(targetMode, InteractionStreamingMode, StringComparison.OrdinalIgnoreCase))
            {
                _browserPanelInteractionHlsService.EnsureWindow(runtimeWindow.Id);
                _browserPanelRollingHlsService.Unregister(runtimeWindow.Id);
            }
            else
            {
                _browserPanelRollingHlsService.EnsureWindow(runtimeWindow.Id);
                _browserPanelInteractionHlsService.Unregister(runtimeWindow.Id);
            }

            var refreshedSnapshot = BuildWindowSnapshot(
                runtimeWindow,
                _currentServerPort,
                _currentBindMode,
                _currentSpecificIp,
                targetMode);

            if (string.Equals(targetMode, VideoStreamingMode, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(refreshedSnapshot.StreamUrl))
            {
                AppLog.Write(
                    "StreamingMode",
                    string.Format(
                        "ACK recebido, mas Video ainda nao esta pronto: janela={0}, stream={1}, autoFullscreen={2}. Mantendo troca pendente.",
                        windowId,
                        string.IsNullOrWhiteSpace(refreshedSnapshot.StreamUrl) ? "<sem-stream>" : refreshedSnapshot.StreamUrl,
                        refreshedSnapshot.AutoOpenFullscreen));
                return;
            }

            state.ExposedMode = targetMode;
            state.PendingTargetMode = string.Empty;
            _windowSnapshots[runtimeWindow.Id] = refreshedSnapshot;

            AppLog.Write(
                "StreamingMode",
                string.Format(
                    "Snapshot recomposto apos ACK: janela={0}, modo={1}, stream={2}, autoFullscreen={3}",
                    windowId,
                    refreshedSnapshot.StreamingMode,
                    string.IsNullOrWhiteSpace(refreshedSnapshot.StreamUrl) ? "<sem-stream>" : refreshedSnapshot.StreamUrl,
                    refreshedSnapshot.AutoOpenFullscreen));
        }

        AppLog.Write(
            "StreamingMode",
            string.Format(
                "Mode switch aplicado confirmado: device={0}, janela={1}, novoModo={2}, ip={3}",
                deviceId,
                windowId,
                targetMode,
                remoteAddress));
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
        var deviceId = BuildRegisteredDisplayDeviceId(GetValue(values, "deviceId"), remoteAddress);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
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
        if (ShouldSuppressNonExclusiveDisplay(remoteAddress))
        {
            snapshot.ExpectedChannelVersion = string.Empty;
            snapshot.UpdateAvailable = false;
        }

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

            if (ShouldAutoSideloadOnRegistration(snapshot.ExpectedChannelVersion) && IsDisplayLinkedToAnyStream(snapshot))
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Agendando sideload automatico via input-log para TV id={0}, atual={1}, esperado={2}.",
                        snapshot.DeviceId,
                        snapshot.ChannelVersion,
                        snapshot.ExpectedChannelVersion));
                _rokuDevDeploymentService.TryScheduleUpdate(snapshot, snapshot.ExpectedChannelVersion, "input_log");
            }
            else if (!IsDisplayLinkedToAnyStream(snapshot))
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Sideload automatico via input-log ignorado para TV sem stream vinculado: id={0}, ip={1}",
                        snapshot.DeviceId,
                        snapshot.NetworkAddress));
            }
        }
    }

    private bool IsDisplayTargetLinkedToAnyStream(DisplayTarget target)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.NetworkAddress))
        {
            return false;
        }

        return HasAssignedStreamForAddress(target.NetworkAddress);
    }

    private bool IsDisplayLinkedToAnyStream(RegisteredDisplaySnapshot snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.NetworkAddress))
        {
            return false;
        }

        return HasAssignedStreamForAddress(snapshot.NetworkAddress);
    }

    private bool HasAssignedStreamForAddress(string networkAddress)
    {
        if (string.IsNullOrWhiteSpace(networkAddress))
        {
            return false;
        }

        var normalizedAddress = networkAddress.Trim();

        return _runtimeWindows.Values.Any(x =>
                   x.AssignedTarget is not null &&
                   !string.IsNullOrWhiteSpace(x.AssignedTarget.NetworkAddress) &&
                   string.Equals(x.AssignedTarget.NetworkAddress, normalizedAddress, StringComparison.OrdinalIgnoreCase))
               || _windowSnapshots.Values.Any(x =>
                   !string.IsNullOrWhiteSpace(x.AssignedDisplayAddress) &&
                   string.Equals(x.AssignedDisplayAddress, normalizedAddress, StringComparison.OrdinalIgnoreCase));
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
        MaybeRequestInteractionReload(windowId, command, result);
        var body = SerializeJson(result);
        return BuildHttpResponse(result.Ok ? 200 : 404, body, "application/json; charset=utf-8");
    }

    private void MaybeRequestInteractionReload(Guid windowId, string command, RemoteCommandResult result)
    {
        if (!result.Ok)
        {
            return;
        }

        if (!IsVideoStreamingMode(windowId))
        {
            return;
        }

        if (!ShouldReloadAfterControl(command))
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var shouldReload = false;

        _lastInteractionReloadUtc.AddOrUpdate(
            windowId,
            _ =>
            {
                shouldReload = true;
                return nowUtc;
            },
            (_, previousUtc) =>
            {
                if (nowUtc - previousUtc >= InteractionReloadDebounce)
                {
                    shouldReload = true;
                    return nowUtc;
                }

                return previousUtc;
            });

        if (!shouldReload)
        {
            return;
        }

        RequestStreamReload(windowId);
    }

    private static bool ShouldReloadAfterControl(string command)
    {
        switch ((command ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "click":
            case "ok":
            case "select":
            case "set-text":
            case "back":
            case "reload":
            case "history-back":
            case "history-forward":
            case "scroll-up":
            case "scroll-down":
            case "media-seek-backward":
            case "media-seek-forward":
            case "enter":
            case "media-play-pause":
            case "play":
            case "tab":
                return true;
            default:
                return false;
        }
    }

    private bool IsVideoStreamingMode(Guid windowId)
    {
        if (_windowSnapshots.TryGetValue(windowId, out var snapshot))
        {
            return string.Equals(
                NormalizeStreamingMode(snapshot.StreamingMode),
                VideoStreamingMode,
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
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

        if (filePart.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var requestedPlaylist = filePart;
            if (string.Equals(filePart, "index.m3u8", StringComparison.OrdinalIgnoreCase))
            {
                filePart = "medium.m3u8";
            }

            if (!_browserPanelRollingHlsService.TryGetOutputBytes(windowId, filePart, out var playlistBytes))
            {
                MaybeLogPanelHlsServe(windowId, requestedPlaylist, false);
                return BuildHttpResponse(404, "Playlist HLS do painel indisponivel.", "text/plain; charset=utf-8");
            }

            MaybeLogPanelHlsServe(windowId, requestedPlaylist, true);
            return BuildBinaryHttpResponse(200, playlistBytes, "application/vnd.apple.mpegurl");
        }

        if (!_browserPanelRollingHlsService.TryGetOutputBytes(windowId, filePart, out var segmentBytes))
        {
            MaybeLogPanelHlsServe(windowId, filePart, false);
            return BuildHttpResponse(404, "Segmento HLS do painel indisponivel.", "text/plain; charset=utf-8");
        }

        MaybeLogPanelHlsServe(windowId, filePart, true);
        return BuildBinaryHttpResponse(200, segmentBytes, "video/mp2t");
    }

    private async Task<byte[]> BuildPanelInteractionHlsResponseAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var relativePath = normalizedPath.Substring("/panel-interaction/".Length);
        var slashIndex = relativePath.IndexOf('/');
        if (slashIndex <= 0 || slashIndex >= relativePath.Length - 1)
        {
            return BuildHttpResponse(404, "Playlist HLS de interacao do painel nao encontrada.", "text/plain; charset=utf-8");
        }

        var windowPart = relativePath.Substring(0, slashIndex);
        var filePart = relativePath.Substring(slashIndex + 1);
        if (!Guid.TryParseExact(windowPart, "N", out var windowId))
        {
            return BuildHttpResponse(404, "Janela HLS de interacao invalida.", "text/plain; charset=utf-8");
        }

        if (filePart.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(filePart, "index.m3u8", StringComparison.OrdinalIgnoreCase) ||
                !_browserPanelInteractionHlsService.TryGetPlaylistBytes(windowId, out var playlistBytes))
            {
                MaybeLogPanelHlsServe(windowId, filePart, false);
                return BuildHttpResponse(404, "Playlist HLS de interacao indisponivel.", "text/plain; charset=utf-8");
            }

            MaybeLogPanelHlsServe(windowId, filePart, true);
            return BuildBinaryHttpResponse(200, playlistBytes, "application/vnd.apple.mpegurl");
        }

        if (!_browserPanelInteractionHlsService.TryGetSegmentBytes(windowId, filePart, out var segmentBytes))
        {
            MaybeLogPanelHlsServe(windowId, filePart, false);
            return BuildHttpResponse(404, "Segmento HLS de interacao indisponivel.", "text/plain; charset=utf-8");
        }

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

    private string GetExclusiveDisplayAddress()
    {
        return _runtimeWindows.Values
            .Where(x => x.IsPrimaryExclusive &&
                        x.AssignedTarget is not null &&
                        !string.IsNullOrWhiteSpace(x.AssignedTarget.NetworkAddress))
            .Select(x => x.AssignedTarget!.NetworkAddress)
            .FirstOrDefault() ?? string.Empty;
    }

    private bool ShouldSuppressNonExclusiveDisplay(string remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress))
        {
            return false;
        }

        var exclusiveAddress = GetExclusiveDisplayAddress();
        return
            !string.IsNullOrWhiteSpace(exclusiveAddress) &&
            !string.Equals(remoteAddress, exclusiveAddress, StringComparison.OrdinalIgnoreCase);
    }

    private string BuildWindowsJson(string requestTarget, string remoteAddress)
    {
        var payload = new WindowsBridgePayload();
        var filteredWindows = ResolveWindowsForBridgeRequest(requestTarget, remoteAddress);
        var queryIndex = requestTarget.IndexOf('?');
        var deviceId = string.Empty;
        if (queryIndex >= 0 && queryIndex < requestTarget.Length - 1)
        {
            var values = ParseQueryString(requestTarget.Substring(queryIndex + 1));
            deviceId = BuildRegisteredDisplayDeviceId(GetValue(values, "deviceId"), remoteAddress);
        }

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

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var diagnosticsCount = _bridgeDiagnosticsCountByDevice.AddOrUpdate(deviceId, 1, (_, current) => current + 1);
            if (diagnosticsCount <= 1)
            {
                foreach (var window in payload.Windows)
                {
                    AppLog.Write(
                        "BridgeDiag",
                        string.Format(
                            "payload#{0}: device={1}, janela={2}, modo={3}, alvo={4}, pendente={5}, autoFullscreen={6}, stream={7}",
                            diagnosticsCount,
                            deviceId,
                            window.Id,
                            window.StreamingMode,
                            window.RequestedStreamingMode,
                            window.ModeSwitchPending,
                            window.AutoOpenFullscreen,
                            string.IsNullOrWhiteSpace(window.StreamUrl) ? "<sem-stream>" : window.StreamUrl));
                }
            }
        }

        using (var stream = new MemoryStream())
        {
            var serializer = new DataContractJsonSerializer(typeof(WindowsBridgePayload));
            serializer.WriteObject(stream, payload);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private List<BridgeWindowSnapshot> ResolveWindowsForBridgeRequest(string requestTarget, string remoteAddress)
    {
        if (ShouldSuppressNonExclusiveDisplay(remoteAddress))
        {
            return new List<BridgeWindowSnapshot>();
        }

        var allWindows = _runtimeWindows.Values
            .Select(window =>
            {
                var desiredStreamingMode = NormalizeStreamingMode(window.StreamingMode);
                return BuildWindowSnapshot(window, _currentServerPort, _currentBindMode, _currentSpecificIp, desiredStreamingMode);
            })
            .ToList();

        if (allWindows.Count == 0)
        {
            allWindows = _windowSnapshots.Values.ToList();
        }

        var queryIndex = requestTarget.IndexOf('?');
        if (queryIndex < 0 || queryIndex >= requestTarget.Length - 1)
        {
            return allWindows;
        }

        var values = ParseQueryString(requestTarget.Substring(queryIndex + 1));
        var deviceId = BuildRegisteredDisplayDeviceId(GetValue(values, "deviceId"), remoteAddress);
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
            return string.IsNullOrWhiteSpace(deviceId) ? allWindows : new List<BridgeWindowSnapshot>();
        }

        var directlyAssignedWindows = allWindows
            .Where(x =>
                (!string.IsNullOrWhiteSpace(display.NetworkAddress) &&
                 string.Equals(x.AssignedDisplayAddress, display.NetworkAddress, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var sessionIds = _sessionSnapshots.Values
            .Where(x => x.DisplayAddresses.Any(address => string.Equals(address, display.NetworkAddress, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sessionIds.Count == 0 && directlyAssignedWindows.Count == 0)
        {
            return new List<BridgeWindowSnapshot>();
        }

        var resolvedWindows = allWindows
            .Where(x => sessionIds.Contains(x.ActiveSessionId))
            .Concat(directlyAssignedWindows)
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        var projectedWindows = resolvedWindows
            .Select(x => ProjectWindowSnapshotForDisplay(x, display.DeviceId))
            .ToList();

        if (string.IsNullOrWhiteSpace(deviceId) || _displayReadyUtcByDeviceId.ContainsKey(deviceId))
        {
            return projectedWindows;
        }

        AppLog.Write(
            "RokuDeploy",
            string.Format(
                "Bridge respondeu antes do display-ready para TV id={0}. Mantendo payload completo para nao atrasar o bootstrap do modo atual.",
                deviceId));

        return projectedWindows;
    }

    private BridgeWindowSnapshot ProjectWindowSnapshotForDisplay(BridgeWindowSnapshot source, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(source.Id))
        {
            return source;
        }

        if (!Guid.TryParse(source.Id, out var windowId) || !_runtimeWindows.TryGetValue(windowId, out var runtimeWindow))
        {
            return source;
        }

        var desiredMode = NormalizeStreamingMode(runtimeWindow.StreamingMode);
        var key = BuildDisplayWindowKey(deviceId, source.Id);
        var switchState = _displayModeSwitchStates.GetOrAdd(
            key,
            _ => new DisplayModeSwitchState
            {
                ExposedMode = desiredMode,
                PendingTargetMode = string.Empty,
                LastUpdatedUtc = DateTime.UtcNow
            });

        if (string.IsNullOrWhiteSpace(switchState.ExposedMode))
        {
            switchState.ExposedMode = desiredMode;
        }

        if (!string.Equals(switchState.ExposedMode, desiredMode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(switchState.PendingTargetMode, desiredMode, StringComparison.OrdinalIgnoreCase))
        {
            switchState.PendingTargetMode = desiredMode;
            switchState.LastUpdatedUtc = DateTime.UtcNow;
            AppLog.Write(
                "StreamingMode",
                string.Format(
                    "Mode switch solicitado: device={0}, janela={1}, atual={2}, alvo={3}",
                    deviceId,
                    source.Id,
                    switchState.ExposedMode,
                    desiredMode));
        }

        if (string.IsNullOrWhiteSpace(switchState.PendingTargetMode))
        {
            return source;
        }

        var pendingMode = NormalizeStreamingMode(switchState.PendingTargetMode);
        if (DateTime.UtcNow - switchState.LastUpdatedUtc >= ModeSwitchAckTimeout)
        {
            var fallbackSnapshot = BuildWindowSnapshot(runtimeWindow, _currentServerPort, _currentBindMode, _currentSpecificIp, pendingMode);
            if (!string.IsNullOrWhiteSpace(fallbackSnapshot.StreamUrl))
            {
                switchState.ExposedMode = pendingMode;
                switchState.PendingTargetMode = string.Empty;
                switchState.LastUpdatedUtc = DateTime.UtcNow;
                AppLog.Write(
                    "StreamingMode",
                    string.Format(
                        "Mode switch promovido por timeout sem ACK: device={0}, janela={1}, novoModo={2}, stream={3}",
                        deviceId,
                        source.Id,
                        pendingMode,
                        fallbackSnapshot.StreamUrl));
                return fallbackSnapshot;
            }
        }

        var projected = BuildWindowSnapshot(runtimeWindow, _currentServerPort, _currentBindMode, _currentSpecificIp, pendingMode);
        projected.RequestedStreamingMode = switchState.PendingTargetMode;
        projected.ModeSwitchPending = true;
        projected.AutoOpenFullscreen = runtimeWindow.IsPrimaryExclusive;
        AppLog.Write(
            "StreamingMode",
            string.Format(
                "Mode switch pendente: device={0}, janela={1}, exposto={2}, alvo={3}, stream={4}, autoFullscreen={5}",
                deviceId,
                source.Id,
                projected.StreamingMode,
                projected.RequestedStreamingMode,
                string.IsNullOrWhiteSpace(projected.StreamUrl) ? "<sem-stream>" : projected.StreamUrl,
                projected.AutoOpenFullscreen));
        return projected;
    }

    private void EnsureDisplayModeSwitchSeed(WindowSession window, string desiredMode)
    {
        var assignedAddress = window.AssignedTarget?.NetworkAddress ?? string.Empty;
        if (string.IsNullOrWhiteSpace(assignedAddress))
        {
            return;
        }

        var display = _registeredDisplays.Values.FirstOrDefault(x =>
            string.Equals(x.NetworkAddress, assignedAddress, StringComparison.OrdinalIgnoreCase));
        if (display is null || string.IsNullOrWhiteSpace(display.DeviceId))
        {
            return;
        }

        var windowKey = window.Id.ToString("N");
        var stateKey = BuildDisplayWindowKey(display.DeviceId, windowKey);
        var previousSnapshot = _windowSnapshots.TryGetValue(window.Id, out var snapshot) ? snapshot : null;
        if (previousSnapshot is null || string.IsNullOrWhiteSpace(previousSnapshot.StreamingMode))
        {
            if (_displayModeSwitchStates.TryGetValue(stateKey, out var initialState))
            {
                if (string.IsNullOrWhiteSpace(initialState.PendingTargetMode))
                {
                    initialState.ExposedMode = desiredMode;
                    initialState.LastUpdatedUtc = DateTime.UtcNow;
                }
            }

            return;
        }

        var previousMode = NormalizeStreamingMode(previousSnapshot.StreamingMode);

        if (string.Equals(previousMode, desiredMode, StringComparison.OrdinalIgnoreCase))
        {
            if (_displayModeSwitchStates.TryGetValue(stateKey, out var existingState) &&
                string.IsNullOrWhiteSpace(existingState.PendingTargetMode) &&
                !string.Equals(existingState.ExposedMode, desiredMode, StringComparison.OrdinalIgnoreCase))
            {
                existingState.ExposedMode = desiredMode;
                existingState.LastUpdatedUtc = DateTime.UtcNow;
            }

            return;
        }

        var switchState = _displayModeSwitchStates.GetOrAdd(
            stateKey,
            _ => new DisplayModeSwitchState
            {
                ExposedMode = previousMode,
                PendingTargetMode = string.Empty,
                LastUpdatedUtc = DateTime.UtcNow
            });

        var exposedMode = NormalizeStreamingMode(string.IsNullOrWhiteSpace(switchState.ExposedMode) ? previousMode : switchState.ExposedMode);
        if (string.Equals(exposedMode, desiredMode, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(previousSnapshot?.StreamingMode))
        {
            exposedMode = previousMode;
        }

        switchState.ExposedMode = exposedMode;
        switchState.PendingTargetMode = desiredMode;
        switchState.LastUpdatedUtc = DateTime.UtcNow;

        AppLog.Write(
            "StreamingMode",
            string.Format(
                "Mode switch semeado no publish: device={0}, janela={1}, exposto={2}, alvo={3}",
                display.DeviceId,
                windowKey,
                switchState.ExposedMode,
                switchState.PendingTargetMode));
    }

    private string ResolveEffectiveStreamingMode(WindowSession window, string desiredMode)
    {
        var assignedAddress = window.AssignedTarget?.NetworkAddress ?? string.Empty;
        if (string.IsNullOrWhiteSpace(assignedAddress))
        {
            return desiredMode;
        }

        var display = _registeredDisplays.Values.FirstOrDefault(x =>
            string.Equals(x.NetworkAddress, assignedAddress, StringComparison.OrdinalIgnoreCase));
        if (display is null || string.IsNullOrWhiteSpace(display.DeviceId))
        {
            return desiredMode;
        }

        var key = BuildDisplayWindowKey(display.DeviceId, window.Id.ToString("N"));
        if (!_displayModeSwitchStates.TryGetValue(key, out var state))
        {
            return desiredMode;
        }

        if (!string.IsNullOrWhiteSpace(state.PendingTargetMode))
        {
            return NormalizeStreamingMode(state.ExposedMode);
        }

        if (!string.IsNullOrWhiteSpace(state.ExposedMode))
        {
            return NormalizeStreamingMode(state.ExposedMode);
        }

        return desiredMode;
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

    private static string BuildRegisteredDisplayDeviceId(string? reportedDeviceId, string remoteAddress)
    {
        var normalizedReportedDeviceId = (reportedDeviceId ?? string.Empty).Trim();
        var normalizedRemoteAddress = (remoteAddress ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(normalizedReportedDeviceId))
        {
            return string.IsNullOrWhiteSpace(normalizedRemoteAddress)
                ? string.Empty
                : "roku-" + normalizedRemoteAddress.Replace(".", "-");
        }

        if (string.IsNullOrWhiteSpace(normalizedRemoteAddress))
        {
            return normalizedReportedDeviceId;
        }

        var normalizedRemoteSuffix = "-" + normalizedRemoteAddress.Replace(".", "-");
        if (normalizedReportedDeviceId.IndexOf('@') >= 0 ||
            normalizedReportedDeviceId.EndsWith(normalizedRemoteSuffix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedReportedDeviceId, normalizedRemoteAddress, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedReportedDeviceId;
        }

        return normalizedReportedDeviceId + "@" + normalizedRemoteAddress;
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

    private BridgeWindowSnapshot BuildWindowSnapshot(WindowSession window, int port, WebRtcBindMode bindMode, string specificIp, string? streamingModeOverride = null)
    {
        var streamingMode = NormalizeStreamingMode(streamingModeOverride ?? window.StreamingMode);
        var publishedUrl = string.IsNullOrWhiteSpace(window.PublishedWebRtcUrl)
            ? string.Empty
            : window.PublishedWebRtcUrl;
        var manualReloadVersion = _streamReloadVersions.TryGetValue(window.Id, out var reloadVersion)
            ? reloadVersion
            : 0;
        var rollingGeneration = _browserPanelRollingHlsService.GetStreamGeneration(window.Id);
        var streamReloadVersion = manualReloadVersion + rollingGeneration;

        string unifiedPanelStreamUrl;
        bool panelHlsReady;
        var publicHost = LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp);

        if (string.Equals(streamingMode, InteractionStreamingMode, StringComparison.OrdinalIgnoreCase))
        {
            var interactionPlaylistReady =
                _browserPanelInteractionHlsService.IsAvailable &&
                _browserPanelInteractionHlsService.HasPlaylist(window.Id);
            var interactionWarmupReady =
                interactionPlaylistReady &&
                _browserPanelInteractionHlsService.HasWarmupSegments(window.Id, InteractionFullscreenWarmupSegments);
            panelHlsReady = interactionWarmupReady;
            unifiedPanelStreamUrl = interactionWarmupReady
                ? string.Format("http://{0}:{1}/panel-interaction/{2}/index.m3u8?rv={3}", publicHost, port, window.Id.ToString("N"), streamReloadVersion)
                : string.Empty;
            if (interactionPlaylistReady && !interactionWarmupReady)
            {
                AppLog.Write(
                    "PanelInteractionHls",
                    $"Warmup pendente para janela {window.Id:N}: aguardando {InteractionFullscreenWarmupSegments} segmentos antes de expor fullscreen.");
            }
        }
        else
        {
            var candidateUnifiedPanelStreamUrl = _browserPanelRollingHlsService.IsAvailable
                ? string.Format("http://{0}:{1}/panel-roll/{2}/index.m3u8?rv={3}", publicHost, port, window.Id.ToString("N"), streamReloadVersion)
                : string.Empty;
            panelHlsReady =
                _browserPanelRollingHlsService.IsAvailable &&
                _browserPanelRollingHlsService.HasOutputFile(window.Id, "medium.m3u8");
            unifiedPanelStreamUrl = panelHlsReady ? candidateUnifiedPanelStreamUrl : string.Empty;
        }

        var interactionSuppressionEnabled = string.Equals(streamingMode, InteractionStreamingMode, StringComparison.OrdinalIgnoreCase);
        var directVideoOverlay = interactionSuppressionEnabled
            ? ResolveDirectVideoOverlay(window.Id)
            : DirectVideoOverlayBridgeSnapshot.None;
        _browserSnapshotService.SetDirectVideoSuppression(window.Id, interactionSuppressionEnabled);
        MaybeLogDirectOverlay(window.Id, directVideoOverlay);

        var autoOpenFullscreen =
            string.Equals(streamingMode, InteractionStreamingMode, StringComparison.OrdinalIgnoreCase)
                ? window.IsPrimaryExclusive
                : window.IsPrimaryExclusive && !string.IsNullOrWhiteSpace(unifiedPanelStreamUrl);

        var videoDiagnostics =
            !panelHlsReady && string.Equals(streamingMode, VideoStreamingMode, StringComparison.OrdinalIgnoreCase)
                ? ", diag=" + _browserPanelRollingHlsService.GetDiagnosticStatus(window.Id)
                : string.Empty;



        if (string.IsNullOrWhiteSpace(unifiedPanelStreamUrl))
        {
            AppLog.Write("PanelRollingHls", $"[BridgeSnapshot] Stream URL vazio para janela {window.Id:N}. HLS disponível: {_browserPanelRollingHlsService.IsAvailable}, medium.m3u8: {_browserPanelRollingHlsService.HasOutputFile(window.Id, "medium.m3u8")}, modo={streamingMode}, publishedUrl={publishedUrl}");
        }
        else
        {
            AppLog.Write("PanelRollingHls", $"[BridgeSnapshot] Stream URL atribuído para janela {window.Id:N}: {unifiedPanelStreamUrl}");
        }

        var bridgeSnapshotMessage = string.Format(
            "Bridge snapshot => janela={0}, modo={1}, stream={2}, autoFullscreen={3}, hlsReady={4}{5}",
            window.Id.ToString("N"),
            streamingMode,
            string.IsNullOrWhiteSpace(unifiedPanelStreamUrl) ? "<sem-stream>" : unifiedPanelStreamUrl,
            autoOpenFullscreen,
            panelHlsReady,
            videoDiagnostics);
        MaybeLogBridgeSnapshot(window.Id, bridgeSnapshotMessage);

        // Log detalhado para diagnóstico
        AppLog.Write(
            "PanelRollingHls",
            "[DEBUG] BuildWindowSnapshot: Id=" + window.Id.ToString("N") +
            ", StreamingMode=" + streamingMode +
            ", PublishedWebRtcUrl=" + publishedUrl +
            ", UnifiedPanelStreamUrl=" + unifiedPanelStreamUrl +
            ", StreamUrl=" + (string.IsNullOrWhiteSpace(unifiedPanelStreamUrl) ? publishedUrl : unifiedPanelStreamUrl) +
            ", AutoOpenFullscreen=" + autoOpenFullscreen +
            ", IsPrimaryExclusive=" + window.IsPrimaryExclusive +
            ", PanelHlsReady=" + panelHlsReady +
            ", HlsAvailable=" + _browserPanelRollingHlsService.IsAvailable +
            ", HasMediumM3u8=" + _browserPanelRollingHlsService.HasOutputFile(window.Id, "medium.m3u8")
        );

        return new BridgeWindowSnapshot
        {
            Id = window.Id.ToString("N"),
            Title = string.IsNullOrWhiteSpace(window.Title) ? "Janela sem titulo" : window.Title,
            State = window.State.ToString(),
            InitialUrl = window.InitialUri?.ToString() ?? string.Empty,
            PublishedWebRtcUrl = publishedUrl,
            // StreamUrl: always set to the HLS URL if available and ready, otherwise fallback to publishedUrl
            StreamUrl = string.IsNullOrWhiteSpace(unifiedPanelStreamUrl) ? publishedUrl : unifiedPanelStreamUrl,
            IsPublishing = window.IsWebRtcPublishingEnabled,
            ServerUrl = string.Format("http://{0}:{1}", LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), port),
            ThumbnailUrl = string.Format("http://{0}:{1}/thumbnails/{2}.jpg", LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), port, window.Id.ToString("N")),
            AudioStreamUrl = directVideoOverlay.Enabled
                ? string.Empty
                : string.IsNullOrWhiteSpace(unifiedPanelStreamUrl)
                ? (_browserAudioHlsService.IsAvailable
                    ? string.Format("http://{0}:{1}/audio-hls/{2}/index.m3u8", LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), port, window.Id.ToString("N"))
                    : string.Format("http://{0}:{1}/audio/{2}.wav?seconds=2.5", LinkRtcAddressBuilder.ResolvePublicHost(bindMode, specificIp), port, window.Id.ToString("N")))
                : string.Empty,
            AudioAvailable = !directVideoOverlay.Enabled && string.IsNullOrWhiteSpace(unifiedPanelStreamUrl) && _browserAudioCaptureService.HasRecentAudio(window.Id),
            ProfileName = window.ProfileName ?? string.Empty,
            ActiveSessionId = window.ActiveSessionId == Guid.Empty ? string.Empty : window.ActiveSessionId.ToString("N"),
            ActiveSessionName = window.ActiveSessionName ?? string.Empty,
            StreamingMode = streamingMode,
            RequestedStreamingMode = string.Empty,
            ModeSwitchPending = false,
            AutoOpenFullscreen = autoOpenFullscreen,
            AssignedDisplayId = window.AssignedTarget?.Id.ToString("N") ?? string.Empty,
            AssignedDisplayName = window.AssignedTarget?.Name ?? string.Empty,
            AssignedDisplayAddress = window.AssignedTarget?.NetworkAddress ?? string.Empty,
            DirectVideoOverlayEnabled = directVideoOverlay.Enabled,
            DirectVideoSourceUrl = directVideoOverlay.SourceUrl,
            DirectVideoStreamUrl = directVideoOverlay.StreamUrl,
            DirectVideoStreamFormat = directVideoOverlay.StreamFormat,
            DirectVideoNormalizedLeft = directVideoOverlay.NormalizedLeft,
            DirectVideoNormalizedTop = directVideoOverlay.NormalizedTop,
            DirectVideoNormalizedWidth = directVideoOverlay.NormalizedWidth,
            DirectVideoNormalizedHeight = directVideoOverlay.NormalizedHeight,
            DirectVideoQualityLabel = directVideoOverlay.QualityLabel,
            DirectVideoQualityOptions = directVideoOverlay.QualityOptions
        };
    }

    private DirectVideoOverlayBridgeSnapshot ResolveDirectVideoOverlay(Guid windowId)
    {
        var detected = _browserSnapshotService.GetDirectVideoOverlayState(windowId);
        if (!detected.Detected || string.IsNullOrWhiteSpace(detected.SourceUrl))
        {
            return DirectVideoOverlayBridgeSnapshot.None;
        }

        var normalizedSourceUrl = NormalizeYoutubeDirectSourceUrl(detected.SourceUrl);
        if (string.IsNullOrWhiteSpace(normalizedSourceUrl))
        {
            return DirectVideoOverlayBridgeSnapshot.None;
        }

        var resolved = TryGetResolvedDirectPlayback(normalizedSourceUrl);
        if (resolved is null || !resolved.Result.Ok || string.IsNullOrWhiteSpace(resolved.Result.StreamUrl))
        {
            return DirectVideoOverlayBridgeSnapshot.None;
        }

        return new DirectVideoOverlayBridgeSnapshot
        {
            Enabled = true,
            SourceUrl = normalizedSourceUrl,
            StreamUrl = resolved.Result.StreamUrl,
            StreamFormat = resolved.Result.StreamFormat,
            QualityLabel = resolved.Result.QualityLabel,
            QualityOptions = resolved.Result.QualityOptions
                .Select(x => new BridgeDirectVideoQualityOption
                {
                    Label = x.Label,
                    StreamUrl = x.StreamUrl,
                    StreamFormat = x.StreamFormat
                })
                .ToList(),
            NormalizedLeft = ClampNormalizedCoordinate(detected.NormalizedLeft),
            NormalizedTop = ClampNormalizedCoordinate(detected.NormalizedTop),
            NormalizedWidth = ClampNormalizedCoordinate(detected.NormalizedWidth),
            NormalizedHeight = ClampNormalizedCoordinate(detected.NormalizedHeight)
        };
    }

    private void MaybeLogDirectOverlay(Guid windowId, DirectVideoOverlayBridgeSnapshot overlay)
    {
        var message = overlay.Enabled
            ? string.Format(
                "Overlay direto ativo => janela={0}, source={1}, format={2}, quality={3}, qualities={4}, stream={5}, rect={6:0.000},{7:0.000},{8:0.000},{9:0.000}",
                windowId.ToString("N"),
                overlay.SourceUrl,
                overlay.StreamFormat,
                overlay.QualityLabel,
                overlay.QualityOptions is null ? 0 : overlay.QualityOptions.Count,
                overlay.StreamUrl,
                overlay.NormalizedLeft,
                overlay.NormalizedTop,
                overlay.NormalizedWidth,
                overlay.NormalizedHeight)
            : string.Format("Overlay direto inativo => janela={0}", windowId.ToString("N"));
        if (_lastDirectOverlayLogByWindow.TryGetValue(windowId, out var previous) &&
            string.Equals(previous, message, StringComparison.Ordinal))
        {
            return;
        }

        _lastDirectOverlayLogByWindow[windowId] = message;
        AppLog.Write("DirectOverlay", message);
    }

    private CachedDirectPlaybackResolveResult? TryGetResolvedDirectPlayback(string youtubeUrl)
    {
        if (string.IsNullOrWhiteSpace(youtubeUrl))
        {
            return null;
        }

        if (_cachedDirectPlaybackByYoutubeUrl.TryGetValue(youtubeUrl, out var cached))
        {
            var ttl = cached.Result.Ok ? DirectYoutubeResolveSuccessTtl : DirectYoutubeResolveFailureTtl;
            if (DateTime.UtcNow - cached.UpdatedUtc <= ttl)
            {
                return cached;
            }
        }

        _ = _directPlaybackResolveTasks.GetOrAdd(
            youtubeUrl,
            url => Task.Run(() =>
            {
                try
                {
                    AppLog.Write("DirectOverlay", string.Format("Resolvendo YouTube direto => {0}", url));
                    var resolved = ResolveYoutubeDirectPlayback(url);
                    var entry = new CachedDirectPlaybackResolveResult
                    {
                        Result = resolved,
                        UpdatedUtc = DateTime.UtcNow
                    };
                    _cachedDirectPlaybackByYoutubeUrl[url] = entry;
                    AppLog.Write(
                        "DirectOverlay",
                        resolved.Ok
                            ? string.Format("YouTube direto resolvido => format={0}, url={1}", resolved.StreamFormat, resolved.StreamUrl)
                            : string.Format("Falha ao resolver YouTube direto => erro={0}, source={1}", resolved.Error, url));
                    return entry;
                }
                finally
                {
                    _directPlaybackResolveTasks.TryRemove(url, out _);
                }
            }));

        return null;
    }

    private static string NormalizeYoutubeDirectSourceUrl(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            AppLog.Write("DirectOverlay", $"[NormalizeYoutubeDirectSourceUrl] sourceUrl vazio ou nulo");
            return string.Empty;
        }

        AppLog.Write("DirectOverlay", $"[NormalizeYoutubeDirectSourceUrl] Recebido: {sourceUrl}");
        string normalized = sourceUrl;
        try
        {
            var uri = new Uri(sourceUrl, UriKind.Absolute);
            var host = uri.Host ?? string.Empty;
            if (host.IndexOf("youtu.be", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var videoId = uri.AbsolutePath.Trim('/').Split('/')[0];
                normalized = string.IsNullOrWhiteSpace(videoId)
                    ? sourceUrl
                    : string.Format("https://www.youtube.com/watch?v={0}", videoId);
                AppLog.Write("DirectOverlay", $"[NormalizeYoutubeDirectSourceUrl] youtu.be detectado, normalizado: {normalized}");
                return normalized;
            }

            if (host.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                host.IndexOf("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (uri.AbsolutePath.StartsWith("/watch", StringComparison.OrdinalIgnoreCase))
                {
                    var query = ParseQueryString(uri.Query.TrimStart('?'));
                    var videoId = GetValue(query, "v");
                    normalized = string.IsNullOrWhiteSpace(videoId)
                        ? sourceUrl
                        : string.Format("https://www.youtube.com/watch?v={0}", videoId);
                    AppLog.Write("DirectOverlay", $"[NormalizeYoutubeDirectSourceUrl] youtube.com/watch detectado, normalizado: {normalized}");
                    return normalized;
                }

                if (uri.AbsolutePath.IndexOf("/embed/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    var embedIndex = Array.FindIndex(segments, segment => string.Equals(segment, "embed", StringComparison.OrdinalIgnoreCase));
                    if (embedIndex >= 0 && embedIndex + 1 < segments.Length && !string.IsNullOrWhiteSpace(segments[embedIndex + 1]))
                    {
                        normalized = string.Format("https://www.youtube.com/watch?v={0}", segments[embedIndex + 1]);
                        AppLog.Write("DirectOverlay", $"[NormalizeYoutubeDirectSourceUrl] youtube.com/embed detectado, normalizado: {normalized}");
                        return normalized;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("DirectOverlay", $"[NormalizeYoutubeDirectSourceUrl] Exceção ao normalizar: {ex.Message}");
        }

        AppLog.Write("DirectOverlay", $"[NormalizeYoutubeDirectSourceUrl] Retornando original: {normalized}");
        return normalized;
    }

    private static double ClampNormalizedCoordinate(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        if (value < 0d)
        {
            return 0d;
        }

        if (value > 1d)
        {
            return 1d;
        }

        return value;
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
        return AutomaticRokuSideloadEnabled;
    }

    private static BridgeWindowSnapshot CloneWindowSnapshotWithoutStreams(BridgeWindowSnapshot source)
    {
        return new BridgeWindowSnapshot
        {
            Id = source.Id,
            Title = source.Title,
            State = source.State,
            InitialUrl = source.InitialUrl,
            PublishedWebRtcUrl = source.PublishedWebRtcUrl,
            StreamUrl = string.Empty,
            IsPublishing = source.IsPublishing,
            ServerUrl = source.ServerUrl,
            ThumbnailUrl = source.ThumbnailUrl,
            AudioStreamUrl = string.Empty,
            AudioAvailable = false,
            ProfileName = source.ProfileName,
            ActiveSessionId = source.ActiveSessionId,
            ActiveSessionName = source.ActiveSessionName,
            StreamingMode = source.StreamingMode,
            RequestedStreamingMode = source.RequestedStreamingMode,
            ModeSwitchPending = source.ModeSwitchPending,
            AutoOpenFullscreen = source.AutoOpenFullscreen,
            AssignedDisplayId = source.AssignedDisplayId,
            AssignedDisplayName = source.AssignedDisplayName,
            AssignedDisplayAddress = source.AssignedDisplayAddress,
            DirectVideoOverlayEnabled = source.DirectVideoOverlayEnabled,
            DirectVideoSourceUrl = source.DirectVideoSourceUrl,
            DirectVideoStreamUrl = source.DirectVideoStreamUrl,
            DirectVideoStreamFormat = source.DirectVideoStreamFormat,
            DirectVideoNormalizedLeft = source.DirectVideoNormalizedLeft,
            DirectVideoNormalizedTop = source.DirectVideoNormalizedTop,
            DirectVideoNormalizedWidth = source.DirectVideoNormalizedWidth,
            DirectVideoNormalizedHeight = source.DirectVideoNormalizedHeight,
            DirectVideoQualityLabel = source.DirectVideoQualityLabel,
            DirectVideoQualityOptions = source.DirectVideoQualityOptions
        };
    }

    private static YouTubeDirectResolveResult ResolveYoutubeDirectPlayback(string youtubeUrl)
    {
        var repoRoot = TryFindMonorepoRoot();
        string ytDlpPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            ytDlpPath = Path.Combine(repoRoot, "tools", "yt-dlp", "yt-dlp.exe");
        }
        // fallback: procurar no caminho do executável
        if (string.IsNullOrWhiteSpace(ytDlpPath) || !File.Exists(ytDlpPath))
        {
            var exeDir = AppContext.BaseDirectory;
            var fallbackPath = Path.Combine(exeDir, "tools", "yt-dlp", "yt-dlp.exe");
            if (File.Exists(fallbackPath))
            {
                ytDlpPath = fallbackPath;
            }
        }
        if (string.IsNullOrWhiteSpace(ytDlpPath) || !File.Exists(ytDlpPath))
        {
            return YouTubeDirectResolveResult.Fail("yt_dlp_ausente");
        }
        var nodePath = ResolveNodePath();
        var jsRuntime = string.IsNullOrWhiteSpace(nodePath)
            ? string.Empty
            : string.Format("node:{0}", nodePath);
        foreach (var extractorArgs in EnumerateYouTubeExtractorArgs())
        {
            var resolved = TryResolveYoutubeDirectPlaybackVariant(ytDlpPath, jsRuntime, youtubeUrl, extractorArgs);
            if (resolved.Ok)
            {
                return resolved;
            }
        }

        return YouTubeDirectResolveResult.Fail("sem_url_compativel");
    }

    private static IEnumerable<string> EnumerateYouTubeExtractorArgs()
    {
        yield return string.Empty;
        yield return "--extractor-args \"youtube:player_client=ios\"";
        yield return "--extractor-args \"youtube:player_client=tv,ios,web\"";
    }

    private static YouTubeDirectResolveResult TryResolveYoutubeDirectPlaybackVariant(string ytDlpPath, string jsRuntime, string youtubeUrl, string extractorArgs)
    {
        var scannedQualityOptions = ScanYoutubeDirectQualityOptions(ytDlpPath, jsRuntime, youtubeUrl, extractorArgs);
        if (scannedQualityOptions.Count > 0)
        {
            var preferred = scannedQualityOptions
                .OrderByDescending(GetDirectQualityPreferenceScore)
                .First();
            AppLog.Write(
                "DirectOverlay",
                string.Format(
                    "Quality scan => extractor={0}, muxed={1}, selected={2} format={3}",
                    string.IsNullOrWhiteSpace(extractorArgs) ? "<padrao>" : extractorArgs,
                    string.Join(", ", scannedQualityOptions.Select(x => string.Format("{0}:{1}", x.Label, x.StreamFormat)).Distinct(StringComparer.OrdinalIgnoreCase)),
                    preferred.Label,
                    preferred.StreamFormat));
            return YouTubeDirectResolveResult.Success(preferred.StreamUrl, preferred.StreamFormat, preferred.Label, scannedQualityOptions);
        }

        var qualityOptions = new List<YouTubeDirectQualityOption>();
        TryAppendDirectQualityOption(qualityOptions, "720p", RunProcessCaptureFirstNonEmptyLine(ytDlpPath, BuildYtDlpArguments(jsRuntime, extractorArgs, string.Format("-g -f \"22\" \"{0}\"", youtubeUrl))), "mp4");
        TryAppendDirectQualityOption(qualityOptions, "480p", RunProcessCaptureFirstNonEmptyLine(ytDlpPath, BuildYtDlpArguments(jsRuntime, extractorArgs, string.Format("-g -f \"59/78\" \"{0}\"", youtubeUrl))), "mp4");
        TryAppendDirectQualityOption(qualityOptions, "360p", RunProcessCaptureFirstNonEmptyLine(ytDlpPath, BuildYtDlpArguments(jsRuntime, extractorArgs, string.Format("-g -f \"18\" \"{0}\"", youtubeUrl))), "mp4");

        if (qualityOptions.Count > 0)
        {
            var preferred = qualityOptions
                .OrderByDescending(x => ParseQualityRank(x.Label))
                .First();
            return YouTubeDirectResolveResult.Success(preferred.StreamUrl, preferred.StreamFormat, preferred.Label, qualityOptions);
        }

        var compatibleHlsUrl = RunProcessCaptureFirstNonEmptyLine(
            ytDlpPath,
            BuildYtDlpArguments(jsRuntime, extractorArgs, string.Format("-g -f \"95-2/95-1/95-0/94-2/94-1/94-0/93-2/93-1/93-0/best[ext=mp4]/best\" \"{0}\"", youtubeUrl)));
        if (!string.IsNullOrWhiteSpace(compatibleHlsUrl) && !string.Equals(compatibleHlsUrl, "NA", StringComparison.OrdinalIgnoreCase))
        {
            if (compatibleHlsUrl.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0 ||
                compatibleHlsUrl.IndexOf("manifest/hls_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return YouTubeDirectResolveResult.Success(
                    compatibleHlsUrl,
                    "hls",
                    "Auto",
                    new List<YouTubeDirectQualityOption>
                    {
                        new YouTubeDirectQualityOption
                        {
                            Label = "Auto",
                            StreamUrl = compatibleHlsUrl,
                            StreamFormat = "hls"
                        }
                    });
            }

            return YouTubeDirectResolveResult.Success(
                compatibleHlsUrl,
                "mp4",
                "Auto",
                new List<YouTubeDirectQualityOption>
                {
                    new YouTubeDirectQualityOption
                    {
                        Label = "Auto",
                        StreamUrl = compatibleHlsUrl,
                        StreamFormat = "mp4"
                    }
                });
        }

        var manifestUrl = RunProcessCaptureFirstNonEmptyLine(
            ytDlpPath,
            BuildYtDlpArguments(jsRuntime, extractorArgs, string.Format("--print manifest_url \"{0}\"", youtubeUrl)));
        if (!string.IsNullOrWhiteSpace(manifestUrl) && !string.Equals(manifestUrl, "NA", StringComparison.OrdinalIgnoreCase))
        {
            if (manifestUrl.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0 ||
                manifestUrl.IndexOf("manifest/hls_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return YouTubeDirectResolveResult.Success(
                    manifestUrl,
                    "hls",
                    "Auto",
                    new List<YouTubeDirectQualityOption>
                    {
                        new YouTubeDirectQualityOption
                        {
                            Label = "Auto",
                            StreamUrl = manifestUrl,
                            StreamFormat = "hls"
                        }
                    });
            }

            if (manifestUrl.IndexOf(".mpd", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return YouTubeDirectResolveResult.Success(
                    manifestUrl,
                    "dash",
                    "Auto",
                    new List<YouTubeDirectQualityOption>
                    {
                        new YouTubeDirectQualityOption
                        {
                            Label = "Auto",
                            StreamUrl = manifestUrl,
                            StreamFormat = "dash"
                        }
                    });
            }
        }

        return YouTubeDirectResolveResult.Fail("sem_url_compativel");
    }

    private static void TryAppendDirectQualityOption(List<YouTubeDirectQualityOption> options, string label, string streamUrl, string streamFormat)
    {
        if (options is null || string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(streamUrl) || string.Equals(streamUrl, "NA", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (options.Any(x => string.Equals(x.StreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        options.Add(new YouTubeDirectQualityOption
        {
            Label = label,
            StreamUrl = streamUrl,
            StreamFormat = streamFormat
        });
    }

    private static int ParseQualityRank(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return 0;
        }

        var digits = new string(label.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static int GetDirectQualityPreferenceScore(YouTubeDirectQualityOption option)
    {
        if (option is null)
        {
            return 0;
        }

        var score = ParseQualityRank(option.Label) * 10;
        if (option.Label.IndexOf("60", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score += 1;
        }

        if (string.Equals(option.StreamFormat, "hls", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        return score;
    }

    private static List<YouTubeDirectQualityOption> ScanYoutubeDirectQualityOptions(string ytDlpPath, string jsRuntime, string youtubeUrl, string extractorArgs)
    {
        var json = RunProcessCaptureOutput(
            ytDlpPath,
            BuildYtDlpArguments(jsRuntime, extractorArgs, string.Format("--no-warnings --no-playlist -J \"{0}\"", youtubeUrl)));
        if (string.IsNullOrWhiteSpace(json))
        {
            AppLog.Write("DirectOverlay", "Quality scan => yt-dlp nao retornou JSON");
            return new List<YouTubeDirectQualityOption>();
        }

        try
        {
            var root = JObject.Parse(json);
            var formats = root["formats"] as JArray;
            if (formats is null || formats.Count == 0)
            {
                AppLog.Write("DirectOverlay", "Quality scan => JSON sem formats");
                return new List<YouTubeDirectQualityOption>();
            }

            var options = new List<YouTubeDirectQualityOption>();
            var adaptiveHeights = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var formatToken in formats.OfType<JObject>())
            {
                var vcodec = formatToken.Value<string>("vcodec") ?? string.Empty;
                var acodec = formatToken.Value<string>("acodec") ?? string.Empty;
                var protocol = formatToken.Value<string>("protocol") ?? string.Empty;
                var ext = formatToken.Value<string>("ext") ?? string.Empty;
                var height = formatToken.Value<int?>("height") ?? 0;
                var fps = formatToken.Value<double?>("fps") ?? 0;

                if (!string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase) &&
                    height > 0)
                {
                    adaptiveHeights.Add(BuildQualityLabel(height, fps));
                }

                if (string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (height <= 0)
                {
                    continue;
                }

                if (string.Equals(protocol, "m3u8_native", StringComparison.OrdinalIgnoreCase))
                {
                    var hlsUrl = formatToken.Value<string>("url") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(hlsUrl))
                    {
                        hlsUrl = formatToken.Value<string>("manifest_url") ?? string.Empty;
                    }

                    TryAppendDirectQualityOption(options, BuildQualityLabel(height, fps), hlsUrl, "hls");
                    continue;
                }

                if (string.Equals(protocol, "https", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ext, "mp4", StringComparison.OrdinalIgnoreCase))
                {
                    TryAppendDirectQualityOption(options, BuildQualityLabel(height, fps), formatToken.Value<string>("url") ?? string.Empty, "mp4");
                }
            }

            AppLog.Write(
                "DirectOverlay",
                string.Format(
                    "Quality scan => muxed={0}, adaptiveOnly={1}",
                    options.Count == 0 ? "<nenhuma>" : string.Join(", ", options.Select(x => string.Format("{0}:{1}", x.Label, x.StreamFormat)).Distinct(StringComparer.OrdinalIgnoreCase)),
                    adaptiveHeights.Count == 0 ? "<nenhuma>" : string.Join(", ", adaptiveHeights)));
            return options
                .GroupBy(x => x.Label + "|" + x.StreamFormat, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(GetDirectQualityPreferenceScore)
                    .First())
                .OrderByDescending(GetDirectQualityPreferenceScore)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Write("DirectOverlay", string.Format("Quality scan => falha ao processar JSON do yt-dlp: {0}", ex.Message));
            return new List<YouTubeDirectQualityOption>();
        }
    }

    private static string ResolveNodePath()
    {
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in pathValue.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(segment.Trim(), "node.exe");
                if (File.Exists(candidate))
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

    private static string BuildYtDlpArguments(string jsRuntime, string extractorArgs, string commandSuffix)
    {
        if (string.IsNullOrWhiteSpace(commandSuffix))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(jsRuntime))
        {
            builder.AppendFormat("--js-runtimes \"{0}\" ", jsRuntime);
        }

        if (!string.IsNullOrWhiteSpace(extractorArgs))
        {
            builder.Append(extractorArgs).Append(' ');
        }

        builder.Append(commandSuffix);
        return builder.ToString().Trim();
    }

    private static string BuildQualityLabel(int height, double fps)
    {
        if (height <= 0)
        {
            return "Auto";
        }

        var roundedFps = (int)Math.Round(fps, MidpointRounding.AwayFromZero);
        if (roundedFps >= 50)
        {
            return string.Format("{0}p60", height);
        }

        return string.Format("{0}p", height);
    }

    private static string RunProcessCaptureFirstNonEmptyLine(string fileName, string arguments)
    {
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory
            };

            process.Start();
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit(120000);

            if (process.ExitCode != 0)
            {
                AppLog.Write("RokuDirect", string.Format("yt-dlp falhou: exit={0}, stderr={1}", process.ExitCode, standardError));
                return string.Empty;
            }

            var lines = standardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
        }
    }

    private static string RunProcessCaptureOutput(string fileName, string arguments)
    {
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory
            };

            process.Start();
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit(120000);

            if (process.ExitCode != 0)
            {
                AppLog.Write("RokuDirect", string.Format("yt-dlp JSON falhou: exit={0}, stderr={1}", process.ExitCode, standardError));
                return string.Empty;
            }

            return standardOutput.Trim();
        }
    }

    private static string TryFindMonorepoRoot()
    {
        try
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var manifestPath = Path.Combine(current.FullName, "manifest");
                var toolsPath = Path.Combine(current.FullName, "tools");
                var sourcePath = Path.Combine(current.FullName, "source");
                if (File.Exists(manifestPath) && Directory.Exists(toolsPath) && Directory.Exists(sourcePath))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string BuildDisplayWindowKey(string deviceId, string windowId)
    {
        return string.Format("{0}|{1}", deviceId.Trim(), windowId.Trim());
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
        var normalizedFileName =
            fileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                ? "<segment>"
                : fileName;
        var key = windowId.ToString("N") + "|" + normalizedFileName;
        var now = DateTime.UtcNow;
        var hadPreviousStatus = _lastPanelHlsStatusByKey.TryGetValue(key, out var previousStatus);
        var statusChanged = !hadPreviousStatus || previousStatus != ok;
        var minInterval = ok ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(2);
        if (!statusChanged &&
            _lastPanelHlsLogUtc.TryGetValue(key, out var previous) &&
            now - previous < minInterval)
        {
            return;
        }

        _lastPanelHlsLogUtc[key] = now;
        _lastPanelHlsStatusByKey[key] = ok;
        AppLog.Write(
            "PanelHls",
            string.Format(
                "Requisicao HLS do painel: janela={0}, arquivo={1}, ok={2}",
                windowId.ToString("N"),
                normalizedFileName,
                ok));
    }

    private void MaybeLogBridgeSnapshot(Guid windowId, string message)
    {
        var now = DateTime.UtcNow;
        var changed = !_lastBridgeSnapshotLogByWindow.TryGetValue(windowId, out var previousMessage) ||
                      !string.Equals(previousMessage, message, StringComparison.Ordinal);
        if (!changed &&
            _lastBridgeSnapshotLogUtcByWindow.TryGetValue(windowId, out var previousUtc) &&
            now - previousUtc < TimeSpan.FromSeconds(15))
        {
            return;
        }

        _lastBridgeSnapshotLogByWindow[windowId] = message;
        _lastBridgeSnapshotLogUtcByWindow[windowId] = now;
        AppLog.Write("StreamingMode", message);
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

    private static string NormalizeStreamingMode(string? value)
    {
        return string.Equals((value ?? string.Empty).Trim(), VideoStreamingMode, StringComparison.OrdinalIgnoreCase)
            ? VideoStreamingMode
            : InteractionStreamingMode;
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

    [DataMember(Name = "streamingMode", Order = 15)]
    public string StreamingMode { get; set; } = "Interacao";

    [DataMember(Name = "requestedStreamingMode", Order = 16)]
    public string RequestedStreamingMode { get; set; } = string.Empty;

    [DataMember(Name = "modeSwitchPending", Order = 17)]
    public bool ModeSwitchPending { get; set; }

    [DataMember(Name = "autoOpenFullscreen", Order = 18)]
    public bool AutoOpenFullscreen { get; set; }

    [DataMember(Name = "assignedDisplayId", Order = 19)]
    public string AssignedDisplayId { get; set; } = string.Empty;

    [DataMember(Name = "assignedDisplayName", Order = 20)]
    public string AssignedDisplayName { get; set; } = string.Empty;

    [DataMember(Name = "assignedDisplayAddress", Order = 21)]
    public string AssignedDisplayAddress { get; set; } = string.Empty;

    [DataMember(Name = "directVideoOverlayEnabled", Order = 22)]
    public bool DirectVideoOverlayEnabled { get; set; }

    [DataMember(Name = "directVideoSourceUrl", Order = 23)]
    public string DirectVideoSourceUrl { get; set; } = string.Empty;

    [DataMember(Name = "directVideoStreamUrl", Order = 24)]
    public string DirectVideoStreamUrl { get; set; } = string.Empty;

    [DataMember(Name = "directVideoStreamFormat", Order = 25)]
    public string DirectVideoStreamFormat { get; set; } = string.Empty;

    [DataMember(Name = "directVideoNormalizedLeft", Order = 26)]
    public double DirectVideoNormalizedLeft { get; set; }

    [DataMember(Name = "directVideoNormalizedTop", Order = 27)]
    public double DirectVideoNormalizedTop { get; set; }

    [DataMember(Name = "directVideoNormalizedWidth", Order = 28)]
    public double DirectVideoNormalizedWidth { get; set; }

    [DataMember(Name = "directVideoNormalizedHeight", Order = 29)]
    public double DirectVideoNormalizedHeight { get; set; }

    [DataMember(Name = "directVideoQualityLabel", Order = 30)]
    public string DirectVideoQualityLabel { get; set; } = string.Empty;

    [DataMember(Name = "directVideoQualityOptions", Order = 31)]
    public List<BridgeDirectVideoQualityOption> DirectVideoQualityOptions { get; set; } = new List<BridgeDirectVideoQualityOption>();
}

internal sealed class DisplayModeSwitchState
{
    public string ExposedMode { get; set; } = "Interacao";

    public string PendingTargetMode { get; set; } = string.Empty;

    public DateTime LastUpdatedUtc { get; set; }
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

internal sealed class DirectVideoOverlayBridgeSnapshot
{
    public static DirectVideoOverlayBridgeSnapshot None => new DirectVideoOverlayBridgeSnapshot();

    public bool Enabled { get; set; }

    public string SourceUrl { get; set; } = string.Empty;

    public string StreamUrl { get; set; } = string.Empty;

    public string StreamFormat { get; set; } = string.Empty;

    public string QualityLabel { get; set; } = string.Empty;

    public List<BridgeDirectVideoQualityOption> QualityOptions { get; set; } = new List<BridgeDirectVideoQualityOption>();

    public double NormalizedLeft { get; set; }

    public double NormalizedTop { get; set; }

    public double NormalizedWidth { get; set; }

    public double NormalizedHeight { get; set; }
}

[DataContract]
public sealed class BridgeDirectVideoQualityOption
{
    [DataMember(Name = "label", Order = 1)]
    public string Label { get; set; } = string.Empty;

    [DataMember(Name = "streamUrl", Order = 2)]
    public string StreamUrl { get; set; } = string.Empty;

    [DataMember(Name = "streamFormat", Order = 3)]
    public string StreamFormat { get; set; } = string.Empty;
}

internal sealed class CachedDirectPlaybackResolveResult
{
    public YouTubeDirectResolveResult Result { get; set; } = YouTubeDirectResolveResult.Fail("pendente");

    public DateTime UpdatedUtc { get; set; }
}

public sealed class YouTubeDirectResolveResult
{
    public bool Ok { get; set; }

    public string StreamUrl { get; set; } = string.Empty;

    public string StreamFormat { get; set; } = string.Empty;

    public string QualityLabel { get; set; } = string.Empty;

    public List<YouTubeDirectQualityOption> QualityOptions { get; set; } = new List<YouTubeDirectQualityOption>();

    public string Error { get; set; } = string.Empty;

    public static YouTubeDirectResolveResult Success(string streamUrl, string streamFormat, string qualityLabel, List<YouTubeDirectQualityOption> qualityOptions)
    {
        return new YouTubeDirectResolveResult
        {
            Ok = true,
            StreamUrl = streamUrl ?? string.Empty,
            StreamFormat = streamFormat ?? string.Empty,
            QualityLabel = qualityLabel ?? string.Empty,
            QualityOptions = qualityOptions ?? new List<YouTubeDirectQualityOption>()
        };
    }

    public static YouTubeDirectResolveResult Fail(string error)
    {
        return new YouTubeDirectResolveResult
        {
            Ok = false,
            Error = error ?? string.Empty
        };
    }
}

public sealed class YouTubeDirectQualityOption
{
    public string Label { get; set; } = string.Empty;

    public string StreamUrl { get; set; } = string.Empty;

    public string StreamFormat { get; set; } = string.Empty;
}

public sealed class RokuPowerBatchResult
{
    public int TargetedCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int SkippedCount { get; set; }
}

