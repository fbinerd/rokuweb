using System;
using System.Collections.Generic;
using WindowManager.App.Profiles;
using WindowManager.App.Runtime.Discovery;
using WindowManager.App.Runtime.Publishing;
using WindowManager.App.ViewModels;
using WindowManager.Core.Abstractions;
using WindowManager.Core.Services;

namespace WindowManager.App.Runtime;

public sealed class Bootstrapper
{
    public MainWindow CreateMainWindow()
    {
        var transports = new List<IDisplayTransport>
        {
            new MiracastTransport(),
            new LanStreamingTransport()
        };

        var browserHost = new StubBrowserInstanceHost();
        var knownDisplayStore = new KnownDisplayStore();
        var captureSessionFactory = new InMemoryCaptureSessionFactory();
        var transportResolver = new DefaultDisplayTransportResolver(transports);
        var routingService = new RoutingService(captureSessionFactory, transportResolver);
        var profileStore = new ProfileStore();
        var activeSessionStore = new ActiveSessionStore();
        var browserSnapshotService = new BrowserSnapshotService();
        var browserAudioCaptureService = new BrowserAudioCaptureService();
        var browserAudioHlsService = new BrowserAudioHlsService(browserAudioCaptureService);
        var browserPanelRollingHlsService = new BrowserPanelRollingHlsService(browserSnapshotService, browserAudioCaptureService);
        var appUpdateManifestService = new AppUpdateManifestService();
        var appUpdatePreferenceStore = new AppUpdatePreferenceStore();
        var webRtcPublisherService = new LocalWebRtcPublisherService(browserSnapshotService, browserAudioCaptureService, browserAudioHlsService, browserPanelRollingHlsService, appUpdatePreferenceStore);
        var discoveryService = new StubDisplayDiscoveryService(knownDisplayStore, appUpdatePreferenceStore);
        var appSelfUpdateService = new AppSelfUpdateService();
        var appDataMaintenanceService = new AppDataMaintenanceService();
        var viewModel = new MainViewModel(browserHost, discoveryService, routingService, profileStore, activeSessionStore, webRtcPublisherService, knownDisplayStore, appUpdateManifestService, appUpdatePreferenceStore, appSelfUpdateService, appDataMaintenanceService);

        return new MainWindow(viewModel, browserSnapshotService, browserAudioCaptureService);
    }
}
