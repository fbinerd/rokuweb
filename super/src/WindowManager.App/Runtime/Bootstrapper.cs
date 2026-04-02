using System;
using System.Collections.Generic;
using System.IO;
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
        File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] CreateMainWindow START\n");
        var transports = new List<IDisplayTransport>();
        try {
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando MiracastTransport\n");
            transports.Add(new MiracastTransport());
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando LanStreamingTransport\n");
            transports.Add(new LanStreamingTransport());
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando StubBrowserInstanceHost\n");
            var browserHost = new StubBrowserInstanceHost();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando KnownDisplayStore\n");
            var knownDisplayStore = new KnownDisplayStore();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando ManualDisplayProbeService\n");
            var manualDisplayProbeService = new ManualDisplayProbeService();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando DisplayIdentityResolverService\n");
            var displayIdentityResolverService = new DisplayIdentityResolverService(knownDisplayStore, manualDisplayProbeService);
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando InMemoryCaptureSessionFactory\n");
            var captureSessionFactory = new InMemoryCaptureSessionFactory();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando DefaultDisplayTransportResolver\n");
            var transportResolver = new DefaultDisplayTransportResolver(transports);
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando RoutingService\n");
            var routingService = new RoutingService(captureSessionFactory, transportResolver);
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando ProfileStore\n");
            var profileStore = new ProfileStore();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando ActiveSessionStore\n");
            var activeSessionStore = new ActiveSessionStore();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando BrowserSnapshotService\n");
            var browserSnapshotService = new BrowserSnapshotService();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando BrowserAudioCaptureService\n");
            var browserAudioCaptureService = new BrowserAudioCaptureService();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando BrowserAudioHlsService\n");
            var browserAudioHlsService = new BrowserAudioHlsService(browserAudioCaptureService);
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando BrowserPanelInteractionHlsService\n");
            var browserPanelInteractionHlsService = new BrowserPanelInteractionHlsService(browserSnapshotService, browserAudioCaptureService);
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando BrowserPanelRollingHlsService\n");
            var browserPanelRollingHlsService = new BrowserPanelRollingHlsService(browserSnapshotService, browserAudioCaptureService);
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando AppUpdateManifestService\n");
            var appUpdateManifestService = new AppUpdateManifestService();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando AppUpdatePreferenceStore\n");
            var appUpdatePreferenceStore = new AppUpdatePreferenceStore();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando LocalWebRtcPublisherService\n");
            var webRtcPublisherService = new LocalWebRtcPublisherService(browserSnapshotService, browserAudioCaptureService, browserAudioHlsService, browserPanelInteractionHlsService, browserPanelRollingHlsService, appUpdatePreferenceStore);
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando StubDisplayDiscoveryService\n");
            var discoveryService = new StubDisplayDiscoveryService(knownDisplayStore, appUpdatePreferenceStore);
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando AppSelfUpdateService\n");
            var appSelfUpdateService = new AppSelfUpdateService();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando AppDataMaintenanceService\n");
            var appDataMaintenanceService = new AppDataMaintenanceService();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando AppInstallationSnapshotService\n");
            var appInstallationSnapshotService = new AppInstallationSnapshotService();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando UpdateRollbackStore\n");
            var updateRollbackStore = new UpdateRollbackStore();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando UpdateRecoveryService\n");
            var updateRecoveryService = new UpdateRecoveryService();
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Criando MainViewModel\n");
            var viewModel = new MainViewModel(browserHost, discoveryService, routingService, profileStore, activeSessionStore, manualDisplayProbeService, displayIdentityResolverService, webRtcPublisherService, knownDisplayStore, appUpdateManifestService, appUpdatePreferenceStore, appSelfUpdateService, appDataMaintenanceService, appInstallationSnapshotService, updateRollbackStore, updateRecoveryService);
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] Instanciando MainWindow\n");
            var mainWindow = new MainWindow(viewModel, browserSnapshotService, browserAudioCaptureService);
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] MainWindow instanciada\n");
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] CreateMainWindow END\n");
            return mainWindow;
        } catch (Exception ex) {
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [BOOTSTRAPPER] EXCEPTION: {ex}\n");
            throw;
        }
    }
}
