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
        var discoveryService = new StubDisplayDiscoveryService(knownDisplayStore);
        var captureSessionFactory = new InMemoryCaptureSessionFactory();
        var transportResolver = new DefaultDisplayTransportResolver(transports);
        var routingService = new RoutingService(captureSessionFactory, transportResolver);
        var profileStore = new ProfileStore();
        var browserSnapshotService = new BrowserSnapshotService();
        var webRtcPublisherService = new LocalWebRtcPublisherService(browserSnapshotService);
        var appUpdateManifestService = new AppUpdateManifestService();
        var viewModel = new MainViewModel(browserHost, discoveryService, routingService, profileStore, webRtcPublisherService, knownDisplayStore, appUpdateManifestService);

        return new MainWindow(viewModel, browserSnapshotService);
    }
}
