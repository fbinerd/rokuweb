using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WindowManager.App.Commands;
using WindowManager.App.Profiles;
using WindowManager.App.Runtime;
using WindowManager.App.Runtime.Discovery;
using WindowManager.App.Runtime.Publishing;
using WindowManager.Core.Abstractions;
using WindowManager.Core.Models;
using WindowManager.Core.Services;

namespace WindowManager.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private static readonly Guid LegacyTvSalaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LegacyDongleMiracastId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly IBrowserInstanceHost _browserInstanceHost;
    private readonly IDisplayDiscoveryService _displayDiscoveryService;
    private readonly RoutingService _routingService;
    private readonly ProfileStore _profileStore;
    private readonly ActiveSessionStore _activeSessionStore;
    private readonly ManualDisplayProbeService _manualDisplayProbeService;
    private readonly LocalWebRtcPublisherService _webRtcPublisherService;
    private readonly KnownDisplayStore _knownDisplayStore;
    private readonly AppUpdateManifestService _appUpdateManifestService;
    private readonly AppUpdatePreferenceStore _appUpdatePreferenceStore;
    private readonly AppSelfUpdateService _appSelfUpdateService;
    private readonly AppDataMaintenanceService _appDataMaintenanceService;

    private bool _isApplyingProfile;
    private bool _isRefreshingProfileNames;
    private WindowSession? _selectedWindow;
    private DisplayTarget? _selectedTarget;
    private ActiveSessionViewModel? _selectedActiveSession;
    private TvProfileViewModel? _selectedTvProfile;
    private WindowProfileViewModel? _selectedWindowProfile;
    private StaticDisplayPanelViewModel? _selectedStaticPanel;
    private string _profileName = "default";
    private string _browserUrlInput = "https://emei.lovable.app";
    private string _currentBrowserAddress = "https://emei.lovable.app";
    private int _webRtcServerPort = 8090;
    private WebRtcBindMode _webRtcBindMode = WebRtcBindMode.Lan;
    private string _webRtcSpecificIp = string.Empty;
    private bool _isDefaultProfile;
    private string _statusMessage = "Pronto para criar janelas e associar destinos.";
    private string _appVersionStatus = string.Format("Versao local: {0} ({1})", BuildVersionInfo.Version, BuildVersionInfo.ReleaseId);
    private string _updateStatusMessage = "Verificacao de atualizacao pendente.";
    private string _latestAvailableVersion = "Ainda nao consultado";
    private string _recommendedUpdatePackageUrl = string.Empty;
    private bool _isUpdateAvailable;
    private bool _isCheckingForUpdates;
    private bool _autoUpdateEnabled;
    private AppUpdateCheckResult? _lastUpdateCheckResult;
    private string _additionalDiscoveryCidrs = string.Empty;
    private string _selectedUpdateChannel = UpdateChannelNames.Stable;
    private string _selectedSessionProfileName = "default";
    private bool _isRestoringActiveSessions;
    private bool _suppressActiveSessionPersistence;

    public MainViewModel(
        IBrowserInstanceHost browserInstanceHost,
        IDisplayDiscoveryService displayDiscoveryService,
        RoutingService routingService,
        ProfileStore profileStore,
        ActiveSessionStore activeSessionStore,
        ManualDisplayProbeService manualDisplayProbeService,
        LocalWebRtcPublisherService webRtcPublisherService,
        KnownDisplayStore knownDisplayStore,
        AppUpdateManifestService appUpdateManifestService,
        AppUpdatePreferenceStore appUpdatePreferenceStore,
        AppSelfUpdateService appSelfUpdateService,
        AppDataMaintenanceService appDataMaintenanceService)
    {
        _browserInstanceHost = browserInstanceHost;
        _displayDiscoveryService = displayDiscoveryService;
        _routingService = routingService;
        _profileStore = profileStore;
        _activeSessionStore = activeSessionStore;
        _manualDisplayProbeService = manualDisplayProbeService;
        _webRtcPublisherService = webRtcPublisherService;
        _knownDisplayStore = knownDisplayStore;
        _appUpdateManifestService = appUpdateManifestService;
        _appUpdatePreferenceStore = appUpdatePreferenceStore;
        _appSelfUpdateService = appSelfUpdateService;
        _appDataMaintenanceService = appDataMaintenanceService;

        ResolutionModes = Enum.GetValues(typeof(RenderResolutionMode)).Cast<RenderResolutionMode>().ToArray();
        WebRtcBindModes = Enum.GetValues(typeof(WebRtcBindMode)).Cast<WebRtcBindMode>().ToArray();

        Windows.CollectionChanged += OnWindowsCollectionChanged;

        CreateWindowCommand = new AsyncRelayCommand(CreateWindowAsync);
        NavigateSelectedWindowCommand = new AsyncRelayCommand(NavigateSelectedWindowAsync, CanNavigateSelectedWindow);
        RefreshTargetsCommand = new AsyncRelayCommand(RefreshTargetsAsync);
        AssignSelectedWindowCommand = new AsyncRelayCommand(AssignSelectedWindowAsync, CanAssignSelectedWindow);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, CanUseProfileName);
        LoadProfileCommand = new AsyncRelayCommand(LoadProfileAsync, CanUseProfileName);
        DeleteProfileCommand = new AsyncRelayCommand(DeleteProfileAsync, CanUseProfileName);
        SetDefaultProfileCommand = new AsyncRelayCommand(SetDefaultProfileAsync, CanUseProfileName);
        DeleteSelectedWindowCommand = new AsyncRelayCommand(DeleteSelectedWindowAsync, CanDeleteSelectedWindow);
        PublishWebRtcCommand = new AsyncRelayCommand(PublishSelectedWindowWebRtcAsync, CanPublishSelectedWindowWebRtc);
        MarkSelectedTargetStaticCommand = new AsyncRelayCommand(MarkSelectedTargetStaticAsync, CanManageSelectedTarget);
        DeleteSelectedTargetCommand = new AsyncRelayCommand(DeleteSelectedTargetAsync, CanManageSelectedTarget);
        CreateStaticPanelCommand = new AsyncRelayCommand(CreateStaticPanelAsync, CanManageSelectedTarget);
        DeleteSelectedPanelCommand = new AsyncRelayCommand(DeleteSelectedPanelAsync, CanDeleteSelectedPanel);
        SearchUpdatesCommand = new AsyncRelayCommand(SearchUpdatesAsync, CanSearchUpdates);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, CanInstallUpdate);
        MigrateToRemoteBuildCommand = new AsyncRelayCommand(MigrateToRemoteBuildAsync, CanMigrateToRemoteBuild);
        UpdateConnectedTvsCommand = new AsyncRelayCommand(UpdateConnectedTvsAsync);
        UpdateSelectedTargetCommand = new AsyncRelayCommand(UpdateSelectedTargetAsync, CanUpdateSelectedTarget);
        PowerOnConnectedRokusCommand = new AsyncRelayCommand(PowerOnConnectedRokusAsync);
        PowerOffConnectedRokusCommand = new AsyncRelayCommand(PowerOffConnectedRokusAsync);
        CreateSessionFromProfileCommand = new AsyncRelayCommand(CreateSessionFromProfileAsync, CanCreateSessionFromProfile);
        BindSelectedTargetToSessionCommand = new AsyncRelayCommand(BindSelectedTargetToSessionAsync, CanBindSelectedTargetToSession);
        UnbindSelectedTargetFromSessionCommand = new AsyncRelayCommand(UnbindSelectedTargetFromSessionAsync, CanBindSelectedTargetToSession);
        RemoveSelectedSessionCommand = new AsyncRelayCommand(RemoveSelectedSessionAsync, CanRemoveSelectedSession);
        AddSelectedWindowToSessionCommand = new AsyncRelayCommand(AddSelectedWindowToSessionAsync, CanAddSelectedWindowToSession);
        RemoveSelectedWindowFromSessionCommand = new AsyncRelayCommand(RemoveSelectedWindowFromSessionAsync, CanRemoveSelectedWindowFromSession);
        CreateSessionFromWindowProfileCommand = new AsyncRelayCommand(CreateSessionFromWindowProfileAsync, CanCreateSessionFromWindowProfile);
        DeleteSelectedTvProfileCommand = new AsyncRelayCommand(DeleteSelectedTvProfileAsync, CanDeleteSelectedTvProfile);
        DeleteSelectedWindowProfileCommand = new AsyncRelayCommand(DeleteSelectedWindowProfileAsync, CanDeleteSelectedWindowProfile);

        UpdateBridgeSnapshot();
    }

    public ObservableCollection<WindowSession> Windows { get; } = new ObservableCollection<WindowSession>();

    public ObservableCollection<DisplayTarget> Targets { get; } = new ObservableCollection<DisplayTarget>();

    public ObservableCollection<string> AvailableProfiles { get; } = new ObservableCollection<string>();

    public ObservableCollection<ProfileDisplayBindingViewModel> ProfileDisplayBindings { get; } = new ObservableCollection<ProfileDisplayBindingViewModel>();

    public ObservableCollection<ActiveSessionViewModel> ActiveSessions { get; } = new ObservableCollection<ActiveSessionViewModel>();

    public ObservableCollection<TvProfileViewModel> TvProfiles { get; } = new ObservableCollection<TvProfileViewModel>();

    public ObservableCollection<WindowProfileViewModel> WindowProfiles { get; } = new ObservableCollection<WindowProfileViewModel>();

    public ObservableCollection<StaticDisplayPanelViewModel> StaticPanels { get; } = new ObservableCollection<StaticDisplayPanelViewModel>();

    public IReadOnlyList<RenderResolutionMode> ResolutionModes { get; }

    public IReadOnlyList<WebRtcBindMode> WebRtcBindModes { get; }

    public IReadOnlyList<string> UpdateChannels { get; } = new[] { UpdateChannelNames.Stable, UpdateChannelNames.Develop };

    public bool IsLocalDevelopmentBuild => string.Equals(BuildVersionInfo.CurrentBuildChannel, UpdateChannelNames.Local, StringComparison.OrdinalIgnoreCase);

    public bool IsAutomaticUpdateToggleEnabled => !IsLocalDevelopmentBuild;

    public AsyncRelayCommand CreateWindowCommand { get; }
    public AsyncRelayCommand NavigateSelectedWindowCommand { get; }
    public AsyncRelayCommand RefreshTargetsCommand { get; }
    public AsyncRelayCommand AssignSelectedWindowCommand { get; }
    public AsyncRelayCommand SaveProfileCommand { get; }
    public AsyncRelayCommand LoadProfileCommand { get; }
    public AsyncRelayCommand DeleteProfileCommand { get; }
    public AsyncRelayCommand SetDefaultProfileCommand { get; }
    public AsyncRelayCommand DeleteSelectedWindowCommand { get; }
    public AsyncRelayCommand PublishWebRtcCommand { get; }
    public AsyncRelayCommand MarkSelectedTargetStaticCommand { get; }
    public AsyncRelayCommand DeleteSelectedTargetCommand { get; }
    public AsyncRelayCommand CreateStaticPanelCommand { get; }
    public AsyncRelayCommand DeleteSelectedPanelCommand { get; }
    public AsyncRelayCommand SearchUpdatesCommand { get; }
    public AsyncRelayCommand InstallUpdateCommand { get; }
    public AsyncRelayCommand MigrateToRemoteBuildCommand { get; }
    public AsyncRelayCommand UpdateConnectedTvsCommand { get; }
    public AsyncRelayCommand UpdateSelectedTargetCommand { get; }
    public AsyncRelayCommand PowerOnConnectedRokusCommand { get; }
    public AsyncRelayCommand PowerOffConnectedRokusCommand { get; }
    public AsyncRelayCommand CreateSessionFromProfileCommand { get; }
    public AsyncRelayCommand BindSelectedTargetToSessionCommand { get; }
    public AsyncRelayCommand UnbindSelectedTargetFromSessionCommand { get; }
    public AsyncRelayCommand RemoveSelectedSessionCommand { get; }
    public AsyncRelayCommand AddSelectedWindowToSessionCommand { get; }
    public AsyncRelayCommand RemoveSelectedWindowFromSessionCommand { get; }
    public AsyncRelayCommand CreateSessionFromWindowProfileCommand { get; }
    public AsyncRelayCommand DeleteSelectedTvProfileCommand { get; }
    public AsyncRelayCommand DeleteSelectedWindowProfileCommand { get; }

    public WindowSession? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (SetProperty(ref _selectedWindow, value))
            {
                BrowserUrlInput = value?.InitialUri?.ToString() ?? BrowserUrlInput;
                CurrentBrowserAddress = value?.InitialUri?.ToString() ?? "about:blank";
                RaisePropertyChanged(nameof(PreviewTitle));
                RaisePropertyChanged(nameof(PreviewSubtitle));
                NavigateSelectedWindowCommand.RaiseCanExecuteChanged();
                AssignSelectedWindowCommand.RaiseCanExecuteChanged();
                SaveProfileCommand.RaiseCanExecuteChanged();
                DeleteSelectedWindowCommand.RaiseCanExecuteChanged();
                PublishWebRtcCommand.RaiseCanExecuteChanged();
                AddSelectedWindowToSessionCommand.RaiseCanExecuteChanged();
                RemoveSelectedWindowFromSessionCommand.RaiseCanExecuteChanged();
                QueueAutoSave();
            }
        }
    }

    public DisplayTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                RaisePropertyChanged(nameof(PreviewSubtitle));
                RaisePropertyChanged(nameof(SelectedTargetNativeResolution));
                AssignSelectedWindowCommand.RaiseCanExecuteChanged();
                MarkSelectedTargetStaticCommand.RaiseCanExecuteChanged();
                DeleteSelectedTargetCommand.RaiseCanExecuteChanged();
                CreateStaticPanelCommand.RaiseCanExecuteChanged();
                UpdateSelectedTargetCommand.RaiseCanExecuteChanged();
                QueueAutoSave();
            }
        }
    }

    public StaticDisplayPanelViewModel? SelectedStaticPanel
    {
        get => _selectedStaticPanel;
        set
        {
            if (SetProperty(ref _selectedStaticPanel, value))
            {
                DeleteSelectedPanelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ActiveSessionViewModel? SelectedActiveSession
    {
        get => _selectedActiveSession;
        set
        {
            if (SetProperty(ref _selectedActiveSession, value))
            {
                BindSelectedTargetToSessionCommand.RaiseCanExecuteChanged();
                UnbindSelectedTargetFromSessionCommand.RaiseCanExecuteChanged();
                RemoveSelectedSessionCommand.RaiseCanExecuteChanged();
                AddSelectedWindowToSessionCommand.RaiseCanExecuteChanged();
                RemoveSelectedWindowFromSessionCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(SelectedActiveSessionSummary));
            }
        }
    }

    public TvProfileViewModel? SelectedTvProfile
    {
        get => _selectedTvProfile;
        set
        {
            if (SetProperty(ref _selectedTvProfile, value))
            {
                DeleteSelectedTvProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public WindowProfileViewModel? SelectedWindowProfile
    {
        get => _selectedWindowProfile;
        set
        {
            if (SetProperty(ref _selectedWindowProfile, value))
            {
                CreateSessionFromWindowProfileCommand.RaiseCanExecuteChanged();
                DeleteSelectedWindowProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ProfileName
    {
        get => _profileName;
        set
        {
            if (_isRefreshingProfileNames && string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (SetProperty(ref _profileName, value))
            {
                if (string.IsNullOrWhiteSpace(SelectedSessionProfileName))
                {
                    SelectedSessionProfileName = value;
                }
                SaveProfileCommand.RaiseCanExecuteChanged();
                SetDefaultProfileCommand.RaiseCanExecuteChanged();
                LoadProfileCommand.RaiseCanExecuteChanged();
                DeleteProfileCommand.RaiseCanExecuteChanged();
                _ = SyncDefaultProfileSelectionAsync();
            }
        }
    }

    public string SelectedSessionProfileName
    {
        get => _selectedSessionProfileName;
        set
        {
            if (SetProperty(ref _selectedSessionProfileName, value?.Trim() ?? string.Empty))
            {
                CreateSessionFromProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDefaultProfile
    {
        get => _isDefaultProfile;
        private set => SetProperty(ref _isDefaultProfile, value);
    }

    public string BrowserUrlInput
    {
        get => _browserUrlInput;
        set
        {
            if (SetProperty(ref _browserUrlInput, value))
            {
                NavigateSelectedWindowCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CurrentBrowserAddress
    {
        get => _currentBrowserAddress;
        private set => SetProperty(ref _currentBrowserAddress, value);
    }

    public int WebRtcServerPort
    {
        get => _webRtcServerPort;
        set
        {
            if (SetProperty(ref _webRtcServerPort, value <= 0 ? 8090 : value))
            {
                PublishWebRtcCommand.RaiseCanExecuteChanged();
                QueueAutoSave();
                RepublishEnabledWindows();
                UpdateBridgeSnapshot();
            }
        }
    }

    public WebRtcBindMode WebRtcBindMode
    {
        get => _webRtcBindMode;
        set
        {
            if (SetProperty(ref _webRtcBindMode, value))
            {
                QueueAutoSave();
                RepublishEnabledWindows();
                UpdateBridgeSnapshot();
            }
        }
    }

    public string WebRtcSpecificIp
    {
        get => _webRtcSpecificIp;
        set
        {
            if (SetProperty(ref _webRtcSpecificIp, value?.Trim() ?? string.Empty))
            {
                QueueAutoSave();
                if (WebRtcBindMode == WebRtcBindMode.SpecificIp)
                {
                    RepublishEnabledWindows();
                }
                UpdateBridgeSnapshot();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string AppVersionStatus
    {
        get => _appVersionStatus;
        private set => SetProperty(ref _appVersionStatus, value);
    }

    public string UpdateStatusMessage
    {
        get => _updateStatusMessage;
        private set => SetProperty(ref _updateStatusMessage, value);
    }

    public string LatestAvailableVersion
    {
        get => _latestAvailableVersion;
        private set => SetProperty(ref _latestAvailableVersion, value);
    }

    public string RecommendedUpdatePackageUrl
    {
        get => _recommendedUpdatePackageUrl;
        private set => SetProperty(ref _recommendedUpdatePackageUrl, value);
    }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set => SetProperty(ref _isUpdateAvailable, value);
    }

    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        private set
        {
            if (SetProperty(ref _isCheckingForUpdates, value))
            {
                SearchUpdatesCommand.RaiseCanExecuteChanged();
                InstallUpdateCommand.RaiseCanExecuteChanged();
                MigrateToRemoteBuildCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool AutoUpdateEnabled
    {
        get => _autoUpdateEnabled;
        set
        {
            if (IsLocalDevelopmentBuild)
            {
                if (SetProperty(ref _autoUpdateEnabled, false))
                {
                    _ = PersistUpdatePreferencesAsync();
                }

                return;
            }

            if (SetProperty(ref _autoUpdateEnabled, value))
            {
                _ = PersistUpdatePreferencesAsync();
            }
        }
    }

    public string SelectedUpdateChannel
    {
        get => _selectedUpdateChannel;
        set
        {
            var normalized = UpdateChannelNames.Normalize(value);
            if (SetProperty(ref _selectedUpdateChannel, normalized))
            {
                _ = PersistUpdatePreferencesAsync();
            }
        }
    }

    public string AdditionalDiscoveryCidrs
    {
        get => _additionalDiscoveryCidrs;
        set
        {
            if (SetProperty(ref _additionalDiscoveryCidrs, value?.Trim() ?? string.Empty))
            {
                _ = PersistUpdatePreferencesAsync();
            }
        }
    }

    public string PreviewTitle =>
        SelectedWindow is null ? "Nenhuma janela selecionada" : SelectedWindow.Title;

    public string PreviewSubtitle
    {
        get
        {
            if (SelectedWindow is null)
            {
                return "Crie uma janela para iniciar o fluxo do gerenciador.";
            }

            if (SelectedTarget is null)
            {
                return "Selecione um destino para transmitir a janela escolhida.";
            }

            return string.Format("Destino atual: {0} ({1}).", SelectedTarget.Name, SelectedTarget.TransportKind);
        }
    }

    public string SelectedTargetNativeResolution =>
        SelectedTarget is null
            ? "Nenhuma TV selecionada"
            : string.Format("Resolucao detectada da TV: {0}x{1}", SelectedTarget.NativeWidth, SelectedTarget.NativeHeight);

    public string SelectedActiveSessionSummary =>
        SelectedActiveSession is null
            ? "Nenhuma sessao ativa selecionada"
            : string.Format(
                "Sessao '{0}' | Perfil: {1} | Janelas: {2} | TVs: {3} | Bindings: {4}",
                SelectedActiveSession.Name,
                SelectedActiveSession.ProfileName,
                SelectedActiveSession.WindowCount,
                SelectedActiveSession.BoundDisplays.Count,
                SelectedActiveSession.BindingCount);

    public string? GetWindowLinkRtcUrl(WindowSession? window)
    {
        if (window is null || !window.IsWebRtcPublishingEnabled)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(window.PublishedWebRtcUrl)
            ? LinkRtcAddressBuilder.BuildPublishedUrl(window, WebRtcServerPort, WebRtcBindMode, WebRtcSpecificIp)
            : window.PublishedWebRtcUrl;
    }

    public async Task OpenWindowLinkRtcAsync(WindowSession window)
    {
        if (window is null)
        {
            return;
        }

        if (!window.IsWebRtcPublishingEnabled)
        {
            window.IsWebRtcPublishingEnabled = true;
        }

        await PublishWindowWebRtcAsync(window, false);

        if (string.IsNullOrWhiteSpace(window.PublishedWebRtcUrl))
        {
            StatusMessage = string.Format("Nao foi possivel publicar o LinkRTC da janela '{0}'.", window.Title);
            return;
        }

        var url = LinkRtcAddressBuilder.BuildLoopbackUrl(window, WebRtcServerPort);

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });

        StatusMessage = string.Format("Abrindo LinkRTC da janela '{0}' em {1}.", window.Title, url);
    }
    public void ReportStartupFailure(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusMessage = message;
        }
    }
    public async Task ApplyWindowSettingsAsync(WindowSession window, string nickname, DisplayTarget? target, bool isWebRtcEnabled)
    {
        if (window is null)
        {
            return;
        }

        var sanitizedNickname = string.IsNullOrWhiteSpace(nickname) ? window.Title : nickname.Trim();

        window.Title = sanitizedNickname;
        window.AssignedTarget = target;
        window.IsWebRtcPublishingEnabled = isWebRtcEnabled;

        if (target is not null)
        {
            SelectedTarget = target;
        }

        await PublishWindowWebRtcAsync(window, true);
        await SaveProfileInternalAsync(updateStatus: false);

        StatusMessage = string.Format("Configuracoes aplicadas para '{0}'.", sanitizedNickname);
    }

    public async Task InitializeAfterStartupAsync()
    {
        await LoadPreferencesAsync();
        await ReloadPersistedStateAsync();
        _ = CheckForAppUpdatesAsync();
    }

    public async Task ExportApplicationDataAsync(string destinationZipPath)
    {
        await _appDataMaintenanceService.ExportAsync(destinationZipPath, CancellationToken.None);
        StatusMessage = string.Format("Backup salvo em '{0}'.", destinationZipPath);
    }

    public async Task ImportApplicationDataAsync(string sourceZipPath)
    {
        await ResetRuntimeStateAsync();
        await _appDataMaintenanceService.ImportAsync(sourceZipPath, CancellationToken.None);
        await LoadPreferencesAsync();
        await ReloadPersistedStateAsync();
        StatusMessage = string.Format("Backup restaurado de '{0}'.", sourceZipPath);
    }

    public async Task ResetApplicationDataAsync()
    {
        await ResetRuntimeStateAsync();
        await _appDataMaintenanceService.ResetAsync(CancellationToken.None);
        await LoadPreferencesAsync();
        await ReloadPersistedStateAsync();
        StatusMessage = "Base local do aplicativo resetada com sucesso.";
    }

    private async Task LoadPreferencesAsync()
    {
        var preferences = await _appUpdatePreferenceStore.LoadAsync(CancellationToken.None);
        AutoUpdateEnabled = IsLocalDevelopmentBuild ? false : preferences.AutoUpdateEnabled;
        SelectedUpdateChannel = preferences.UpdateChannel;
        AdditionalDiscoveryCidrs = preferences.AdditionalDiscoveryCidrs;
    }

    private async Task ReloadPersistedStateAsync()
    {
        await RefreshTargetsAsync();
        await RefreshProfileNamesAsync();

        var startupProfileName = await _profileStore.GetStartupProfileNameAsync(CancellationToken.None);
        ProfileName = startupProfileName;
        await LoadProfileAsync();
        SelectedSessionProfileName = ProfileName;
        await RestoreActiveSessionsAsync();
    }

    private async Task CheckForAppUpdatesAsync()
    {
        try
        {
            IsCheckingForUpdates = true;
            UpdateStatusMessage = IsLocalDevelopmentBuild
                ? "Build local detectado. Auto-update automatico desabilitado."
                : "Consultando atualizacoes do super...";
            var result = await RefreshUpdateInfoAsync();

            AppLog.Write(
                "Updater",
                string.Format(
                    "Manifesto consultado em {0}. UpdateAvailable={1}. Atual={2}. Remoto={3}.",
                    result.ManifestUrl,
                    result.UpdateAvailable,
                    result.CurrentReleaseId,
                    result.LatestReleaseId));

            if (AutoUpdateEnabled && result.UpdateAvailable)
            {
                await DownloadAndApplyUpdateAsync(result, automatic: true);
            }
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = string.Format("Falha ao consultar atualizacoes: {0}", ex.Message);
            AppLog.Write("Updater", UpdateStatusMessage);
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private bool CanSearchUpdates() => !IsCheckingForUpdates;

    private bool CanInstallUpdate() => !IsCheckingForUpdates && IsUpdateAvailable && _lastUpdateCheckResult is not null;
    private bool CanMigrateToRemoteBuild() => !IsCheckingForUpdates && IsLocalDevelopmentBuild;
    private bool CanUpdateSelectedTarget() => SelectedTarget is not null && !string.IsNullOrWhiteSpace(SelectedTarget.NetworkAddress);
    private bool CanCreateSessionFromProfile() => !string.IsNullOrWhiteSpace(SelectedSessionProfileName);
    private bool CanCreateSessionFromWindowProfile() => SelectedWindowProfile is not null;
    private bool CanBindSelectedTargetToSession() => SelectedTarget is not null && SelectedActiveSession is not null;
    private bool CanRemoveSelectedSession() => SelectedActiveSession is not null;
    private bool CanAddSelectedWindowToSession() => SelectedWindow is not null && SelectedActiveSession is not null;
    private bool CanRemoveSelectedWindowFromSession() => SelectedWindow is not null && SelectedActiveSession is not null;
    private bool CanDeleteSelectedTvProfile() => SelectedTvProfile is not null;
    private bool CanDeleteSelectedWindowProfile() => SelectedWindowProfile is not null;

    private async Task SearchUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        try
        {
            UpdateStatusMessage = "Buscando atualizacao mais recente do super...";
            await RefreshUpdateInfoAsync();
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private async Task InstallUpdateAsync()
    {
        IsCheckingForUpdates = true;
        try
        {
            if (_lastUpdateCheckResult is null || !_lastUpdateCheckResult.UpdateAvailable)
            {
                UpdateStatusMessage = "Nenhuma atualizacao pendente para instalar.";
                return;
            }

            await DownloadAndApplyUpdateAsync(_lastUpdateCheckResult, automatic: false);
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private async Task MigrateToRemoteBuildAsync()
    {
        IsCheckingForUpdates = true;
        try
        {
            UpdateStatusMessage = string.Format("Build local detectado. Buscando release remota do canal {0}...", SelectedUpdateChannel);
            var result = await RefreshUpdateInfoAsync(ignoreLocalBuildRestriction: true);
            if (!result.UpdateAvailable)
            {
                UpdateStatusMessage = string.IsNullOrWhiteSpace(result.StatusMessage)
                    ? "Nenhuma release remota mais recente foi encontrada para migracao."
                    : result.StatusMessage;
                return;
            }

            await DownloadAndApplyUpdateAsync(result, automatic: false, customActionLabel: "Migracao para build remota");
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private async Task UpdateConnectedTvsAsync()
    {
        UpdateStatusMessage = "Verificando TVs conectadas para sideload...";
        AppLog.Write("RokuDeploy", "Disparo manual solicitado para atualizar TVs conectadas.");

        var updatedCount = await _webRtcPublisherService.ForceUpdateConnectedDisplaysAsync(CancellationToken.None);
        UpdateStatusMessage = updatedCount <= 0
            ? "Nenhuma TV conectada precisava de atualizacao."
            : string.Format("{0} TV(s) receberam sideload de atualizacao.", updatedCount);
    }

    private async Task PowerOnConnectedRokusAsync()
    {
        UpdateStatusMessage = "Enviando comando para ligar TVs Roku compativeis...";
        UpdateStatusMessage = await SendPowerCommandToKnownRokusAsync(powerOn: true);
    }

    private async Task PowerOffConnectedRokusAsync()
    {
        UpdateStatusMessage = "Enviando comando para desligar TVs Roku compativeis...";
        UpdateStatusMessage = await SendPowerCommandToKnownRokusAsync(powerOn: false);
    }

    public async Task ApplyTvProfileSetupAsync(TvProfileSetupViewModel setup)
    {
        var name = setup.ProfileName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Informe um nome para o perfil de TV.";
            return;
        }

        if (setup.IncludedTargets.Count == 0)
        {
            StatusMessage = "Adicione pelo menos uma TV ao perfil.";
            return;
        }

        var tvProfile = TvProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (tvProfile is null)
        {
            tvProfile = new TvProfileViewModel
            {
                Id = Guid.NewGuid()
            };
            TvProfiles.Add(tvProfile);
        }

        tvProfile.Name = name;
        tvProfile.Targets.Clear();

        foreach (var entry in setup.IncludedTargets)
        {
            var target = await EnsureTargetForTvProfileEntryAsync(entry);
            tvProfile.Targets.Add(new TvProfileTargetViewModel
            {
                DisplayTargetId = target.Id,
                DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? target.Name : entry.DisplayName,
                NetworkAddress = target.NetworkAddress,
                DeviceUniqueId = target.DeviceUniqueId,
                MacAddress = target.MacAddress,
                DiscoverySource = target.DiscoverySource,
                NativeWidth = target.NativeWidth,
                NativeHeight = target.NativeHeight
            });
        }

        SelectedTvProfile = tvProfile;
        await PersistKnownTargetsAsync();
        await SaveProfileInternalAsync(updateStatus: false);
        StatusMessage = string.Format("Perfil de TV '{0}' salvo com {1} TVs.", tvProfile.Name, tvProfile.Targets.Count);
    }

    public async Task ApplyWindowProfileSetupAsync(WindowProfileSetupViewModel setup)
    {
        var name = setup.ProfileName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Informe um nome para o perfil de janelas.";
            return;
        }

        if (setup.Windows.Count == 0)
        {
            StatusMessage = "Adicione pelo menos uma janela ao perfil.";
            return;
        }

        if (setup.SelectedTvProfile is null)
        {
            StatusMessage = "Selecione um perfil de TV para concluir.";
            return;
        }

        var windowProfile = WindowProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (windowProfile is null)
        {
            windowProfile = new WindowProfileViewModel
            {
                Id = Guid.NewGuid()
            };
            WindowProfiles.Add(windowProfile);
        }

        windowProfile.Name = name;
        windowProfile.AssignedTvProfileId = setup.SelectedTvProfile.Id;
        windowProfile.AssignedTvProfileName = setup.SelectedTvProfile.Name;
        windowProfile.Windows.Clear();
        foreach (var window in setup.Windows)
        {
            windowProfile.Windows.Add(new WindowProfileItemViewModel
            {
                Id = window.Id == Guid.Empty ? Guid.NewGuid() : window.Id,
                Nickname = window.Nickname,
                Url = window.Url
            });
        }

        SelectedWindowProfile = windowProfile;
        await SaveProfileInternalAsync(updateStatus: false);
        StatusMessage = string.Format(
            "Perfil de janelas '{0}' salvo e vinculado ao perfil de TV '{1}'.",
            windowProfile.Name,
            windowProfile.AssignedTvProfileName);
    }

    public async Task DeleteSelectedTvProfileAsync()
    {
        if (SelectedTvProfile is null)
        {
            return;
        }

        var removedName = SelectedTvProfile.Name;
        var removedId = SelectedTvProfile.Id;
        TvProfiles.Remove(SelectedTvProfile);
        SelectedTvProfile = TvProfiles.FirstOrDefault();

        foreach (var windowProfile in WindowProfiles.Where(x => x.AssignedTvProfileId == removedId))
        {
            windowProfile.AssignedTvProfileId = null;
            windowProfile.AssignedTvProfileName = string.Empty;
        }

        await SaveProfileInternalAsync(updateStatus: false);
        StatusMessage = string.Format("Perfil de TV '{0}' removido.", removedName);
    }

    public async Task DeleteSelectedWindowProfileAsync()
    {
        if (SelectedWindowProfile is null)
        {
            return;
        }

        var removedName = SelectedWindowProfile.Name;
        WindowProfiles.Remove(SelectedWindowProfile);
        SelectedWindowProfile = WindowProfiles.FirstOrDefault();
        await SaveProfileInternalAsync(updateStatus: false);
        StatusMessage = string.Format("Perfil de janelas '{0}' removido.", removedName);
    }

    private async Task CreateSessionFromWindowProfileAsync()
    {
        if (SelectedWindowProfile is null)
        {
            StatusMessage = "Selecione um perfil de janelas.";
            return;
        }

        var windowProfile = SelectedWindowProfile;
        var tvProfile = TvProfiles.FirstOrDefault(x => x.Id == windowProfile.AssignedTvProfileId);
        if (tvProfile is null)
        {
            StatusMessage = string.Format(
                "O perfil de janelas '{0}' nao possui um perfil de TV valido vinculado.",
                windowProfile.Name);
            return;
        }

        var sessionId = Guid.NewGuid();
        var sessionName = BuildSessionName(windowProfile.Name);

        foreach (var windowDefinition in windowProfile.Windows)
        {
            Uri? initialUri = null;
            if (!string.IsNullOrWhiteSpace(windowDefinition.Url))
            {
                Uri.TryCreate(windowDefinition.Url, UriKind.Absolute, out initialUri);
            }

            var browserWindow = await _browserInstanceHost.CreateAsync(initialUri ?? new Uri("about:blank"), CancellationToken.None);
            browserWindow.Title = string.IsNullOrWhiteSpace(windowDefinition.Nickname) ? browserWindow.Title : windowDefinition.Nickname;
            browserWindow.InitialUri = initialUri;
            browserWindow.State = WindowSessionState.Created;
            browserWindow.ProfileName = windowProfile.Name;
            browserWindow.ActiveSessionId = sessionId;
            browserWindow.ActiveSessionName = sessionName;
            browserWindow.AssignedTarget = null;
            Windows.Add(browserWindow);
        }

        RebuildActiveSessionsFromWindows();
        SelectedActiveSession = ActiveSessions.FirstOrDefault(x => x.Id == sessionId);
        if (SelectedActiveSession is not null)
        {
            foreach (var binding in tvProfile.Targets)
            {
                if (!SelectedActiveSession.BoundDisplays.Any(x => x.DisplayTargetId == binding.DisplayTargetId))
                {
                    SelectedActiveSession.BoundDisplays.Add(new ActiveSessionDisplayBindingViewModel
                    {
                        DisplayTargetId = binding.DisplayTargetId,
                        DisplayName = binding.DisplayName,
                        NetworkAddress = binding.NetworkAddress,
                        DeviceUniqueId = binding.DeviceUniqueId,
                        BindingName = tvProfile.Name
                    });
                }
            }

            SelectedActiveSession.TvCount = SelectedActiveSession.BoundDisplays.Count;
            SelectedActiveSession.BindingCount = SelectedActiveSession.BoundDisplays.Count;
        }

        StatusMessage = string.Format(
            "Sessao '{0}' criada a partir do perfil de janelas '{1}' com o perfil de TV '{2}'.",
            sessionName,
            windowProfile.Name,
            tvProfile.Name);
        UpdateBridgeSnapshot();
        await PersistActiveSessionsAsync();
    }

    private async Task CreateSessionFromProfileAsync()
    {
        var profileName = SelectedSessionProfileName?.Trim();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            StatusMessage = "Selecione um perfil para criar a sessao.";
            return;
        }

        var profile = await _profileStore.LoadAsync(profileName!, CancellationToken.None);
        if (profile is null)
        {
            StatusMessage = string.Format("Perfil '{0}' nao foi encontrado.", profileName);
            return;
        }

        var sessionId = Guid.NewGuid();
        var sessionName = BuildSessionName(profile.Name);

        foreach (var persistedWindow in profile.Windows)
        {
            Uri? initialUri = null;
            if (!string.IsNullOrWhiteSpace(persistedWindow.InitialUrl))
            {
                Uri.TryCreate(persistedWindow.InitialUrl, UriKind.Absolute, out initialUri);
            }

            var browserWindow = await _browserInstanceHost.CreateAsync(initialUri ?? new Uri("about:blank"), CancellationToken.None);
            browserWindow.Title = string.IsNullOrWhiteSpace(persistedWindow.Title) ? browserWindow.Title : persistedWindow.Title;
            browserWindow.InitialUri = initialUri;
            browserWindow.State = persistedWindow.State;
            browserWindow.BrowserResolutionMode = persistedWindow.BrowserResolutionMode;
            browserWindow.BrowserManualWidth = persistedWindow.BrowserManualWidth;
            browserWindow.BrowserManualHeight = persistedWindow.BrowserManualHeight;
            browserWindow.TargetResolutionMode = persistedWindow.TargetResolutionMode;
            browserWindow.TargetManualWidth = persistedWindow.TargetManualWidth;
            browserWindow.TargetManualHeight = persistedWindow.TargetManualHeight;
            browserWindow.IsWebRtcPublishingEnabled = persistedWindow.IsWebRtcPublishingEnabled;
            browserWindow.ProfileName = profile.Name;
            browserWindow.ActiveSessionId = sessionId;
            browserWindow.ActiveSessionName = sessionName;
            browserWindow.AssignedTarget = null;

            Windows.Add(browserWindow);
        }

        RebuildActiveSessionsFromWindows();
        SelectedActiveSession = ActiveSessions.FirstOrDefault(x => x.Id == sessionId);
        if (SelectedActiveSession is not null)
        {
            foreach (var binding in profile.DisplayBindings)
            {
                if (!SelectedActiveSession.BoundDisplays.Any(x => x.DisplayTargetId == binding.DisplayTargetId))
                {
                    SelectedActiveSession.BoundDisplays.Add(new ActiveSessionDisplayBindingViewModel
                    {
                        DisplayTargetId = binding.DisplayTargetId,
                        DisplayName = string.IsNullOrWhiteSpace(binding.Name) ? binding.DisplayTargetName : binding.Name,
                        NetworkAddress = binding.NetworkAddress,
                        DeviceUniqueId = binding.DeviceUniqueId,
                        BindingName = string.IsNullOrWhiteSpace(binding.Name) ? binding.DisplayTargetName : binding.Name
                    });
                }
            }

            SelectedActiveSession.TvCount = SelectedActiveSession.BoundDisplays.Count;
            SelectedActiveSession.BindingCount = SelectedActiveSession.BoundDisplays.Count;
        }

        StatusMessage = string.Format("Sessao '{0}' criada a partir do perfil '{1}'.", sessionName, profile.Name);
        UpdateBridgeSnapshot();
        await PersistActiveSessionsAsync();
    }

    private async Task BindSelectedTargetToSessionAsync()
    {
        if (SelectedTarget is null || SelectedActiveSession is null)
        {
            return;
        }

        foreach (var session in ActiveSessions.Where(x => x.Id != SelectedActiveSession.Id))
        {
            var existing = session.BoundDisplays.FirstOrDefault(x => x.DisplayTargetId == SelectedTarget.Id);
            if (existing is not null)
            {
                session.BoundDisplays.Remove(existing);
                session.TvCount = session.BoundDisplays.Count;
            }
        }

        if (!SelectedActiveSession.BoundDisplays.Any(x => x.DisplayTargetId == SelectedTarget.Id))
        {
            SelectedActiveSession.BoundDisplays.Add(new ActiveSessionDisplayBindingViewModel
            {
                DisplayTargetId = SelectedTarget.Id,
                DisplayName = SelectedTarget.Name,
                NetworkAddress = SelectedTarget.NetworkAddress,
                DeviceUniqueId = SelectedTarget.DeviceUniqueId,
                BindingName = SelectedTarget.Name
            });
            SelectedActiveSession.TvCount = SelectedActiveSession.BoundDisplays.Count;
            SelectedActiveSession.BindingCount = SelectedActiveSession.BoundDisplays.Count;
        }

        StatusMessage = string.Format("TV '{0}' vinculada a sessao '{1}'.", SelectedTarget.Name, SelectedActiveSession.Name);
        UpdateBridgeSnapshot();
        await PersistActiveSessionsAsync();
    }

    private async Task AddSelectedWindowToSessionAsync()
    {
        if (SelectedWindow is null || SelectedActiveSession is null)
        {
            return;
        }

        SelectedWindow.ActiveSessionId = SelectedActiveSession.Id;
        SelectedWindow.ActiveSessionName = SelectedActiveSession.Name;
        SelectedWindow.ProfileName = SelectedActiveSession.ProfileName;
        if (!SelectedActiveSession.WindowIds.Contains(SelectedWindow.Id))
        {
            foreach (var session in ActiveSessions.Where(x => x.Id != SelectedActiveSession.Id))
            {
                session.WindowIds.Remove(SelectedWindow.Id);
                session.WindowCount = session.WindowIds.Count;
            }

            SelectedActiveSession.WindowIds.Add(SelectedWindow.Id);
            SelectedActiveSession.WindowCount = SelectedActiveSession.WindowIds.Count;
        }

        RebuildActiveSessionsFromWindows();
        StatusMessage = string.Format("Janela '{0}' adicionada a sessao '{1}'.", SelectedWindow.Title, SelectedActiveSession.Name);
        UpdateBridgeSnapshot();
        await PersistActiveSessionsAsync();
    }

    private async Task RemoveSelectedWindowFromSessionAsync()
    {
        if (SelectedWindow is null || SelectedActiveSession is null)
        {
            return;
        }

        if (SelectedWindow.ActiveSessionId == SelectedActiveSession.Id)
        {
            SelectedWindow.ActiveSessionId = Guid.Empty;
            SelectedWindow.ActiveSessionName = string.Empty;
        }

        SelectedActiveSession.WindowIds.Remove(SelectedWindow.Id);
        SelectedActiveSession.WindowCount = SelectedActiveSession.WindowIds.Count;
        RebuildActiveSessionsFromWindows();
        StatusMessage = string.Format("Janela '{0}' removida da sessao '{1}'.", SelectedWindow.Title, SelectedActiveSession.Name);
        UpdateBridgeSnapshot();
        await PersistActiveSessionsAsync();
    }

    private async Task UnbindSelectedTargetFromSessionAsync()
    {
        if (SelectedTarget is null || SelectedActiveSession is null)
        {
            return;
        }

        var binding = SelectedActiveSession.BoundDisplays.FirstOrDefault(x => x.DisplayTargetId == SelectedTarget.Id);
        if (binding is not null)
        {
            SelectedActiveSession.BoundDisplays.Remove(binding);
            SelectedActiveSession.TvCount = SelectedActiveSession.BoundDisplays.Count;
            SelectedActiveSession.BindingCount = SelectedActiveSession.BoundDisplays.Count;
            StatusMessage = string.Format("TV '{0}' removida da sessao '{1}'.", SelectedTarget.Name, SelectedActiveSession.Name);
            UpdateBridgeSnapshot();
            await PersistActiveSessionsAsync();
        }
    }

    private async Task RemoveSelectedSessionAsync()
    {
        if (SelectedActiveSession is null)
        {
            return;
        }

        var session = SelectedActiveSession;
        var sessionWindowIds = session.WindowIds.ToList();
        var windowsToRemove = Windows.Where(x => sessionWindowIds.Contains(x.Id)).ToList();
        foreach (var window in windowsToRemove)
        {
            try
            {
                await _browserInstanceHost.CloseAsync(window.Id, CancellationToken.None);
            }
            catch
            {
            }

            Windows.Remove(window);
        }

        ActiveSessions.Remove(session);
        SelectedActiveSession = ActiveSessions.FirstOrDefault();
        StatusMessage = string.Format("Sessao '{0}' removida.", session.Name);
        UpdateBridgeSnapshot();
        await PersistActiveSessionsAsync();
    }

    private async Task UpdateSelectedTargetAsync()
    {
        if (SelectedTarget is null)
        {
            StatusMessage = "Selecione uma TV descoberta primeiro.";
            return;
        }

        StatusMessage = string.Format("Enviando atualizacao para a TV '{0}'...", SelectedTarget.Name);
        var result = await _webRtcPublisherService.ForceUpdateDisplayTargetAsync(SelectedTarget, CancellationToken.None);
        StatusMessage = string.Format("Resultado da atualizacao da TV '{0}': {1}", SelectedTarget.Name, result);
        AppLog.Write(
            "RokuDeploy",
            string.Format(
                "Resultado da atualizacao direta da TV descoberta '{0}' ({1}): {2}",
                SelectedTarget.Name,
                SelectedTarget.NetworkAddress,
                result));
    }

    private async Task<AppUpdateCheckResult> RefreshUpdateInfoAsync(bool ignoreLocalBuildRestriction = false)
    {
        if (IsLocalDevelopmentBuild && !ignoreLocalBuildRestriction)
        {
            AppVersionStatus = string.Format("Versao local: {0} ({1})", BuildVersionInfo.Version, BuildVersionInfo.ReleaseId);
            LatestAvailableVersion = "Build local";
            IsUpdateAvailable = false;
            RecommendedUpdatePackageUrl = string.Empty;
            UpdateStatusMessage = "Build local detectado. Use o script local para compilar e fazer sideload.";
            _lastUpdateCheckResult = AppUpdateCheckResult.Failure(string.Empty, UpdateStatusMessage);
            InstallUpdateCommand.RaiseCanExecuteChanged();
            MigrateToRemoteBuildCommand.RaiseCanExecuteChanged();
            return _lastUpdateCheckResult;
        }

        var result = await _appUpdateManifestService.CheckForUpdateAsync(SelectedUpdateChannel, CancellationToken.None);

        AppVersionStatus = string.Format("Versao local: {0} ({1})", result.CurrentVersion, result.CurrentReleaseId);
        LatestAvailableVersion = string.IsNullOrWhiteSpace(result.LatestReleaseId)
            ? "Sem informacao remota"
            : string.Format("{0} ({1})", result.LatestVersion, result.LatestReleaseId);
        IsUpdateAvailable = result.UpdateAvailable;
        RecommendedUpdatePackageUrl = result.RecommendedPackageUrl;
        UpdateStatusMessage = result.StatusMessage;
        _lastUpdateCheckResult = result;
        InstallUpdateCommand.RaiseCanExecuteChanged();
        MigrateToRemoteBuildCommand.RaiseCanExecuteChanged();
        return result;
    }

    private async Task DownloadAndApplyUpdateAsync(AppUpdateCheckResult result, bool automatic, string? customActionLabel = null)
    {
        var actionLabel = customActionLabel ?? (automatic ? "Auto-update" : "Atualizacao manual");
        UpdateStatusMessage = string.Format("{0}: baixando pacote {1}...", actionLabel, result.RecommendedPackageUrl);
        AppLog.Write("Updater", UpdateStatusMessage);

        var applyResult = await _appSelfUpdateService.DownloadAndPrepareAsync(result, CancellationToken.None);
        if (!applyResult.Succeeded)
        {
            UpdateStatusMessage = applyResult.Message;
            AppLog.Write("Updater", applyResult.Message);
            return;
        }

        UpdateStatusMessage = string.Format("{0}: pacote preparado. Reiniciando para aplicar a release {1}.", actionLabel, result.LatestReleaseId);
        AppLog.Write("Updater", UpdateStatusMessage);

        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            Application.Current.Shutdown();
        }));
    }

    private async Task PersistUpdatePreferencesAsync()
    {
        try
        {
            await _appUpdatePreferenceStore.SaveAsync(
                new AppUpdatePreferences
                {
                    AutoUpdateEnabled = AutoUpdateEnabled,
                    UpdateChannel = SelectedUpdateChannel,
                    AdditionalDiscoveryCidrs = AdditionalDiscoveryCidrs
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Write("Updater", string.Format("Falha ao salvar preferencia de auto-update: {0}", ex.Message));
        }
    }

    private async Task RefreshTargetsAfterStartupAsync()
    {
        try
        {
            await RefreshTargetsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format("Falha ao atualizar destinos automaticamente: {0}", ex.Message);
        }
    }

    private async Task CreateWindowAsync()
    {
        if (!TryCreateUri(BrowserUrlInput, out var url))
        {
            StatusMessage = "Informe uma URL valida para criar a janela CEF.";
            return;
        }

        var window = await _browserInstanceHost.CreateAsync(url, CancellationToken.None);
        if (window.ActiveSessionId == Guid.Empty)
        {
            window.ActiveSessionId = Guid.NewGuid();
        }

        if (string.IsNullOrWhiteSpace(window.ActiveSessionName))
        {
            window.ActiveSessionName = string.IsNullOrWhiteSpace(ProfileName) ? "Sessao ativa" : ProfileName.Trim();
        }

        if (string.IsNullOrWhiteSpace(window.ProfileName))
        {
            window.ProfileName = string.IsNullOrWhiteSpace(ProfileName) ? "default" : ProfileName.Trim();
        }

        Windows.Add(window);
        SelectedWindow = window;
        CurrentBrowserAddress = url.ToString();
        StatusMessage = string.Format("Janela '{0}' criada com a URL '{1}'.", window.Title, url);
        await SaveProfileInternalAsync(updateStatus: false);
        UpdateBridgeSnapshot();
    }

    private async Task RefreshTargetsAsync()
    {
        var targets = await _displayDiscoveryService.DiscoverAsync(CancellationToken.None);

        Targets.Clear();
        foreach (var target in targets)
        {
            Targets.Add(target);
        }

        RebindStaticPanels();
        SelectedTarget ??= Targets.FirstOrDefault();
        StatusMessage = Targets.Count == 0
            ? "Nenhum destino encontrado."
            : string.Format("{0} destino(s) encontrados.", Targets.Count);
    }

    private async Task<string> SendPowerCommandToKnownRokusAsync(bool powerOn)
    {
        var connectedResult = await _webRtcPublisherService.SendPowerCommandToConnectedDisplaysAsync(powerOn, CancellationToken.None);
        if (connectedResult.TargetedCount > 0)
        {
            return string.Format(
                "Comando de {0} concluido. Alvo(s): {1}, sucesso(s): {2}, falha(s): {3}, ignorada(s): {4}.",
                powerOn ? "ligar" : "desligar",
                connectedResult.TargetedCount,
                connectedResult.SuccessCount,
                connectedResult.FailureCount,
                connectedResult.SkippedCount);
        }

        var rokuTargets = Targets
            .Where(x => x.TransportKind == DisplayTransportKind.LanStreaming && !string.IsNullOrWhiteSpace(x.NetworkAddress))
            .ToList();

        if (rokuTargets.Count == 0)
        {
            return "Nenhuma TV Roku compativel foi encontrada para o comando de energia.";
        }

        var successCount = 0;
        var failureCount = 0;

        foreach (var target in rokuTargets)
        {
            var result = await _webRtcPublisherService.SendPowerCommandToDisplayTargetAsync(target, powerOn, CancellationToken.None);
            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ||
                result.StartsWith("ok_fallback_", StringComparison.OrdinalIgnoreCase))
            {
                successCount++;
            }
            else
            {
                failureCount++;
            }
        }

        return string.Format(
            "Comando de {0} concluido via TVs descobertas. Alvo(s): {1}, sucesso(s): {2}, falha(s): {3}.",
            powerOn ? "ligar" : "desligar",
            rokuTargets.Count,
            successCount,
            failureCount);
    }

    private bool CanAssignSelectedWindow() => SelectedWindow is not null && SelectedTarget is not null;
    private bool CanNavigateSelectedWindow() => SelectedWindow is not null && !string.IsNullOrWhiteSpace(BrowserUrlInput);
    private bool CanUseProfileName() => !string.IsNullOrWhiteSpace(ProfileName);
    private bool CanDeleteSelectedWindow() => SelectedWindow is not null;
    private bool CanPublishSelectedWindowWebRtc() => SelectedWindow is not null;
    private bool CanManageSelectedTarget() => SelectedTarget is not null;
    private bool CanDeleteSelectedPanel() => SelectedStaticPanel is not null;

    private async Task NavigateSelectedWindowAsync()
    {
        if (SelectedWindow is null)
        {
            StatusMessage = "Selecione uma janela antes de navegar.";
            return;
        }

        if (!TryCreateUri(BrowserUrlInput, out var uri))
        {
            StatusMessage = "Informe uma URL valida para abrir no CEF.";
            return;
        }

        await _browserInstanceHost.NavigateAsync(SelectedWindow.Id, uri, CancellationToken.None);
        SelectedWindow.InitialUri = uri;
        CurrentBrowserAddress = uri.ToString();
        StatusMessage = string.Format("Janela '{0}' navegando para '{1}'.", SelectedWindow.Title, uri);
        RaisePropertyChanged(nameof(PreviewSubtitle));

        if (SelectedWindow.IsWebRtcPublishingEnabled)
        {
            await PublishWindowWebRtcAsync(SelectedWindow, false);
        }

        await SaveProfileInternalAsync(updateStatus: false);
    }

    private async Task AssignSelectedWindowAsync()
    {
        if (SelectedWindow is null || SelectedTarget is null)
        {
            AppLog.Write("UI", "Transmitir clicado sem janela ou TV selecionada.");
            return;
        }

        AppLog.Write(
            "UI",
            string.Format(
                "Transmitir clicado: janela '{0}' -> TV '{1}' ({2}).",
                SelectedWindow.Title,
                SelectedTarget.Name,
                SelectedTarget.NetworkAddress));

        if (SelectedTarget.TransportKind == DisplayTransportKind.LanStreaming)
        {
            if (!SelectedWindow.IsWebRtcPublishingEnabled)
            {
                SelectedWindow.IsWebRtcPublishingEnabled = true;
            }

            await PublishWindowWebRtcAsync(SelectedWindow, true);

            if (!string.IsNullOrWhiteSpace(SelectedWindow.PublishedWebRtcUrl))
            {
                AppLog.Write(
                    "UI",
                    string.Format("Rota LAN publicada para teste/manual: {0}", SelectedWindow.PublishedWebRtcUrl));
            }
        }

        if (SelectedActiveSession is not null)
        {
            SelectedWindow.ActiveSessionId = SelectedActiveSession.Id;
            SelectedWindow.ActiveSessionName = SelectedActiveSession.Name;
            SelectedWindow.ProfileName = SelectedActiveSession.ProfileName;
            if (!SelectedActiveSession.WindowIds.Contains(SelectedWindow.Id))
            {
                foreach (var session in ActiveSessions.Where(x => x.Id != SelectedActiveSession.Id))
                {
                    session.WindowIds.Remove(SelectedWindow.Id);
                    session.WindowCount = session.WindowIds.Count;
                }

                SelectedActiveSession.WindowIds.Add(SelectedWindow.Id);
                SelectedActiveSession.WindowCount = SelectedActiveSession.WindowIds.Count;
            }

            foreach (var session in ActiveSessions.Where(x => x.Id != SelectedActiveSession.Id))
            {
                var existing = session.BoundDisplays.FirstOrDefault(x => x.DisplayTargetId == SelectedTarget.Id);
                if (existing is not null)
                {
                    session.BoundDisplays.Remove(existing);
                    session.TvCount = session.BoundDisplays.Count;
                }
            }

            if (!SelectedActiveSession.BoundDisplays.Any(x => x.DisplayTargetId == SelectedTarget.Id))
            {
                SelectedActiveSession.BoundDisplays.Add(new ActiveSessionDisplayBindingViewModel
                {
                    DisplayTargetId = SelectedTarget.Id,
                    DisplayName = SelectedTarget.Name,
                    NetworkAddress = SelectedTarget.NetworkAddress
                });
                SelectedActiveSession.TvCount = SelectedActiveSession.BoundDisplays.Count;
            }
        }

        await _routingService.AssignWindowToTargetAsync(SelectedWindow, SelectedTarget, CancellationToken.None);
        StatusMessage = SelectedTarget.TransportKind == DisplayTransportKind.Miracast
            ? string.Format("Janela '{0}' pronta para conexao com '{1}'. Conclua a transmissao no painel Cast do Windows.", SelectedWindow.Title, SelectedTarget.Name)
            : string.Format("Janela '{0}' associada a '{1}'.", SelectedWindow.Title, SelectedTarget.Name);
        RaisePropertyChanged(nameof(PreviewSubtitle));
        AppLog.Write(
            "UI",
            string.Format(
                "Fluxo concluido. Estado da janela '{0}': {1}.",
                SelectedWindow.Title,
                SelectedWindow.State));
        await SaveProfileInternalAsync(updateStatus: false);
        UpdateBridgeSnapshot();
    }

    private async Task ResetRuntimeStateAsync()
    {
        foreach (var window in Windows.ToList())
        {
            try
            {
                await _webRtcPublisherService.UnpublishAsync(window, CancellationToken.None);
            }
            catch
            {
            }

            try
            {
                await _browserInstanceHost.CloseAsync(window.Id, CancellationToken.None);
            }
            catch
            {
            }
        }

        Windows.Clear();
        Targets.Clear();
        ActiveSessions.Clear();
        TvProfiles.Clear();
        WindowProfiles.Clear();
        StaticPanels.Clear();
        AvailableProfiles.Clear();
        SelectedWindow = null;
        SelectedTarget = null;
        SelectedActiveSession = null;
        SelectedTvProfile = null;
        SelectedWindowProfile = null;
        SelectedStaticPanel = null;
        CurrentBrowserAddress = "about:blank";
        await PersistActiveSessionsAsync();
    }

    private async Task DeleteSelectedWindowAsync()
    {
        if (SelectedWindow is null)
        {
            return;
        }

        var windowToDelete = SelectedWindow;
        var deletedTitle = windowToDelete.Title;

        await _webRtcPublisherService.UnpublishAsync(windowToDelete, CancellationToken.None);
        await _browserInstanceHost.CloseAsync(windowToDelete.Id, CancellationToken.None);

        Windows.Remove(windowToDelete);
        foreach (var session in ActiveSessions)
        {
            if (session.WindowIds.Remove(windowToDelete.Id))
            {
                session.WindowCount = session.WindowIds.Count;
            }
        }

        RebuildActiveSessionsFromWindows();
        SelectedWindow = Windows.FirstOrDefault();
        CurrentBrowserAddress = SelectedWindow?.InitialUri?.ToString() ?? "about:blank";
        StatusMessage = string.Format("Painel '{0}' removido.", deletedTitle);
        await SaveProfileInternalAsync(updateStatus: false);
        UpdateBridgeSnapshot();
        await PersistActiveSessionsAsync();
    }

    private async Task PublishSelectedWindowWebRtcAsync()
    {
        if (SelectedWindow is null)
        {
            return;
        }

        await PublishWindowWebRtcAsync(SelectedWindow, true);
        await SaveProfileInternalAsync(updateStatus: false);
        UpdateBridgeSnapshot();
    }

    private async Task MarkSelectedTargetStaticAsync()
    {
        if (SelectedTarget is null)
        {
            return;
        }

        SelectedTarget.IsStaticTarget = true;
        await PersistKnownTargetsAsync();
        StatusMessage = string.Format("TV '{0}' marcada como estatica.", SelectedTarget.Name);
        await SaveProfileInternalAsync(updateStatus: false);
        UpdateBridgeSnapshot();
    }

    private async Task DeleteSelectedTargetAsync()
    {
        if (SelectedTarget is null)
        {
            return;
        }

        var targetId = SelectedTarget.Id;
        var targetName = SelectedTarget.Name;

        await _knownDisplayStore.RemoveAsync(targetId, CancellationToken.None);

        var panelsToRemove = StaticPanels.Where(x => x.DisplayTargetId == targetId).ToList();
        foreach (var panel in panelsToRemove)
        {
            StaticPanels.Remove(panel);
        }

        Targets.Remove(SelectedTarget);
        SelectedTarget = Targets.FirstOrDefault();
        StatusMessage = string.Format("TV '{0}' removida.", targetName);
        await SaveProfileInternalAsync(updateStatus: false);
    }

    private async Task CreateStaticPanelAsync()
    {
        if (SelectedTarget is null)
        {
            return;
        }

        SelectedTarget.IsStaticTarget = true;

        var panel = new StaticDisplayPanelViewModel
        {
            Name = string.Format("Painel {0}", SelectedTarget.Name),
            DisplayTargetId = SelectedTarget.Id,
            DisplayName = SelectedTarget.Name,
            PreferredWindowId = SelectedWindow?.Id,
            PreferredRouteNickname = SelectedWindow?.Title ?? SelectedTarget.Name,
            IsWebRtcEnabled = SelectedWindow?.IsWebRtcPublishingEnabled ?? false
        };

        StaticPanels.Add(panel);
        SelectedStaticPanel = panel;
        await PersistKnownTargetsAsync();
        StatusMessage = string.Format("Painel criado para a TV '{0}'.", SelectedTarget.Name);
        await SaveProfileInternalAsync(updateStatus: false);
    }

    private async Task DeleteSelectedPanelAsync()
    {
        if (SelectedStaticPanel is null)
        {
            return;
        }

        var panelName = SelectedStaticPanel.Name;
        StaticPanels.Remove(SelectedStaticPanel);
        SelectedStaticPanel = StaticPanels.FirstOrDefault();
        StatusMessage = string.Format("Painel '{0}' removido.", panelName);
        await SaveProfileInternalAsync(updateStatus: false);
    }

    private async Task SetDefaultProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            return;
        }

        await _profileStore.SetDefaultProfileNameAsync(ProfileName.Trim(), CancellationToken.None);
        await SyncDefaultProfileSelectionAsync();
        StatusMessage = string.Format("Perfil '{0}' definido como padrao de abertura.", ProfileName.Trim());
    }

    private async Task DeleteProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            return;
        }

        var deletedProfile = ProfileName.Trim();
        await _profileStore.DeleteAsync(deletedProfile, CancellationToken.None);
        await RefreshProfileNamesAsync();

        ProfileName = AvailableProfiles.FirstOrDefault() ?? "default";
        Windows.Clear();
        StaticPanels.Clear();
        SelectedWindow = null;
        SelectedStaticPanel = null;
        CurrentBrowserAddress = "about:blank";

        await SyncDefaultProfileSelectionAsync();
        StatusMessage = string.Format("Perfil '{0}' removido.", deletedProfile);
    }

    private async Task PublishWindowWebRtcAsync(WindowSession window, bool updateStatus)
    {
        if (!window.IsWebRtcPublishingEnabled)
        {
            await _webRtcPublisherService.UnpublishAsync(window, CancellationToken.None);
            window.PublishedWebRtcUrl = string.Empty;

            if (updateStatus)
            {
                StatusMessage = string.Format("Publicacao WebRTC local desativada para '{0}'.", window.Title);
            }

            UpdateBridgeSnapshot();
            return;
        }

        if (window.InitialUri is null)
        {
            StatusMessage = "Defina uma URL valida antes de publicar a rota WebRTC local.";
            return;
        }

        try
        {
            window.PublishedWebRtcUrl = await _webRtcPublisherService.PublishAsync(window, WebRtcServerPort, WebRtcBindMode, WebRtcSpecificIp, CancellationToken.None);

            if (updateStatus)
            {
                StatusMessage = string.Format("Rota WebRTC local publicada em {0}.", window.PublishedWebRtcUrl);
            }

            UpdateBridgeSnapshot();
        }
        catch (SocketException ex)
        {
            window.PublishedWebRtcUrl = string.Empty;
            StatusMessage = BuildPublishFailureMessage(ex);
            UpdateBridgeSnapshot();
            return;
        }
        catch (Exception ex)
        {
            window.PublishedWebRtcUrl = string.Empty;
            StatusMessage = string.Format("Falha ao publicar a rota da janela '{0}': {1}", window.Title, ex.Message);
            UpdateBridgeSnapshot();
            return;
        }
    }

    private string BuildPublishFailureMessage(SocketException ex)
    {
        if (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            return string.Format(
                "Falha ao abrir a rota local em {0}:{1}. O Windows negou o bind da porta.",
                WebRtcBindMode == WebRtcBindMode.Localhost ? "localhost" : LinkRtcAddressBuilder.ResolvePublicHost(WebRtcBindMode, WebRtcSpecificIp),
                WebRtcServerPort);
        }

        if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return string.Format("A porta {0} ja esta em uso por outro processo.", WebRtcServerPort);
        }

        return string.Format("Falha ao publicar a rota local: {0}", ex.Message);
    }
    private async Task SaveProfileAsync() => await SaveProfileInternalAsync(updateStatus: true);

    private async Task LoadProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            StatusMessage = "Informe um nome de perfil valido para carregar.";
            return;
        }

        var profile = await _profileStore.LoadAsync(ProfileName, CancellationToken.None);
        if (profile is null)
        {
            StatusMessage = string.Format("Perfil '{0}' ainda nao existe. Um novo sera criado quando voce salvar.", ProfileName);
            await SyncDefaultProfileSelectionAsync();
            return;
        }

        _isApplyingProfile = true;
        _suppressActiveSessionPersistence = true;
        try
        {
            var restoredSessionId = Guid.NewGuid();
            var restoredSessionName = profile.Name;
            WebRtcServerPort = profile.WebRtcServerPort <= 0 ? 8090 : profile.WebRtcServerPort;
            WebRtcBindMode = profile.WebRtcBindMode;
            WebRtcSpecificIp = profile.WebRtcSpecificIp ?? string.Empty;
            ApplyProfileTargets(profile);

            foreach (var window in Windows)
            {
                window.PropertyChanged -= OnWindowPropertyChanged;
            }

            Windows.Clear();
            StaticPanels.Clear();

            foreach (var persistedWindow in profile.Windows)
            {
                Uri? initialUri = null;
                if (!string.IsNullOrWhiteSpace(persistedWindow.InitialUrl))
                {
                    Uri.TryCreate(persistedWindow.InitialUrl, UriKind.Absolute, out initialUri);
                }

                var assignedTarget = Targets.FirstOrDefault(x => x.Id == persistedWindow.AssignedTargetId);
                var window = new WindowSession
                {
                    Id = persistedWindow.Id,
                    Title = string.IsNullOrWhiteSpace(persistedWindow.Title) ? "Janela restaurada" : persistedWindow.Title,
                    InitialUri = initialUri,
                    State = persistedWindow.State,
                    AssignedTarget = assignedTarget,
                    BrowserResolutionMode = persistedWindow.BrowserResolutionMode,
                    BrowserManualWidth = persistedWindow.BrowserManualWidth,
                    BrowserManualHeight = persistedWindow.BrowserManualHeight,
                    TargetResolutionMode = persistedWindow.TargetResolutionMode,
                    TargetManualWidth = persistedWindow.TargetManualWidth,
                    TargetManualHeight = persistedWindow.TargetManualHeight,
                    IsWebRtcPublishingEnabled = persistedWindow.IsWebRtcPublishingEnabled,
                    ProfileName = string.IsNullOrWhiteSpace(persistedWindow.ProfileName) ? profile.Name : persistedWindow.ProfileName,
                    ActiveSessionId = persistedWindow.ActiveSessionId == Guid.Empty ? restoredSessionId : persistedWindow.ActiveSessionId,
                    ActiveSessionName = string.IsNullOrWhiteSpace(persistedWindow.ActiveSessionName) ? restoredSessionName : persistedWindow.ActiveSessionName
                };

                Windows.Add(window);
            }

            foreach (var panelProfile in profile.StaticPanels)
            {
                var target = Targets.FirstOrDefault(x => x.Id == panelProfile.DisplayTargetId);
                StaticPanels.Add(new StaticDisplayPanelViewModel
                {
                    Id = panelProfile.Id,
                    Name = panelProfile.Name,
                    DisplayTargetId = panelProfile.DisplayTargetId,
                    DisplayName = string.IsNullOrWhiteSpace(panelProfile.DisplayName) ? (target?.Name ?? "TV removida") : panelProfile.DisplayName,
                    PreferredWindowId = panelProfile.PreferredWindowId,
                    PreferredRouteNickname = panelProfile.PreferredRouteNickname,
                    IsWebRtcEnabled = panelProfile.IsWebRtcEnabled
                });
            }

            SelectedWindow = Windows.FirstOrDefault(x => x.Id == profile.SelectedWindowId) ?? Windows.FirstOrDefault();
            SelectedTarget = Targets.FirstOrDefault(x => x.Id == profile.SelectedTargetId)
                ?? SelectedWindow?.AssignedTarget
                ?? Targets.FirstOrDefault();
            SelectedStaticPanel = StaticPanels.FirstOrDefault();
            CurrentBrowserAddress = SelectedWindow?.InitialUri?.ToString() ?? "about:blank";
        }
        finally
        {
            _isApplyingProfile = false;
            _suppressActiveSessionPersistence = false;
        }

        RestoreProfileDisplayBindings(profile);
        RestoreTvProfiles(profile);
        RestoreWindowProfiles(profile);
        await RefreshProfileNamesAsync();
        await SyncDefaultProfileSelectionAsync();
        RebuildActiveSessionsFromWindows();
        StatusMessage = string.Format("Perfil '{0}' restaurado com sucesso. As rotas WebRTC podem ser atualizadas manualmente apos a abertura.", ProfileName);
        UpdateBridgeSnapshot();
    }

    private async Task RestoreActiveSessionsAsync()
    {
        var records = await _activeSessionStore.LoadAsync(CancellationToken.None);
        if (records.Count == 0)
        {
            return;
        }

        _isRestoringActiveSessions = true;
        _suppressActiveSessionPersistence = true;
        try
        {
            await ClearRuntimeWindowsAsync();

            foreach (var record in records)
            {
                var session = new ActiveSessionViewModel
                {
                    Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id,
                    Name = string.IsNullOrWhiteSpace(record.Name) ? BuildSessionName(record.ProfileName) : record.Name,
                    ProfileName = record.ProfileName,
                    StartedAtUtc = record.StartedAtUtc
                };

                foreach (var bindingRecord in record.BoundDisplays)
                {
                    var target = Targets.FirstOrDefault(x =>
                        x.Id == bindingRecord.DisplayTargetId ||
                        (!string.IsNullOrWhiteSpace(bindingRecord.DeviceUniqueId) &&
                         string.Equals(x.DeviceUniqueId, bindingRecord.DeviceUniqueId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(bindingRecord.NetworkAddress) &&
                         string.Equals(x.NetworkAddress, bindingRecord.NetworkAddress, StringComparison.OrdinalIgnoreCase)));

                    session.BoundDisplays.Add(new ActiveSessionDisplayBindingViewModel
                    {
                        DisplayTargetId = target?.Id ?? bindingRecord.DisplayTargetId,
                        DisplayName = target?.Name ?? bindingRecord.DisplayName,
                        NetworkAddress = target?.NetworkAddress ?? bindingRecord.NetworkAddress,
                        DeviceUniqueId = target?.DeviceUniqueId ?? bindingRecord.DeviceUniqueId,
                        BindingName = string.IsNullOrWhiteSpace(bindingRecord.BindingName)
                            ? (target?.Name ?? bindingRecord.DisplayName)
                            : bindingRecord.BindingName
                    });
                }

                ActiveSessions.Add(session);

                foreach (var windowRecord in record.Windows)
                {
                    Uri? initialUri = null;
                    if (!string.IsNullOrWhiteSpace(windowRecord.InitialUrl))
                    {
                        Uri.TryCreate(windowRecord.InitialUrl, UriKind.Absolute, out initialUri);
                    }

                    var browserWindow = await _browserInstanceHost.CreateAsync(initialUri ?? new Uri("about:blank"), CancellationToken.None);
                    var assignedTarget = Targets.FirstOrDefault(x => x.Id == windowRecord.AssignedTargetId);
                    browserWindow.Id = windowRecord.Id == Guid.Empty ? Guid.NewGuid() : windowRecord.Id;
                    browserWindow.Title = string.IsNullOrWhiteSpace(windowRecord.Title) ? browserWindow.Title : windowRecord.Title;
                    browserWindow.InitialUri = initialUri;
                    browserWindow.State = windowRecord.State;
                    browserWindow.AssignedTarget = assignedTarget;
                    browserWindow.BrowserResolutionMode = windowRecord.BrowserResolutionMode;
                    browserWindow.BrowserManualWidth = windowRecord.BrowserManualWidth;
                    browserWindow.BrowserManualHeight = windowRecord.BrowserManualHeight;
                    browserWindow.TargetResolutionMode = windowRecord.TargetResolutionMode;
                    browserWindow.TargetManualWidth = windowRecord.TargetManualWidth;
                    browserWindow.TargetManualHeight = windowRecord.TargetManualHeight;
                    browserWindow.IsWebRtcPublishingEnabled = windowRecord.IsWebRtcPublishingEnabled;
                    browserWindow.ProfileName = record.ProfileName;
                    browserWindow.ActiveSessionId = session.Id;
                    browserWindow.ActiveSessionName = session.Name;
                    Windows.Add(browserWindow);
                }
            }

            RebuildActiveSessionsFromWindows();
            SelectedActiveSession = ActiveSessions.FirstOrDefault();
            SelectedWindow = Windows.FirstOrDefault();
            CurrentBrowserAddress = SelectedWindow?.InitialUri?.ToString() ?? "about:blank";
            StatusMessage = string.Format("{0} sessoes ativas foram restauradas.", ActiveSessions.Count);
            UpdateBridgeSnapshot();
        }
        finally
        {
            _isRestoringActiveSessions = false;
            _suppressActiveSessionPersistence = false;
        }
    }

    private async Task PersistActiveSessionsAsync()
    {
        if (_isApplyingProfile || _isRestoringActiveSessions || _suppressActiveSessionPersistence)
        {
            return;
        }

        var records = ActiveSessions.Select(session => new ActiveSessionRecord
        {
            Id = session.Id,
            Name = session.Name,
            ProfileName = session.ProfileName,
            StartedAtUtc = string.IsNullOrWhiteSpace(session.StartedAtUtc) ? DateTime.UtcNow.ToString("o") : session.StartedAtUtc,
            Windows = Windows
                .Where(window => window.ActiveSessionId == session.Id)
                .Select(window => new ActiveSessionWindowRecord
                {
                    Id = window.Id,
                    Title = window.Title,
                    InitialUrl = window.InitialUri?.ToString() ?? string.Empty,
                    State = window.State,
                    AssignedTargetId = window.AssignedTarget?.Id,
                    BrowserResolutionMode = window.BrowserResolutionMode,
                    BrowserManualWidth = window.BrowserManualWidth,
                    BrowserManualHeight = window.BrowserManualHeight,
                    TargetResolutionMode = window.TargetResolutionMode,
                    TargetManualWidth = window.TargetManualWidth,
                    TargetManualHeight = window.TargetManualHeight,
                    IsWebRtcPublishingEnabled = window.IsWebRtcPublishingEnabled
                }).ToList(),
            BoundDisplays = session.BoundDisplays.Select(binding => new ActiveSessionDisplayBindingRecord
            {
                DisplayTargetId = binding.DisplayTargetId,
                DisplayName = binding.DisplayName,
                NetworkAddress = binding.NetworkAddress,
                DeviceUniqueId = binding.DeviceUniqueId,
                BindingName = binding.BindingName
            }).ToList()
        }).ToList();

        await _activeSessionStore.SaveAsync(records, CancellationToken.None);
    }

    private async Task ClearRuntimeWindowsAsync()
    {
        foreach (var window in Windows.ToList())
        {
            try
            {
                await _webRtcPublisherService.UnpublishAsync(window, CancellationToken.None);
            }
            catch
            {
            }

            try
            {
                await _browserInstanceHost.CloseAsync(window.Id, CancellationToken.None);
            }
            catch
            {
            }
        }

        Windows.Clear();
        ActiveSessions.Clear();
        SelectedWindow = null;
        SelectedActiveSession = null;
        CurrentBrowserAddress = "about:blank";
    }

    private async Task SaveProfileInternalAsync(bool updateStatus)
    {
        if (_isApplyingProfile || string.IsNullOrWhiteSpace(ProfileName))
        {
            return;
        }

        var profile = new AppProfile
        {
            Name = ProfileName.Trim(),
            SelectedWindowId = SelectedWindow?.Id,
            SelectedTargetId = SelectedTarget?.Id,
            WebRtcServerPort = WebRtcServerPort,
            WebRtcBindMode = WebRtcBindMode,
            WebRtcSpecificIp = WebRtcSpecificIp,
            DisplayTargets = Targets.Select(x => new DisplayTargetProfile
            {
                Id = x.Id,
                Name = x.Name,
                NetworkAddress = x.NetworkAddress,
                LastKnownNetworkAddress = x.LastKnownNetworkAddress,
                MacAddress = x.MacAddress,
                DeviceUniqueId = x.DeviceUniqueId,
                DiscoverySource = x.DiscoverySource,
                TransportKind = x.TransportKind,
                IsOnline = x.IsOnline,
                WasPreviouslyKnown = x.WasPreviouslyKnown,
                IsStaticTarget = x.IsStaticTarget,
                NativeWidth = x.NativeWidth,
                NativeHeight = x.NativeHeight
            }).ToList(),
            Windows = Windows
                .Where(x => string.Equals(x.ProfileName, ProfileName.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(x => new WindowSessionProfile
            {
                Id = x.Id,
                Title = x.Title,
                InitialUrl = x.InitialUri?.ToString() ?? string.Empty,
                State = x.State,
                AssignedTargetId = x.AssignedTarget?.Id,
                BrowserResolutionMode = x.BrowserResolutionMode,
                BrowserManualWidth = x.BrowserManualWidth,
                BrowserManualHeight = x.BrowserManualHeight,
                TargetResolutionMode = x.TargetResolutionMode,
                TargetManualWidth = x.TargetManualWidth,
                TargetManualHeight = x.TargetManualHeight,
                IsWebRtcPublishingEnabled = x.IsWebRtcPublishingEnabled,
                ProfileName = x.ProfileName,
                ActiveSessionId = x.ActiveSessionId,
                ActiveSessionName = x.ActiveSessionName
            }).ToList(),
            DisplayBindings = BuildProfileDisplayBindings(),
            TvProfiles = BuildTvProfiles(),
            WindowProfiles = BuildWindowProfiles(),
            StaticPanels = StaticPanels.Select(x => new StaticPanelProfile
            {
                Id = x.Id,
                Name = x.Name,
                DisplayTargetId = x.DisplayTargetId,
                DisplayName = x.DisplayName,
                PreferredWindowId = x.PreferredWindowId,
                PreferredRouteNickname = x.PreferredRouteNickname,
                IsWebRtcEnabled = x.IsWebRtcEnabled
            }).ToList()
        };

        await _profileStore.SaveAsync(profile, CancellationToken.None);
        await RefreshProfileNamesAsync();
        await SyncDefaultProfileSelectionAsync();

        if (updateStatus)
        {
            StatusMessage = string.Format("Perfil '{0}' salvo com sucesso.", profile.Name);
        }
    }

    private async Task SyncDefaultProfileSelectionAsync()
    {
        var defaultProfileName = await _profileStore.GetDefaultProfileNameAsync(CancellationToken.None);
        IsDefaultProfile = !string.IsNullOrWhiteSpace(defaultProfileName)
            && string.Equals(defaultProfileName, ProfileName?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshProfileNamesAsync()
    {
        var currentProfileName = ProfileName?.Trim() ?? string.Empty;
        var names = await _profileStore.GetProfileNamesAsync(CancellationToken.None);

        _isRefreshingProfileNames = true;
        try
        {
            AvailableProfiles.Clear();
            foreach (var name in names)
            {
                AvailableProfiles.Add(name);
            }

            if (!string.IsNullOrWhiteSpace(currentProfileName) &&
                !AvailableProfiles.Any(x => string.Equals(x, currentProfileName, StringComparison.OrdinalIgnoreCase)))
            {
                AvailableProfiles.Add(currentProfileName);
            }
        }
        finally
        {
            _isRefreshingProfileNames = false;
        }

        if (!string.IsNullOrWhiteSpace(currentProfileName) &&
            !string.Equals(ProfileName, currentProfileName, StringComparison.OrdinalIgnoreCase))
        {
            ProfileName = currentProfileName;
        }
    }

    private void ApplyProfileTargets(AppProfile profile)
    {
        foreach (var targetProfile in profile.DisplayTargets)
        {
            if (IsLegacyFakeProfileTarget(targetProfile))
            {
                continue;
            }

            var target = Targets.FirstOrDefault(x => x.Id == targetProfile.Id);
            if (target is null)
            {
                Targets.Add(new DisplayTarget
                {
                    Id = targetProfile.Id,
                    Name = targetProfile.Name,
                    NetworkAddress = targetProfile.NetworkAddress,
                    LastKnownNetworkAddress = targetProfile.LastKnownNetworkAddress,
                    MacAddress = targetProfile.MacAddress,
                    DeviceUniqueId = targetProfile.DeviceUniqueId,
                    DiscoverySource = targetProfile.DiscoverySource,
                    TransportKind = targetProfile.TransportKind,
                    IsOnline = targetProfile.IsOnline,
                    WasPreviouslyKnown = targetProfile.WasPreviouslyKnown,
                    IsStaticTarget = targetProfile.IsStaticTarget,
                    NativeWidth = targetProfile.NativeWidth,
                    NativeHeight = targetProfile.NativeHeight
                });

                continue;
            }

            target.Name = targetProfile.Name;
            target.NetworkAddress = targetProfile.NetworkAddress;
            target.LastKnownNetworkAddress = targetProfile.LastKnownNetworkAddress;
            target.MacAddress = targetProfile.MacAddress;
            target.DeviceUniqueId = targetProfile.DeviceUniqueId;
            target.DiscoverySource = targetProfile.DiscoverySource;
            target.TransportKind = targetProfile.TransportKind;
            target.IsOnline = targetProfile.IsOnline;
            target.WasPreviouslyKnown = targetProfile.WasPreviouslyKnown;
            target.IsStaticTarget = targetProfile.IsStaticTarget;
            target.NativeWidth = targetProfile.NativeWidth;
            target.NativeHeight = targetProfile.NativeHeight;
        }
    }

    private async Task PersistKnownTargetsAsync()
    {
        var records = Targets
            .Where(x => !IsLegacyFakeDisplayTarget(x))
            .Select(x => new KnownDisplayRecord
        {
            Id = x.Id,
            Name = x.Name,
            NetworkAddress = x.NetworkAddress,
            LastKnownNetworkAddress = x.LastKnownNetworkAddress,
            MacAddress = x.MacAddress,
            DeviceUniqueId = x.DeviceUniqueId,
            DiscoverySource = x.DiscoverySource,
            TransportKind = x.TransportKind,
            NativeWidth = x.NativeWidth,
            NativeHeight = x.NativeHeight,
            IsStaticTarget = x.IsStaticTarget
        }).ToList();

        await _knownDisplayStore.SaveAsync(records, CancellationToken.None);
    }

    private void RebindStaticPanels()
    {
        foreach (var panel in StaticPanels)
        {
            var target = Targets.FirstOrDefault(x => x.Id == panel.DisplayTargetId);
            panel.DisplayName = target?.Name ?? "TV removida";
        }
    }

    private List<DisplayBindingProfile> BuildProfileDisplayBindings()
    {
        var bindingMap = new Dictionary<Guid, DisplayBindingProfile>();

        foreach (var window in Windows.Where(x =>
                     string.Equals(x.ProfileName, ProfileName?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                     x.AssignedTarget is not null))
        {
            var target = window.AssignedTarget!;
            if (!bindingMap.ContainsKey(target.Id))
            {
                bindingMap[target.Id] = new DisplayBindingProfile
                {
                    Id = Guid.NewGuid(),
                    Name = target.Name,
                    DisplayTargetId = target.Id,
                    DisplayTargetName = target.Name,
                    DeviceUniqueId = target.DeviceUniqueId,
                    NetworkAddress = target.NetworkAddress
                };
            }
        }

        foreach (var panel in StaticPanels)
        {
            var target = Targets.FirstOrDefault(x => x.Id == panel.DisplayTargetId);
            if (target is null || bindingMap.ContainsKey(target.Id))
            {
                continue;
            }

            bindingMap[target.Id] = new DisplayBindingProfile
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(panel.Name) ? target.Name : panel.Name,
                DisplayTargetId = target.Id,
                DisplayTargetName = target.Name,
                DeviceUniqueId = target.DeviceUniqueId,
                NetworkAddress = target.NetworkAddress
            };
        }

        return bindingMap.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<TvProfileDefinition> BuildTvProfiles()
    {
        return TvProfiles.Select(profile => new TvProfileDefinition
        {
            Id = profile.Id,
            Name = profile.Name,
            Targets = profile.Targets.Select(target => new TvProfileTargetDefinition
            {
                DisplayTargetId = target.DisplayTargetId,
                DisplayName = target.DisplayName,
                NetworkAddress = target.NetworkAddress,
                DeviceUniqueId = target.DeviceUniqueId,
                MacAddress = target.MacAddress,
                DiscoverySource = target.DiscoverySource,
                NativeWidth = target.NativeWidth,
                NativeHeight = target.NativeHeight
            }).ToList()
        }).ToList();
    }

    private List<WindowGroupProfile> BuildWindowProfiles()
    {
        return WindowProfiles.Select(profile => new WindowGroupProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            AssignedTvProfileId = profile.AssignedTvProfileId,
            AssignedTvProfileName = profile.AssignedTvProfileName,
            Windows = profile.Windows.Select(window => new WindowLinkProfile
            {
                Id = window.Id,
                Nickname = window.Nickname,
                Url = window.Url
            }).ToList()
        }).ToList();
    }

    private void RestoreTvProfiles(AppProfile profile)
    {
        TvProfiles.Clear();

        foreach (var tvProfile in profile.TvProfiles.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var viewModel = new TvProfileViewModel
            {
                Id = tvProfile.Id == Guid.Empty ? Guid.NewGuid() : tvProfile.Id,
                Name = tvProfile.Name
            };

            foreach (var target in tvProfile.Targets)
            {
                viewModel.Targets.Add(new TvProfileTargetViewModel
                {
                    DisplayTargetId = target.DisplayTargetId,
                    DisplayName = target.DisplayName,
                    NetworkAddress = target.NetworkAddress,
                    DeviceUniqueId = target.DeviceUniqueId,
                    MacAddress = target.MacAddress,
                    DiscoverySource = target.DiscoverySource,
                    NativeWidth = target.NativeWidth,
                    NativeHeight = target.NativeHeight
                });
            }

            TvProfiles.Add(viewModel);
        }

        SelectedTvProfile = TvProfiles.FirstOrDefault();
    }

    private void RestoreWindowProfiles(AppProfile profile)
    {
        WindowProfiles.Clear();

        foreach (var windowProfile in profile.WindowProfiles.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var viewModel = new WindowProfileViewModel
            {
                Id = windowProfile.Id == Guid.Empty ? Guid.NewGuid() : windowProfile.Id,
                Name = windowProfile.Name,
                AssignedTvProfileId = windowProfile.AssignedTvProfileId,
                AssignedTvProfileName = windowProfile.AssignedTvProfileName
            };

            foreach (var window in windowProfile.Windows)
            {
                viewModel.Windows.Add(new WindowProfileItemViewModel
                {
                    Id = window.Id == Guid.Empty ? Guid.NewGuid() : window.Id,
                    Nickname = window.Nickname,
                    Url = window.Url
                });
            }

            WindowProfiles.Add(viewModel);
        }

        SelectedWindowProfile = WindowProfiles.FirstOrDefault();
    }

    private async Task<DisplayTarget> EnsureTargetForTvProfileEntryAsync(TvProfileTargetEditorViewModel entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.NetworkAddress))
        {
            var probedTarget = await _manualDisplayProbeService.ProbeAsync(entry.NetworkAddress, CancellationToken.None);
            if (probedTarget is not null)
            {
                entry.DisplayTargetId = probedTarget.Id;
                entry.DisplayName = probedTarget.Name;
                entry.DeviceUniqueId = probedTarget.DeviceUniqueId;
                entry.MacAddress = probedTarget.MacAddress;
                entry.DiscoverySource = probedTarget.DiscoverySource;
                entry.NativeWidth = probedTarget.NativeWidth;
                entry.NativeHeight = probedTarget.NativeHeight;
            }
        }

        var existingTarget = Targets.FirstOrDefault(x =>
            (entry.DisplayTargetId != Guid.Empty && x.Id == entry.DisplayTargetId) ||
            (!string.IsNullOrWhiteSpace(entry.DeviceUniqueId) &&
             string.Equals(x.DeviceUniqueId, entry.DeviceUniqueId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(entry.NetworkAddress) &&
             string.Equals(x.NetworkAddress, entry.NetworkAddress, StringComparison.OrdinalIgnoreCase)));

        if (existingTarget is not null)
        {
            if (!string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                existingTarget.Name = entry.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(entry.NetworkAddress))
            {
                existingTarget.NetworkAddress = entry.NetworkAddress;
                existingTarget.LastKnownNetworkAddress = entry.NetworkAddress;
            }

            if (!string.IsNullOrWhiteSpace(entry.DeviceUniqueId))
            {
                existingTarget.DeviceUniqueId = entry.DeviceUniqueId;
            }

            if (!string.IsNullOrWhiteSpace(entry.MacAddress))
            {
                existingTarget.MacAddress = entry.MacAddress;
            }

            if (!string.IsNullOrWhiteSpace(entry.DiscoverySource))
            {
                existingTarget.DiscoverySource = entry.DiscoverySource;
            }

            if (entry.NativeWidth > 0)
            {
                existingTarget.NativeWidth = entry.NativeWidth;
            }

            if (entry.NativeHeight > 0)
            {
                existingTarget.NativeHeight = entry.NativeHeight;
            }

            return existingTarget;
        }

        var target = new DisplayTarget
        {
            Id = entry.DisplayTargetId == Guid.Empty ? Guid.NewGuid() : entry.DisplayTargetId,
            Name = string.IsNullOrWhiteSpace(entry.DisplayName) ? string.Format("TV {0}", entry.NetworkAddress) : entry.DisplayName,
            NetworkAddress = entry.NetworkAddress,
            LastKnownNetworkAddress = entry.NetworkAddress,
            MacAddress = entry.MacAddress,
            DeviceUniqueId = entry.DeviceUniqueId,
            DiscoverySource = string.IsNullOrWhiteSpace(entry.DiscoverySource) ? "Manual" : entry.DiscoverySource,
            TransportKind = DisplayTransportKind.LanStreaming,
            IsOnline = false,
            WasPreviouslyKnown = true,
            IsStaticTarget = true,
            NativeWidth = entry.NativeWidth <= 0 ? 1920 : entry.NativeWidth,
            NativeHeight = entry.NativeHeight <= 0 ? 1080 : entry.NativeHeight
        };

        Targets.Add(target);
        return target;
    }

    private void RestoreProfileDisplayBindings(AppProfile profile)
    {
        ProfileDisplayBindings.Clear();

        var bindings = profile.DisplayBindings;
        if (bindings is null || bindings.Count == 0)
        {
            bindings = BuildProfileDisplayBindingsFromProfile(profile);
        }

        foreach (var binding in bindings)
        {
            ProfileDisplayBindings.Add(new ProfileDisplayBindingViewModel
            {
                Id = binding.Id == Guid.Empty ? Guid.NewGuid() : binding.Id,
                Name = binding.Name,
                DisplayTargetId = binding.DisplayTargetId,
                DisplayTargetName = binding.DisplayTargetName,
                DeviceUniqueId = binding.DeviceUniqueId,
                NetworkAddress = binding.NetworkAddress
            });
        }
    }

    private static List<DisplayBindingProfile> BuildProfileDisplayBindingsFromProfile(AppProfile profile)
    {
        var bindings = new Dictionary<Guid, DisplayBindingProfile>();
        foreach (var persistedWindow in profile.Windows)
        {
            if (!persistedWindow.AssignedTargetId.HasValue)
            {
                continue;
            }

            var assignedTargetId = persistedWindow.AssignedTargetId.Value;
            var target = profile.DisplayTargets.FirstOrDefault(x => x.Id == assignedTargetId);
            if (target is null || bindings.ContainsKey(target.Id))
            {
                continue;
            }

            bindings[target.Id] = new DisplayBindingProfile
            {
                Id = Guid.NewGuid(),
                Name = target.Name,
                DisplayTargetId = target.Id,
                DisplayTargetName = target.Name,
                DeviceUniqueId = target.DeviceUniqueId,
                NetworkAddress = target.NetworkAddress
            };
        }

        return bindings.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void OnWindowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (WindowSession window in e.NewItems)
            {
                window.PropertyChanged += OnWindowPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (WindowSession window in e.OldItems)
            {
                window.PropertyChanged -= OnWindowPropertyChanged;
            }
        }

        RebuildActiveSessionsFromWindows();
        QueueAutoSave();
        UpdateBridgeSnapshot();
    }

    private async void OnWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is WindowSession window &&
            (e.PropertyName == nameof(WindowSession.IsWebRtcPublishingEnabled) ||
             e.PropertyName == nameof(WindowSession.InitialUri) ||
             e.PropertyName == nameof(WindowSession.Title)))
        {
            try
            {
                await PublishWindowWebRtcAsync(window, false);
            }
            catch
            {
            }
        }

        QueueAutoSave();
        UpdateBridgeSnapshot();
    }

    private async void QueueAutoSave()
    {
        if (_isApplyingProfile)
        {
            return;
        }

        try
        {
            await SaveProfileInternalAsync(updateStatus: false);
        }
        catch
        {
        }
    }

    private async void RepublishEnabledWindows()
    {
        if (_isApplyingProfile)
        {
            return;
        }

        foreach (var window in Windows.Where(x => x.IsWebRtcPublishingEnabled))
        {
            try
            {
                await PublishWindowWebRtcAsync(window, false);
            }
            catch
            {
            }
        }

        UpdateBridgeSnapshot();
    }

    private void UpdateBridgeSnapshot()
    {
        try
        {
            _webRtcPublisherService.UpdateWindowSnapshots(Windows, BuildBridgeSessions(), WebRtcServerPort, WebRtcBindMode, WebRtcSpecificIp);
        }
        catch
        {
        }
    }

    private List<BridgeActiveSessionSnapshot> BuildBridgeSessions()
    {
        return ActiveSessions.Select(session => new BridgeActiveSessionSnapshot
        {
            Id = session.Id.ToString("N"),
            Name = session.Name,
            ProfileName = session.ProfileName,
            WindowIds = session.WindowIds.Select(x => x.ToString("N")).ToList(),
            DisplayAddresses = session.BoundDisplays
                .Select(x => x.NetworkAddress)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        }).ToList();
    }

    private void RebuildActiveSessionsFromWindows()
    {
        var existingSessions = ActiveSessions
            .ToDictionary(
                x => x.Id,
                x => new
                {
                    x.Name,
                    x.ProfileName,
                    x.StartedAtUtc,
                    BoundDisplays = x.BoundDisplays.Select(binding => new ActiveSessionDisplayBindingViewModel
                    {
                        DisplayTargetId = binding.DisplayTargetId,
                        DisplayName = binding.DisplayName,
                        NetworkAddress = binding.NetworkAddress,
                        DeviceUniqueId = binding.DeviceUniqueId,
                        BindingName = binding.BindingName
                    }).ToList()
                });

        var windowGroups = Windows
            .Where(x => x.ActiveSessionId != Guid.Empty)
            .GroupBy(x => x.ActiveSessionId)
            .ToDictionary(x => x.Key, x => x.ToList());

        var orderedSessionIds = existingSessions.Keys
            .Concat(windowGroups.Keys)
            .Distinct()
            .ToList();

        var selectedSessionId = SelectedActiveSession?.Id;

        ActiveSessions.Clear();

        foreach (var sessionId in orderedSessionIds)
        {
            windowGroups.TryGetValue(sessionId, out var groupedWindows);
            var first = groupedWindows?.FirstOrDefault();
            existingSessions.TryGetValue(sessionId, out var existingSession);
            var session = new ActiveSessionViewModel
            {
                Id = sessionId,
                Name = existingSession?.Name
                    ?? (string.IsNullOrWhiteSpace(first?.ActiveSessionName) ? first?.ProfileName ?? "Sessao" : first?.ActiveSessionName ?? "Sessao"),
                ProfileName = existingSession?.ProfileName ?? first?.ProfileName ?? string.Empty,
                StartedAtUtc = existingSession?.StartedAtUtc ?? DateTime.UtcNow.ToString("o"),
                WindowCount = groupedWindows?.Count ?? 0,
                TvCount = 0
            };

            foreach (var window in groupedWindows ?? Enumerable.Empty<WindowSession>())
            {
                session.WindowIds.Add(window.Id);
            }

            foreach (var binding in existingSession?.BoundDisplays ?? Enumerable.Empty<ActiveSessionDisplayBindingViewModel>())
            {
                session.BoundDisplays.Add(binding);
            }

            session.TvCount = session.BoundDisplays.Count;
            session.BindingCount = session.BoundDisplays.Count;

            ActiveSessions.Add(session);
        }

        if (selectedSessionId.HasValue)
        {
            SelectedActiveSession = ActiveSessions.FirstOrDefault(x => x.Id == selectedSessionId.Value);
        }

        if (SelectedActiveSession is null)
        {
            SelectedActiveSession = ActiveSessions.FirstOrDefault();
        }
    }

    private string BuildSessionName(string profileName)
    {
        var baseName = string.IsNullOrWhiteSpace(profileName) ? "Sessao" : profileName.Trim();
        var nextNumber = ActiveSessions.Count(x => string.Equals(x.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)) + 1;
        return string.Format("{0} #{1}", baseName, nextNumber);
    }

    private static bool TryCreateUri(string input, out Uri uri)
    {
        var normalized = input?.Trim() ?? string.Empty;
        if (normalized.IndexOf("://", StringComparison.Ordinal) < 0)
        {
            normalized = string.Format("https://{0}", normalized);
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out uri!)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool IsLegacyFakeProfileTarget(DisplayTargetProfile targetProfile)
    {
        return targetProfile.Id == LegacyTvSalaId ||
               targetProfile.Id == LegacyDongleMiracastId ||
               string.Equals(targetProfile.DeviceUniqueId, "TV-SALA-AA-BB-CC-11-22-33", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(targetProfile.DeviceUniqueId, "DONGLE-MIRACAST-DD-EE-FF-44-55-66", StringComparison.OrdinalIgnoreCase);
    }

private static bool IsLegacyFakeDisplayTarget(DisplayTarget target)
    {
        return target.Id == LegacyTvSalaId ||
               target.Id == LegacyDongleMiracastId ||
               string.Equals(target.DeviceUniqueId, "TV-SALA-AA-BB-CC-11-22-33", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target.DeviceUniqueId, "DONGLE-MIRACAST-DD-EE-FF-44-55-66", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ActiveSessionViewModel : ViewModelBase
{
    private Guid _id;
    private string _name = string.Empty;
    private string _profileName = string.Empty;
    private string _startedAtUtc = string.Empty;
    private int _windowCount;
    private int _tvCount;
    private int _bindingCount;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }

    public string StartedAtUtc
    {
        get => _startedAtUtc;
        set => SetProperty(ref _startedAtUtc, value);
    }

    public int WindowCount
    {
        get => _windowCount;
        set => SetProperty(ref _windowCount, value);
    }

    public int TvCount
    {
        get => _tvCount;
        set => SetProperty(ref _tvCount, value);
    }

    public int BindingCount
    {
        get => _bindingCount;
        set => SetProperty(ref _bindingCount, value);
    }

    public ObservableCollection<Guid> WindowIds { get; } = new ObservableCollection<Guid>();

    public ObservableCollection<ActiveSessionDisplayBindingViewModel> BoundDisplays { get; } = new ObservableCollection<ActiveSessionDisplayBindingViewModel>();
}

public sealed class ActiveSessionDisplayBindingViewModel : ViewModelBase
{
    private Guid _displayTargetId;
    private string _displayName = string.Empty;
    private string _networkAddress = string.Empty;
    private string _deviceUniqueId = string.Empty;
    private string _bindingName = string.Empty;

    public Guid DisplayTargetId
    {
        get => _displayTargetId;
        set => SetProperty(ref _displayTargetId, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string NetworkAddress
    {
        get => _networkAddress;
        set => SetProperty(ref _networkAddress, value);
    }

    public string DeviceUniqueId
    {
        get => _deviceUniqueId;
        set => SetProperty(ref _deviceUniqueId, value);
    }

    public string BindingName
    {
        get => _bindingName;
        set => SetProperty(ref _bindingName, value);
    }
}

public sealed class ProfileDisplayBindingViewModel : ViewModelBase
{
    private Guid _id;
    private string _name = string.Empty;
    private Guid _displayTargetId;
    private string _displayTargetName = string.Empty;
    private string _deviceUniqueId = string.Empty;
    private string _networkAddress = string.Empty;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public Guid DisplayTargetId
    {
        get => _displayTargetId;
        set => SetProperty(ref _displayTargetId, value);
    }

    public string DisplayTargetName
    {
        get => _displayTargetName;
        set => SetProperty(ref _displayTargetName, value);
    }

    public string DeviceUniqueId
    {
        get => _deviceUniqueId;
        set => SetProperty(ref _deviceUniqueId, value);
    }

    public string NetworkAddress
    {
        get => _networkAddress;
        set => SetProperty(ref _networkAddress, value);
    }
}

public sealed class TvProfileViewModel : ViewModelBase
{
    private Guid _id;
    private string _name = string.Empty;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public ObservableCollection<TvProfileTargetViewModel> Targets { get; } = new ObservableCollection<TvProfileTargetViewModel>();
}

public sealed class TvProfileTargetViewModel : ViewModelBase
{
    private Guid _displayTargetId;
    private string _displayName = string.Empty;
    private string _networkAddress = string.Empty;
    private string _deviceUniqueId = string.Empty;
    private string _macAddress = string.Empty;
    private string _discoverySource = string.Empty;
    private int _nativeWidth = 1920;
    private int _nativeHeight = 1080;

    public Guid DisplayTargetId
    {
        get => _displayTargetId;
        set => SetProperty(ref _displayTargetId, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string NetworkAddress
    {
        get => _networkAddress;
        set => SetProperty(ref _networkAddress, value);
    }

    public string DeviceUniqueId
    {
        get => _deviceUniqueId;
        set => SetProperty(ref _deviceUniqueId, value);
    }

    public string MacAddress
    {
        get => _macAddress;
        set => SetProperty(ref _macAddress, value);
    }

    public string DiscoverySource
    {
        get => _discoverySource;
        set => SetProperty(ref _discoverySource, value);
    }

    public int NativeWidth
    {
        get => _nativeWidth;
        set => SetProperty(ref _nativeWidth, value);
    }

    public int NativeHeight
    {
        get => _nativeHeight;
        set => SetProperty(ref _nativeHeight, value);
    }
}

public sealed class WindowProfileViewModel : ViewModelBase
{
    private Guid _id;
    private string _name = string.Empty;
    private Guid? _assignedTvProfileId;
    private string _assignedTvProfileName = string.Empty;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public Guid? AssignedTvProfileId
    {
        get => _assignedTvProfileId;
        set => SetProperty(ref _assignedTvProfileId, value);
    }

    public string AssignedTvProfileName
    {
        get => _assignedTvProfileName;
        set => SetProperty(ref _assignedTvProfileName, value);
    }

    public ObservableCollection<WindowProfileItemViewModel> Windows { get; } = new ObservableCollection<WindowProfileItemViewModel>();
}

public sealed class WindowProfileItemViewModel : ViewModelBase
{
    private Guid _id;
    private string _nickname = string.Empty;
    private string _url = string.Empty;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Nickname
    {
        get => _nickname;
        set => SetProperty(ref _nickname, value);
    }

    public string Url
    {
        get => _url;
        set => SetProperty(ref _url, value);
    }
}












