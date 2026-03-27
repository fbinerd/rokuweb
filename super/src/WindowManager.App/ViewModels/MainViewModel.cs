using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
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
    private readonly DisplayIdentityResolverService _displayIdentityResolverService;
    private readonly LocalWebRtcPublisherService _webRtcPublisherService;
    private readonly KnownDisplayStore _knownDisplayStore;
    private readonly AppUpdateManifestService _appUpdateManifestService;
    private readonly AppUpdatePreferenceStore _appUpdatePreferenceStore;
    private readonly AppSelfUpdateService _appSelfUpdateService;
    private readonly AppDataMaintenanceService _appDataMaintenanceService;
    private readonly AppInstallationSnapshotService _appInstallationSnapshotService;
    private readonly UpdateRollbackStore _updateRollbackStore;
    private readonly UpdateRecoveryService _updateRecoveryService;
    private readonly SemaphoreSlim _profileLoadSemaphore = new SemaphoreSlim(1, 1);

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
    private bool _showWindowPreviews = true;
    private AppUpdateCheckResult? _lastUpdateCheckResult;
    private string _additionalDiscoveryCidrs = string.Empty;
    private string _selectedUpdateChannel = UpdateChannelNames.Stable;
    private string _selectedSessionProfileName = "default";
    private bool _isRestoringActiveSessions;
    private bool _suppressActiveSessionPersistence;
    private bool _isInitializingStartup;
    private bool _isNormalizingWindowProfiles;
    private readonly Dictionary<Guid, DateTime> _streamKeepAliveAttemptUtc = new Dictionary<Guid, DateTime>();

    public MainViewModel(
        IBrowserInstanceHost browserInstanceHost,
        IDisplayDiscoveryService displayDiscoveryService,
        RoutingService routingService,
        ProfileStore profileStore,
        ActiveSessionStore activeSessionStore,
        ManualDisplayProbeService manualDisplayProbeService,
        DisplayIdentityResolverService displayIdentityResolverService,
        LocalWebRtcPublisherService webRtcPublisherService,
        KnownDisplayStore knownDisplayStore,
        AppUpdateManifestService appUpdateManifestService,
        AppUpdatePreferenceStore appUpdatePreferenceStore,
        AppSelfUpdateService appSelfUpdateService,
        AppDataMaintenanceService appDataMaintenanceService,
        AppInstallationSnapshotService appInstallationSnapshotService,
        UpdateRollbackStore updateRollbackStore,
        UpdateRecoveryService updateRecoveryService)
    {
        _browserInstanceHost = browserInstanceHost;
        _displayDiscoveryService = displayDiscoveryService;
        _routingService = routingService;
        _profileStore = profileStore;
        _activeSessionStore = activeSessionStore;
        _manualDisplayProbeService = manualDisplayProbeService;
        _displayIdentityResolverService = displayIdentityResolverService;
        _webRtcPublisherService = webRtcPublisherService;
        _knownDisplayStore = knownDisplayStore;
        _appUpdateManifestService = appUpdateManifestService;
        _appUpdatePreferenceStore = appUpdatePreferenceStore;
        _appSelfUpdateService = appSelfUpdateService;
        _appDataMaintenanceService = appDataMaintenanceService;
        _appInstallationSnapshotService = appInstallationSnapshotService;
        _updateRollbackStore = updateRollbackStore;
        _updateRecoveryService = updateRecoveryService;

        ResolutionModes = Enum.GetValues(typeof(RenderResolutionMode)).Cast<RenderResolutionMode>().ToArray();
        WebRtcBindModes = Enum.GetValues(typeof(WebRtcBindMode)).Cast<WebRtcBindMode>().ToArray();

        Windows.CollectionChanged += OnWindowsCollectionChanged;
        WindowProfiles.CollectionChanged += OnWindowProfilesInternalCollectionChanged;

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
        RollbackToPreviousVersionCommand = new AsyncRelayCommand(RollbackToPreviousVersionAsync);
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

    public ObservableCollection<BrowserProfileViewModel> BrowserProfiles { get; } = new ObservableCollection<BrowserProfileViewModel>();

    public ObservableCollection<WindowProfileViewModel> WindowProfiles { get; } = new ObservableCollection<WindowProfileViewModel>();

    public ObservableCollection<StaticDisplayPanelViewModel> StaticPanels { get; } = new ObservableCollection<StaticDisplayPanelViewModel>();

    public IReadOnlyList<RenderResolutionMode> ResolutionModes { get; }

    public IReadOnlyList<WebRtcBindMode> WebRtcBindModes { get; }

    public IReadOnlyList<string> UpdateChannels { get; } = new[] { UpdateChannelNames.Stable, UpdateChannelNames.Develop };

    public bool IsLocalDevelopmentBuild => string.Equals(BuildVersionInfo.CurrentBuildChannel, UpdateChannelNames.Local, StringComparison.OrdinalIgnoreCase);

    public bool IsAutomaticUpdateToggleEnabled => !IsLocalDevelopmentBuild;

    public bool ShowWindowPreviews
    {
        get => _showWindowPreviews;
        set
        {
            if (SetProperty(ref _showWindowPreviews, value))
            {
                QueueAutoSave();
            }
        }
    }

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
    public AsyncRelayCommand RollbackToPreviousVersionCommand { get; }
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
        _isInitializingStartup = true;
        LoadProfileCommand.RaiseCanExecuteChanged();
        try
        {
            await InitializePersistedStateWithRecoveryAsync();
            await Task.Delay(350);
            NormalizeWindowProfilesInMemory();
            RefreshWindowProfilesCollection();
            _ = CheckForAppUpdatesAsync();
        }
        finally
        {
            _isInitializingStartup = false;
            LoadProfileCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task<IReadOnlyList<DisplayTarget>> RefreshTargetsForSetupAsync()
    {
        await RefreshTargetsAsync();
        await _displayIdentityResolverService.ReconcileKnownDisplaysAsync(Targets, CancellationToken.None);
        return Targets.ToList();
    }

    public async Task<DisplayTarget> ResolveCurrentTargetForSetupAsync(DisplayTarget target)
    {
        return await _displayIdentityResolverService.ResolveCurrentTargetAsync(target, Targets, CancellationToken.None);
    }

    public async Task ExportApplicationDataAsync(string destinationZipPath)
    {
        await _appDataMaintenanceService.ExportAsync(destinationZipPath, CancellationToken.None);
        StatusMessage = string.Format("Backup salvo em '{0}'.", destinationZipPath);
    }

    public async Task ImportApplicationDataAsync(string sourceZipPath)
    {
        if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath))
        {
            throw new FileNotFoundException("Nao foi possivel localizar o backup informado.", sourceZipPath);
        }

        // Lê os perfis do backup zip sem sobrescrever arquivos diretamente
        var imported = LoadProfilesFromBackup(sourceZipPath);
        if (imported.Profiles.Count == 0)
        {
            throw new InvalidOperationException("O backup nao contem nenhum perfil valido para restauracao.");
        }

        // Limpa runtime e base local, mas não sobrescreve arquivos de perfil existentes
        await ResetRuntimeStateAsync();
        await _appDataMaintenanceService.ResetAsync(CancellationToken.None);

        // Migra e salva cada perfil individualmente
        foreach (var profile in imported.Profiles)
        {
            AppProfileMigrator.Migrate(profile);
            await _profileStore.SaveAsync(profile, CancellationToken.None);
        }

        // Define perfil padrão e de inicialização, se existirem
        if (!string.IsNullOrWhiteSpace(imported.DefaultProfileName))
        {
            await _profileStore.SetDefaultProfileNameAsync(imported.DefaultProfileName, CancellationToken.None);
        }
        else
        {
            await _profileStore.ClearDefaultProfileNameAsync(CancellationToken.None);
        }

        if (!string.IsNullOrWhiteSpace(imported.StartupProfileName))
        {
            await _profileStore.SetLastProfileNameAsync(imported.StartupProfileName, CancellationToken.None);
            ProfileName = imported.StartupProfileName;
        }
        else
        {
            ProfileName = imported.Profiles[0].Name;
        }

        await LoadPreferencesAsync();
        await ReloadPersistedStateAsync(refreshTargets: false);

        try
        {
            await RefreshTargetsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("Backup", string.Format("Falha ao atualizar destinos apos restaurar backup: {0}", ex));
            StatusMessage = string.Format(
                "Backup restaurado de '{0}'. A base foi carregada, mas a atualizacao de destinos falhou: {1}",
                sourceZipPath,
                ex.Message);
            return;
        }

        StatusMessage = string.Format(
            "Backup restaurado de '{0}'. Perfil carregado: {1}. TVs: {2}. Streams: {3}.",
            sourceZipPath,
            ProfileName,
            TvProfiles.Count,
            WindowProfiles.Count);
    }

    public async Task ResetApplicationDataAsync()
    {
        await ResetRuntimeStateAsync();
        await _appDataMaintenanceService.ResetAsync(CancellationToken.None);
        await LoadPreferencesAsync();
        await ReloadPersistedStateAsync(refreshTargets: false);

        try
        {
            await RefreshTargetsAsync();
            StatusMessage = "Base local do aplicativo resetada com sucesso.";
        }
        catch (Exception ex)
        {
            AppLog.Write("Reset", string.Format("Falha ao atualizar destinos apos reset: {0}", ex));
            StatusMessage = string.Format(
                "Base local do aplicativo resetada com sucesso. A atualizacao de destinos falhou: {0}",
                ex.Message);
        }
    }

    private async Task LoadPreferencesAsync()
    {
        var preferences = await _appUpdatePreferenceStore.LoadAsync(CancellationToken.None);
        AutoUpdateEnabled = IsLocalDevelopmentBuild ? false : preferences.AutoUpdateEnabled;
        SelectedUpdateChannel = preferences.UpdateChannel;
        AdditionalDiscoveryCidrs = preferences.AdditionalDiscoveryCidrs;
    }

    private async Task ReloadPersistedStateAsync(bool refreshTargets = true)
    {
        await RefreshProfileNamesAsync();

        var startupProfileName = await _profileStore.GetStartupProfileNameAsync(CancellationToken.None);
        ProfileName = startupProfileName;
        await LoadProfileAsync();
        SelectedSessionProfileName = ProfileName;
        if (refreshTargets)
        {
            await RefreshTargetsAsync();
        }

        NormalizeWindowProfilesInMemory();
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

        var expectedVersion = await ResolveExpectedRokuReleaseIdAsync();
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            UpdateStatusMessage = "Nao foi possivel determinar a release Roku esperada para atualizacao.";
            return;
        }

        var updatedCount = await _webRtcPublisherService.ForceUpdateConnectedDisplaysAsync(expectedVersion, CancellationToken.None);
        var attemptedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in Targets.Where(IsRokuUpdatableTarget))
        {
            var key = !string.IsNullOrWhiteSpace(target.DeviceUniqueId)
                ? target.DeviceUniqueId
                : target.NetworkAddress ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key) || !attemptedTargets.Add(key))
            {
                continue;
            }

            var result = await _webRtcPublisherService.ForceUpdateDisplayTargetAsync(target, expectedVersion, CancellationToken.None);
            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                updatedCount++;
            }
        }

        UpdateStatusMessage = updatedCount <= 0
            ? "Nenhuma TV Roku conectada precisava de atualizacao."
            : string.Format("{0} TV(s) Roku receberam sideload de atualizacao.", updatedCount);
    }

    private async Task PowerOnConnectedRokusAsync()
    {
        UpdateStatusMessage = "Ligando TVs Roku compativeis e abrindo o app...";
        UpdateStatusMessage = await SendPowerAndLaunchCommandToKnownRokusAsync();
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
            StatusMessage = "Selecione a TV base do perfil.";
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

        var entry = setup.IncludedTargets.First();
        var target = await EnsureTargetForTvProfileEntryAsync(entry);
        tvProfile.Targets.Add(new TvProfileTargetViewModel
        {
            DisplayTargetId = target.Id,
            DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? target.Name : entry.DisplayName,
            NetworkAddress = target.NetworkAddress,
            DeviceUniqueId = target.DeviceUniqueId,
            MacAddress = target.MacAddress,
            AlternateMacAddresses = target.AlternateMacAddresses.ToList(),
            DiscoverySource = target.DiscoverySource,
            NativeWidth = target.NativeWidth,
            NativeHeight = target.NativeHeight
        });
        tvProfile.NotifyTargetSummaryChanged();

        SelectedTvProfile = tvProfile;
        await PersistKnownTargetsAsync();
        await SaveProfileInternalAsync(updateStatus: false);
        StatusMessage = string.Format("Perfil de TV '{0}' salvo para a TV '{1}'.", tvProfile.Name, tvProfile.Targets.First().DisplayName);
    }

    public async Task ApplyWindowProfileSetupAsync(WindowProfileSetupViewModel setup)
    {
        var name = setup.ProfileName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Informe um nickname para o stream.";
            return;
        }

        var distinctWindows = setup.GetDistinctWindowDefinitions();
        if (distinctWindows.Count == 0)
        {
            StatusMessage = "Adicione pelo menos uma janela ao stream.";
            return;
        }

        if (setup.SelectedTvProfile is null)
        {
            StatusMessage = "Selecione um perfil de TV para transmitir.";
            return;
        }

        var browserProfileName = BrowserProfileStorage.NormalizeName(setup.SelectedBrowserProfileName);
        if (string.IsNullOrWhiteSpace(browserProfileName))
        {
            StatusMessage = "Selecione um perfil de navegador para o stream.";
            return;
        }

        if (!BrowserProfiles.Any(x => string.Equals(x.Name, browserProfileName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = string.Format("O perfil de navegador '{0}' nao existe mais. Crie-o novamente ou selecione outro.", browserProfileName);
            return;
        }

        var occupiedByAnotherStream = WindowProfiles.FirstOrDefault(x =>
            x.AssignedTvProfileId == setup.SelectedTvProfile.Id &&
            (!setup.EditingProfileId.HasValue || x.Id != setup.EditingProfileId.Value));

        if (occupiedByAnotherStream is not null)
        {
            StatusMessage = string.Format(
                "O perfil de TV '{0}' ja esta ocupado pelo stream '{1}'.",
                setup.SelectedTvProfile.Name,
                occupiedByAnotherStream.Name);
            return;
        }

        var browserProfileOccupiedByAnotherStream = WindowProfiles.FirstOrDefault(x =>
            string.Equals(x.BrowserProfileName, browserProfileName, StringComparison.OrdinalIgnoreCase) &&
            (!setup.EditingProfileId.HasValue || x.Id != setup.EditingProfileId.Value));

        if (browserProfileOccupiedByAnotherStream is not null)
        {
            StatusMessage = string.Format(
                "O perfil de navegador '{0}' ja esta em uso pelo stream '{1}'.",
                browserProfileName,
                browserProfileOccupiedByAnotherStream.Name);
            return;
        }

        var windowProfile = setup.EditingProfileId.HasValue
            ? WindowProfiles.FirstOrDefault(x => x.Id == setup.EditingProfileId.Value)
            : null;

        if (windowProfile is null)
        {
            windowProfile = WindowProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        if (windowProfile is null)
        {
            windowProfile = new WindowProfileViewModel
            {
                Id = Guid.NewGuid()
            };
            WindowProfiles.Add(windowProfile);
        }

        var browserProfileChanged = !string.Equals(windowProfile.BrowserProfileName, browserProfileName, StringComparison.OrdinalIgnoreCase);
        windowProfile.Name = name;
        windowProfile.AssignedTvProfileId = setup.SelectedTvProfile.Id;
        windowProfile.AssignedTvProfileName = setup.SelectedTvProfile.Name;
        windowProfile.BrowserProfileName = browserProfileName;

        var desiredWindows = distinctWindows
            .Select(window => new
            {
                Id = window.Id == Guid.Empty ? Guid.NewGuid() : window.Id,
                Nickname = window.Nickname,
                Url = window.Url,
                IsEnabled = window.IsEnabled,
                IsPrimaryExclusive = window.IsPrimaryExclusive,
                IsNavigationBarEnabled = window.IsNavigationBarEnabled,
                StreamingMode = StreamingModeOptions.Normalize(window.StreamingMode)
            })
            .ToList();

        var exclusiveWindow = desiredWindows.FirstOrDefault(x => x.IsPrimaryExclusive);
        if (exclusiveWindow is not null)
        {
            desiredWindows = desiredWindows
                .Select(x => new
                {
                    x.Id,
                    x.Nickname,
                    x.Url,
                    IsEnabled = x.Id == exclusiveWindow.Id,
                    IsPrimaryExclusive = x.Id == exclusiveWindow.Id,
                    x.IsNavigationBarEnabled,
                    x.StreamingMode
                })
                .ToList();
        }

        var desiredWindowIds = desiredWindows.Select(x => x.Id).ToHashSet();
        var streamingModeChanged = desiredWindows.Any(desiredWindow =>
        {
            var existingWindow = windowProfile.Windows.FirstOrDefault(x => x.Id == desiredWindow.Id);
            if (existingWindow is null)
            {
                return false;
            }

            return !string.Equals(
                StreamingModeOptions.Normalize(existingWindow.StreamingMode),
                desiredWindow.StreamingMode,
                StringComparison.OrdinalIgnoreCase);
        });
        var windowsToRemove = windowProfile.Windows
            .Where(existing => !desiredWindowIds.Contains(existing.Id))
            .ToList();

        foreach (var removedWindow in windowsToRemove)
        {
            windowProfile.Windows.Remove(removedWindow);
        }

        foreach (var desiredWindow in desiredWindows)
        {
            var existingWindow = windowProfile.Windows.FirstOrDefault(x => x.Id == desiredWindow.Id);
            if (existingWindow is null)
            {
                windowProfile.Windows.Add(new WindowProfileItemViewModel
                {
                    Id = desiredWindow.Id,
                    Nickname = desiredWindow.Nickname,
                    Url = desiredWindow.Url,
                    IsEnabled = desiredWindow.IsEnabled,
                    IsPrimaryExclusive = desiredWindow.IsPrimaryExclusive,
                    IsNavigationBarEnabled = desiredWindow.IsNavigationBarEnabled,
                    StreamingMode = desiredWindow.StreamingMode
                });
                continue;
            }

            existingWindow.Nickname = desiredWindow.Nickname;
            existingWindow.Url = desiredWindow.Url;
            existingWindow.IsEnabled = desiredWindow.IsEnabled;
            existingWindow.IsPrimaryExclusive = desiredWindow.IsPrimaryExclusive;
            existingWindow.IsNavigationBarEnabled = desiredWindow.IsNavigationBarEnabled;
            existingWindow.StreamingMode = desiredWindow.StreamingMode;
        }

        SelectedWindowProfile = windowProfile;
        NormalizeWindowProfilesInMemory();
        if (browserProfileChanged || streamingModeChanged)
        {
            await ResetWindowProfileRuntimeAsync(windowProfile);
        }
        await SyncWindowProfileRuntimeAsync(windowProfile);
        await SaveProfileInternalAsync(updateStatus: false);
        StatusMessage = string.Format(
            "Stream '{0}' salvo e vinculado ao perfil de TV '{1}'.",
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
        StatusMessage = string.Format("Stream '{0}' removido.", removedName);
    }

    private async Task RollbackToPreviousVersionAsync()
    {
        UpdateStatusMessage = "Preparando rollback para a versao anterior...";
        var latestRollback = await _updateRollbackStore.GetLatestAsync(CancellationToken.None);
        if (latestRollback is null)
        {
            UpdateStatusMessage = "Nenhum rollback disponivel nesta maquina.";
            return;
        }

        if (string.IsNullOrWhiteSpace(latestRollback.AppSnapshotZipPath) || !File.Exists(latestRollback.AppSnapshotZipPath))
        {
            UpdateStatusMessage = "O snapshot da versao anterior nao foi encontrado para rollback.";
            await _updateRollbackStore.RemoveAsync(latestRollback, CancellationToken.None);
            return;
        }

        var rollbackResult = await _appSelfUpdateService.PrepareLocalPackageAsync(
            latestRollback.AppSnapshotZipPath,
            latestRollback.BaseBackupZipPath,
            CancellationToken.None);

        if (!rollbackResult.Succeeded)
        {
            UpdateStatusMessage = rollbackResult.Message;
            AppLog.Write("Updater", rollbackResult.Message);
            return;
        }

        await _updateRollbackStore.RemoveAsync(latestRollback, CancellationToken.None);
        UpdateStatusMessage = string.Format(
            "Rollback preparado para a release {0}. Reiniciando aplicativo...",
            latestRollback.PreviousReleaseId);
        AppLog.Write("Updater", UpdateStatusMessage);

        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            Application.Current.Shutdown();
        }));
    }

    public async Task<BrowserProfileMutationResult> CreateBrowserProfileAsync(string profileName, Guid? editingStreamId)
    {
        var normalizedName = BrowserProfileStorage.NormalizeName(profileName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return BrowserProfileMutationResult.Fail("Informe um nome para o perfil de navegador.");
        }

        if (BrowserProfiles.Any(x => string.Equals(x.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return BrowserProfileMutationResult.Fail(string.Format("Ja existe um perfil de navegador chamado '{0}'.", normalizedName));
        }

        var occupiedByAnotherStream = WindowProfiles.FirstOrDefault(x =>
            string.Equals(x.BrowserProfileName, normalizedName, StringComparison.OrdinalIgnoreCase) &&
            (!editingStreamId.HasValue || x.Id != editingStreamId.Value));
        if (occupiedByAnotherStream is not null)
        {
            return BrowserProfileMutationResult.Fail(string.Format(
                "O perfil de navegador '{0}' ja esta vinculado ao stream '{1}'.",
                normalizedName,
                occupiedByAnotherStream.Name));
        }

        BrowserProfileStorage.EnsureProfileDirectory(normalizedName);
        BrowserProfiles.Add(new BrowserProfileViewModel
        {
            Name = normalizedName
        });
        await SaveProfileInternalAsync(updateStatus: false);
        return BrowserProfileMutationResult.Success(normalizedName, string.Format("Perfil de navegador '{0}' criado.", normalizedName));
    }

    public async Task<BrowserProfileMutationResult> DeleteBrowserProfileAsync(string profileName, Guid? editingStreamId)
    {
        var normalizedName = BrowserProfileStorage.NormalizeName(profileName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return BrowserProfileMutationResult.Fail("Selecione um perfil de navegador para excluir.");
        }

        var occupiedByAnotherStream = WindowProfiles.FirstOrDefault(x =>
            string.Equals(x.BrowserProfileName, normalizedName, StringComparison.OrdinalIgnoreCase) &&
            (!editingStreamId.HasValue || x.Id != editingStreamId.Value));
        if (occupiedByAnotherStream is not null)
        {
            return BrowserProfileMutationResult.Fail(string.Format(
                "O perfil de navegador '{0}' esta em uso pelo stream '{1}'.",
                normalizedName,
                occupiedByAnotherStream.Name));
        }

        var existingProfile = BrowserProfiles.FirstOrDefault(x => string.Equals(x.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (existingProfile is null)
        {
            return BrowserProfileMutationResult.Fail(string.Format("O perfil de navegador '{0}' nao foi encontrado.", normalizedName));
        }

        var affectedStreams = WindowProfiles
            .Where(x => string.Equals(x.BrowserProfileName, normalizedName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var affectedStream in affectedStreams)
        {
            affectedStream.BrowserProfileName = string.Empty;
            foreach (var item in affectedStream.Windows)
            {
                item.IsEnabled = false;
                item.IsPrimaryExclusive = false;
            }

            await ResetWindowProfileRuntimeAsync(affectedStream);
        }

        BrowserProfiles.Remove(existingProfile);
        BrowserProfileStorage.DeleteProfileDirectory(normalizedName);
        RebuildActiveSessionsFromWindows();
        UpdateBridgeSnapshot();
        await PersistActiveSessionsAsync();
        await SaveProfileInternalAsync(updateStatus: false);
        return BrowserProfileMutationResult.Success(normalizedName, string.Format("Perfil de navegador '{0}' excluido.", normalizedName));
    }

    private async Task CreateSessionFromWindowProfileAsync()
    {
        if (SelectedWindowProfile is null)
        {
            StatusMessage = "Selecione um stream.";
            return;
        }

        var windowProfile = SelectedWindowProfile;
        var tvProfile = TvProfiles.FirstOrDefault(x => x.Id == windowProfile.AssignedTvProfileId);
        if (tvProfile is null)
        {
            StatusMessage = string.Format(
                "O stream '{0}' nao possui um perfil de TV valido vinculado.",
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

            var browserWindow = await _browserInstanceHost.CreateAsync(
                initialUri ?? new Uri("about:blank"),
                CancellationToken.None,
                windowDefinition.Id,
                windowDefinition.Nickname);
            browserWindow.Title = string.IsNullOrWhiteSpace(windowDefinition.Nickname) ? browserWindow.Title : windowDefinition.Nickname;
            browserWindow.InitialUri = initialUri;
            browserWindow.State = WindowSessionState.Created;
            browserWindow.BrowserProfileName = windowProfile.BrowserProfileName ?? string.Empty;
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
            var binding = tvProfile.Targets.FirstOrDefault();
            if (binding is not null)
            {
                var resolvedBindingTarget = await _displayIdentityResolverService.ResolveCurrentTargetAsync(
                    new DisplayTarget
                    {
                        Id = binding.DisplayTargetId,
                        Name = binding.DisplayName,
                        NetworkAddress = binding.NetworkAddress,
                        DeviceUniqueId = binding.DeviceUniqueId,
                        MacAddress = binding.MacAddress,
                        AlternateMacAddresses = binding.AlternateMacAddresses.ToList(),
                        DiscoverySource = binding.DiscoverySource,
                        NativeWidth = binding.NativeWidth,
                        NativeHeight = binding.NativeHeight,
                        TransportKind = DisplayTransportKind.LanStreaming,
                        IsStaticTarget = true
                    },
                    Targets,
                    CancellationToken.None);

                if (!SelectedActiveSession.BoundDisplays.Any(x => x.DisplayTargetId == binding.DisplayTargetId))
                {
                    SelectedActiveSession.BoundDisplays.Add(new ActiveSessionDisplayBindingViewModel
                    {
                        DisplayTargetId = resolvedBindingTarget.Id != Guid.Empty ? resolvedBindingTarget.Id : binding.DisplayTargetId,
                        DisplayName = string.IsNullOrWhiteSpace(resolvedBindingTarget.Name) ? binding.DisplayName : resolvedBindingTarget.Name,
                        NetworkAddress = string.IsNullOrWhiteSpace(resolvedBindingTarget.NetworkAddress) ? binding.NetworkAddress : resolvedBindingTarget.NetworkAddress,
                        DeviceUniqueId = string.IsNullOrWhiteSpace(resolvedBindingTarget.DeviceUniqueId) ? binding.DeviceUniqueId : resolvedBindingTarget.DeviceUniqueId,
                        BindingName = tvProfile.Name
                    });
                }
            }

            SelectedActiveSession.TvCount = SelectedActiveSession.BoundDisplays.Count;
            SelectedActiveSession.BindingCount = SelectedActiveSession.BoundDisplays.Count;
        }

        StatusMessage = string.Format(
            "Sessao '{0}' criada a partir do stream '{1}' com o perfil de TV '{2}'.",
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
            browserWindow.IsNavigationBarEnabled = persistedWindow.IsNavigationBarEnabled;
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

        var expectedVersion = await ResolveExpectedRokuReleaseIdAsync();
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            StatusMessage = "Nao foi possivel determinar a release Roku esperada para atualizacao.";
            return;
        }

        StatusMessage = string.Format("Enviando atualizacao para a TV '{0}'...", SelectedTarget.Name);
        var result = await _webRtcPublisherService.ForceUpdateDisplayTargetAsync(SelectedTarget, expectedVersion, CancellationToken.None);
        StatusMessage = string.Format("Resultado da atualizacao da TV '{0}': {1}", SelectedTarget.Name, result);
        AppLog.Write(
            "RokuDeploy",
            string.Format(
                "Resultado da atualizacao direta da TV descoberta '{0}' ({1}): {2}",
                SelectedTarget.Name,
                SelectedTarget.NetworkAddress,
                result));
    }

    private async Task<string> ResolveExpectedRokuReleaseIdAsync()
    {
        if (IsLocalDevelopmentBuild)
        {
            return BuildVersionInfo.ReleaseId;
        }

        if (_lastUpdateCheckResult is not null &&
            _lastUpdateCheckResult.Succeeded &&
            string.Equals(
                AppUpdateManifestService.BuildManifestUrl(SelectedUpdateChannel),
                _lastUpdateCheckResult.ManifestUrl,
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_lastUpdateCheckResult.LatestReleaseId))
        {
            return _lastUpdateCheckResult.LatestReleaseId;
        }

        var result = await _appUpdateManifestService.CheckForUpdateAsync(SelectedUpdateChannel, CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(result.LatestReleaseId))
        {
            return result.LatestReleaseId;
        }

        return BuildVersionInfo.ReleaseId;
    }

    private static bool IsRokuUpdatableTarget(DisplayTarget target)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.NetworkAddress))
        {
            return false;
        }

        var transportName = target.TransportKind.ToString();
        return target.Name.IndexOf("roku", StringComparison.OrdinalIgnoreCase) >= 0 ||
               (!string.IsNullOrWhiteSpace(target.DeviceUniqueId) && target.DeviceUniqueId.IndexOf("roku-", StringComparison.OrdinalIgnoreCase) >= 0) ||
               transportName.IndexOf("Lan", StringComparison.OrdinalIgnoreCase) >= 0;
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

        string? automaticBackupPath = null;
        string? appSnapshotPath = null;
        try
        {
            automaticBackupPath = _updateRecoveryService.BuildAutomaticBackupPath(result.LatestReleaseId);
            await _appDataMaintenanceService.ExportAsync(automaticBackupPath, CancellationToken.None);
            appSnapshotPath = _appInstallationSnapshotService.BuildSnapshotPath(BuildVersionInfo.ReleaseId);
            await _appInstallationSnapshotService.ExportCurrentInstallationAsync(appSnapshotPath, CancellationToken.None);
            await _updateRecoveryService.SavePendingAsync(
                new PendingUpdateRecoveryRecord
                {
                    BackupZipPath = automaticBackupPath,
                    ReleaseId = result.LatestReleaseId,
                    CreatedAtUtc = DateTime.UtcNow.ToString("O")
                },
                CancellationToken.None);
            await _updateRollbackStore.AddAsync(
                new UpdateRollbackRecord
                {
                    PreviousReleaseId = BuildVersionInfo.ReleaseId,
                    PreviousVersion = BuildVersionInfo.Version,
                    BaseBackupZipPath = automaticBackupPath,
                    AppSnapshotZipPath = appSnapshotPath,
                    CreatedAtUtc = DateTime.UtcNow.ToString("O")
                },
                CancellationToken.None);
            AppLog.Write("Updater", string.Format("Backup automatico criado antes do update: {0}", automaticBackupPath));
            AppLog.Write("Updater", string.Format("Snapshot da aplicacao criado antes do update: {0}", appSnapshotPath));
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = string.Format("{0}: falha ao criar backup automatico antes do update: {1}", actionLabel, ex.Message);
            AppLog.Write("Updater", UpdateStatusMessage);
            return;
        }

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

    private async Task InitializePersistedStateWithRecoveryAsync()
    {
        await LoadPreferencesAsync();

        try
        {
            await ReloadPersistedStateAsync(refreshTargets: false);
            await TryRefreshTargetsSilentlyAsync();
            await _updateRecoveryService.ClearPendingAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Write("Updater", string.Format("Falha ao carregar base atual: {0}", ex.Message));
            var recovered = await TryRestorePendingBackupAsync(ex);
            if (!recovered)
            {
                throw;
            }
        }
    }

    private async Task<bool> TryRestorePendingBackupAsync(Exception startupException)
    {
        var pending = await _updateRecoveryService.LoadPendingAsync(CancellationToken.None);
        var backupZipPath = pending?.BackupZipPath;
        if (string.IsNullOrWhiteSpace(backupZipPath) || !File.Exists(backupZipPath))
        {
            var latestRollback = await _updateRollbackStore.GetLatestAsync(CancellationToken.None);
            backupZipPath = latestRollback?.BaseBackupZipPath;
        }

        if (string.IsNullOrWhiteSpace(backupZipPath) || !File.Exists(backupZipPath))
        {
            return false;
        }

        AppLog.Write("Updater", string.Format("Tentando restaurar backup automatico apos falha de startup: {0}", backupZipPath));

        await ResetRuntimeStateAsync();
        await _appDataMaintenanceService.ImportAsync(backupZipPath, CancellationToken.None);
        await LoadPreferencesAsync();
        await ReloadPersistedStateAsync(refreshTargets: false);
        await TryRefreshTargetsSilentlyAsync();
        await _updateRecoveryService.ClearPendingAsync(CancellationToken.None);

        StatusMessage = string.Format(
            "A base foi restaurada automaticamente do backup '{0}' apos falha na atualizacao: {1}",
            backupZipPath,
            startupException.Message);

        AppLog.Write("Updater", "Backup automatico restaurado com sucesso apos falha de startup.");
        return true;
    }

    private async Task TryRefreshTargetsSilentlyAsync()
    {
        try
        {
            await RefreshTargetsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("Startup", string.Format("Falha ao atualizar destinos apos carregar a base: {0}", ex));
        }
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
        var targets = (await _displayDiscoveryService.DiscoverAsync(CancellationToken.None))?.ToList()
            ?? new List<DisplayTarget>();
        await _displayIdentityResolverService.ReconcileKnownDisplaysAsync(targets, CancellationToken.None);

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

    private async Task<string> SendPowerAndLaunchCommandToKnownRokusAsync()
    {
        var rokuTargets = Targets
            .Where(IsRokuUpdatableTarget)
            .GroupBy(x => !string.IsNullOrWhiteSpace(x.DeviceUniqueId) ? x.DeviceUniqueId : x.NetworkAddress ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        foreach (var registered in _webRtcPublisherService.GetRegisteredDisplaysSnapshot())
        {
            var key = !string.IsNullOrWhiteSpace(registered.DeviceId) ? registered.DeviceId : registered.NetworkAddress;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (rokuTargets.Any(x =>
                    (!string.IsNullOrWhiteSpace(x.DeviceUniqueId) &&
                     string.Equals(x.DeviceUniqueId, registered.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(x.NetworkAddress) &&
                     string.Equals(x.NetworkAddress, registered.NetworkAddress, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            rokuTargets.Add(new DisplayTarget
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(registered.DeviceModel) ? "Roku TV" : registered.DeviceModel,
                NetworkAddress = registered.NetworkAddress,
                DeviceUniqueId = registered.DeviceId,
                TransportKind = DisplayTransportKind.LanStreaming,
                IsOnline = true
            });
        }

        if (rokuTargets.Count == 0)
        {
            return "Nenhuma TV Roku compativel foi encontrada para ligar e abrir o app.";
        }

        var successCount = 0;
        var failureCount = 0;

        foreach (var target in rokuTargets)
        {
            var appAlreadyConnected = _webRtcPublisherService.IsDisplayRegisteredRecently(target, TimeSpan.FromSeconds(8));
            string result;

            if (appAlreadyConnected)
            {
                result = await _webRtcPublisherService.SendPowerCommandToDisplayTargetAsync(target, true, CancellationToken.None);
            }
            else
            {
                result = await _webRtcPublisherService.EnsureDisplayAppRunningAsync(target, requirePowerOn: true, CancellationToken.None);
            }

            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(result, "ok_fallback_Power", StringComparison.OrdinalIgnoreCase))
            {
                successCount++;
            }
            else
            {
                failureCount++;
            }
        }

        return string.Format(
            "Comando de ligar e abrir app concluido. Alvo(s): {0}, sucesso(s): {1}, falha(s): {2}.",
            rokuTargets.Count,
            successCount,
            failureCount);
    }

    private bool CanAssignSelectedWindow() => SelectedWindow is not null && SelectedTarget is not null;
    private bool CanNavigateSelectedWindow() => SelectedWindow is not null && !string.IsNullOrWhiteSpace(BrowserUrlInput);
    private bool CanUseProfileName() => !string.IsNullOrWhiteSpace(ProfileName) && !_isInitializingStartup && !_isApplyingProfile;
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
        await _profileLoadSemaphore.WaitAsync();
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            StatusMessage = "Informe um nome de perfil valido para carregar.";
            _profileLoadSemaphore.Release();
            return;
        }

        var profile = await _profileStore.LoadAsync(ProfileName, CancellationToken.None);
        if (profile is null)
        {
            StatusMessage = string.Format("Perfil '{0}' ainda nao existe. Um novo sera criado quando voce salvar.", ProfileName);
            await SyncDefaultProfileSelectionAsync();
            _profileLoadSemaphore.Release();
            return;
        }

        if (await TryImportLegacyActiveSessionsAsync(profile))
        {
            await _profileStore.SaveAsync(profile, CancellationToken.None);
        }

        _isApplyingProfile = true;
        _suppressActiveSessionPersistence = true;
        try
        {
            await UnloadCurrentProfileStateAsync();

            var restoredSessionId = Guid.NewGuid();
            var restoredSessionName = profile.Name;
            ShowWindowPreviews = profile.ShowWindowPreviews;
            WebRtcServerPort = profile.WebRtcServerPort <= 0 ? 8090 : profile.WebRtcServerPort;
            WebRtcBindMode = profile.WebRtcBindMode;
            WebRtcSpecificIp = profile.WebRtcSpecificIp ?? string.Empty;
            ApplyProfileTargets(profile);

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
                    IsNavigationBarEnabled = persistedWindow.IsNavigationBarEnabled,
                    BrowserProfileName = persistedWindow.BrowserProfileName ?? string.Empty,
                    StreamingMode = StreamingModeOptions.Normalize(persistedWindow.StreamingMode),
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

            RestoreProfileDisplayBindings(profile);
            RestoreTvProfiles(profile);
            RestoreBrowserProfiles(profile);
            RestoreWindowProfiles(profile);
            NormalizeWindowProfilesInMemory();
            if (profile.ActiveSessions.Count > 0)
            {
                await RestoreActiveSessionsAsync(profile.ActiveSessions);
            }
            await RefreshProfileNamesAsync();
            await SyncDefaultProfileSelectionAsync();
            if (profile.ActiveSessions.Count == 0)
            {
                foreach (var windowProfile in WindowProfiles.ToList())
                {
                    await SyncWindowProfileRuntimeAsync(windowProfile);
                }

                RebuildActiveSessionsFromWindows();
            }
            StatusMessage = string.Format("Perfil '{0}' restaurado com sucesso. As rotas WebRTC podem ser atualizadas manualmente apos a abertura.", ProfileName);
            UpdateBridgeSnapshot();
        }
        finally
        {
            _isApplyingProfile = false;
            _suppressActiveSessionPersistence = false;
            _profileLoadSemaphore.Release();
        }
    }

    private async Task UnloadCurrentProfileStateAsync()
    {
        foreach (var window in Windows)
        {
            window.PropertyChanged -= OnWindowPropertyChanged;
        }

        await ClearRuntimeWindowsAsync();
        StaticPanels.Clear();
        ProfileDisplayBindings.Clear();
        TvProfiles.Clear();
        WindowProfiles.Clear();
        ActiveSessions.Clear();
        Targets.Clear();
        SelectedTarget = null;
        SelectedStaticPanel = null;
    }

    private async Task RestoreActiveSessionsAsync(IReadOnlyList<ActiveSessionRecord> records)
    {
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

                foreach (var bindingRecord in record.BoundDisplays ?? new List<ActiveSessionDisplayBindingRecord>())
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

                foreach (var windowRecord in record.Windows ?? new List<ActiveSessionWindowRecord>())
                {
                    Uri? initialUri = null;
                    if (!string.IsNullOrWhiteSpace(windowRecord.InitialUrl))
                    {
                        Uri.TryCreate(windowRecord.InitialUrl, UriKind.Absolute, out initialUri);
                    }

                    var browserWindow = await _browserInstanceHost.CreateAsync(
                        initialUri ?? new Uri("about:blank"),
                        CancellationToken.None,
                        windowRecord.Id == Guid.Empty ? null : windowRecord.Id,
                        string.IsNullOrWhiteSpace(windowRecord.Title) ? null : windowRecord.Title);
                    var assignedTarget = Targets.FirstOrDefault(x => x.Id == windowRecord.AssignedTargetId);
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
                    browserWindow.IsPrimaryExclusive = windowRecord.IsPrimaryExclusive;
                    browserWindow.IsNavigationBarEnabled = windowRecord.IsNavigationBarEnabled;
                    browserWindow.BrowserProfileName = windowRecord.BrowserProfileName ?? string.Empty;
                    browserWindow.StreamingMode = StreamingModeOptions.Normalize(windowRecord.StreamingMode);
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

        var records = BuildActiveSessionRecords();

        await _activeSessionStore.SaveAsync(records, CancellationToken.None);
    }

    private List<ActiveSessionRecord> BuildActiveSessionRecords()
    {
        return ActiveSessions.Select(session => new ActiveSessionRecord
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
                    IsWebRtcPublishingEnabled = window.IsWebRtcPublishingEnabled,
                    IsPrimaryExclusive = window.IsPrimaryExclusive,
                    IsNavigationBarEnabled = window.IsNavigationBarEnabled,
                    BrowserProfileName = window.BrowserProfileName ?? string.Empty,
                    StreamingMode = StreamingModeOptions.Normalize(window.StreamingMode)
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
            ShowWindowPreviews = ShowWindowPreviews,
            DisplayTargets = Targets.Select(x => new DisplayTargetProfile
            {
                Id = x.Id,
                Name = x.Name,
                NetworkAddress = x.NetworkAddress,
                LastKnownNetworkAddress = x.LastKnownNetworkAddress,
                MacAddress = x.MacAddress,
                AlternateMacAddresses = x.AlternateMacAddresses.ToList(),
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
                IsNavigationBarEnabled = x.IsNavigationBarEnabled,
                BrowserProfileName = x.BrowserProfileName ?? string.Empty,
                StreamingMode = StreamingModeOptions.Normalize(x.StreamingMode),
                ProfileName = x.ProfileName,
                ActiveSessionId = x.ActiveSessionId,
                ActiveSessionName = x.ActiveSessionName
            }).ToList(),
            DisplayBindings = BuildProfileDisplayBindings(),
            TvProfiles = BuildTvProfiles(),
            WindowProfiles = BuildWindowProfiles(),
            ActiveSessions = BuildActiveSessionRecords(),
            BrowserProfiles = BuildBrowserProfiles(),
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
                    AlternateMacAddresses = targetProfile.AlternateMacAddresses.ToList(),
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
            target.AlternateMacAddresses = targetProfile.AlternateMacAddresses.ToList();
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
            AlternateMacAddresses = x.AlternateMacAddresses.ToList(),
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
                AlternateMacAddresses = target.AlternateMacAddresses.ToList(),
                DiscoverySource = target.DiscoverySource,
                NativeWidth = target.NativeWidth,
                NativeHeight = target.NativeHeight
            }).ToList()
        }).ToList();
    }

    private List<WindowGroupProfile> BuildWindowProfiles()
    {
        var emittedIds = new HashSet<Guid>();
        var emittedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return WindowProfiles
            .Where(profile =>
            {
                if (profile.Id != Guid.Empty)
                {
                    return emittedIds.Add(profile.Id);
                }

                var name = profile.Name?.Trim() ?? string.Empty;
                return emittedNames.Add(name);
            })
            .Select(profile => new WindowGroupProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                AssignedTvProfileId = profile.AssignedTvProfileId,
                AssignedTvProfileName = profile.AssignedTvProfileName,
                KeepDisplayConnected = profile.KeepDisplayConnected,
                BrowserProfileName = profile.BrowserProfileName ?? string.Empty,
                Windows = profile.Windows.Select(window => new WindowLinkProfile
                {
                    Id = window.Id,
                    Nickname = window.Nickname,
                    Url = window.Url,
                    IsEnabled = window.IsEnabled,
                    IsPrimaryExclusive = window.IsPrimaryExclusive,
                    IsNavigationBarEnabled = window.IsNavigationBarEnabled,
                    StreamingMode = StreamingModeOptions.Normalize(window.StreamingMode)
                }).ToList()
            }).ToList();
    }

    private void RestoreTvProfiles(AppProfile profile)
    {
        TvProfiles.Clear();

        foreach (var tvProfile in (profile.TvProfiles ?? new List<TvProfileDefinition>()).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var viewModel = new TvProfileViewModel
            {
                Id = tvProfile.Id == Guid.Empty ? Guid.NewGuid() : tvProfile.Id,
                Name = tvProfile.Name
            };

            foreach (var target in tvProfile.Targets ?? new List<TvProfileTargetDefinition>())
            {
                viewModel.Targets.Add(new TvProfileTargetViewModel
                {
                    DisplayTargetId = target.DisplayTargetId,
                    DisplayName = target.DisplayName,
                    NetworkAddress = target.NetworkAddress,
                    DeviceUniqueId = target.DeviceUniqueId,
                    MacAddress = target.MacAddress,
                    AlternateMacAddresses = target.AlternateMacAddresses.ToList(),
                    DiscoverySource = target.DiscoverySource,
                    NativeWidth = target.NativeWidth,
                    NativeHeight = target.NativeHeight
                });
            }

            viewModel.NotifyTargetSummaryChanged();

            TvProfiles.Add(viewModel);
        }

        SelectedTvProfile = TvProfiles.FirstOrDefault();
    }

    private static ImportedBackupProfiles LoadProfilesFromBackup(string sourceZipPath)
    {
        var serializer = new DataContractJsonSerializer(typeof(AppProfile));
        var result = new ImportedBackupProfiles();

        using var archive = ZipFile.OpenRead(sourceZipPath);

        foreach (var entry in archive.Entries
                     .Where(x => x.FullName.StartsWith("Profiles/", StringComparison.OrdinalIgnoreCase) &&
                                 x.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = entry.Open();
            var profile = serializer.ReadObject(stream) as AppProfile;
            if (profile is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                profile.Name = Path.GetFileNameWithoutExtension(entry.FullName);
            }

            result.Profiles.Add(profile);
        }

        result.StartupProfileName = ReadTextEntry(archive, "last-profile.txt");
        result.DefaultProfileName = ReadTextEntry(archive, "default-profile.txt");

        if (string.IsNullOrWhiteSpace(result.StartupProfileName) && result.Profiles.Count > 0)
        {
            result.StartupProfileName = result.Profiles[0].Name;
        }

        return result;
    }

    private static string ReadTextEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.Entries.FirstOrDefault(x => string.Equals(x.FullName, entryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return string.Empty;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    private void RestoreBrowserProfiles(AppProfile profile)
    {
        BrowserProfiles.Clear();

        var profileNames = (profile.BrowserProfiles ?? new List<BrowserProfileDefinition>())
            .Select(x => x.Name)
            .Concat((profile.WindowProfiles ?? new List<WindowGroupProfile>()).Select(x => x.BrowserProfileName))
            .Concat((profile.Windows ?? new List<WindowSessionProfile>()).Select(x => x.BrowserProfileName))
            .Concat((profile.ActiveSessions ?? new List<ActiveSessionRecord>()).SelectMany(x => x.Windows ?? new List<ActiveSessionWindowRecord>()).Select(x => x.BrowserProfileName))
            .Select(BrowserProfileStorage.NormalizeName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var profileName in profileNames)
        {
            BrowserProfiles.Add(new BrowserProfileViewModel
            {
                Name = profileName
            });
        }
    }

    private async Task<bool> TryImportLegacyActiveSessionsAsync(AppProfile profile)
    {
        if ((profile.ActiveSessions?.Count ?? 0) > 0)
        {
            return false;
        }

        var legacyRecords = await _activeSessionStore.LoadAsync(CancellationToken.None);
        if (legacyRecords.Count == 0)
        {
            return false;
        }

        var windowProfiles = profile.WindowProfiles ?? new List<WindowGroupProfile>();
        var windowProfileIds = new HashSet<Guid>(windowProfiles.Select(x => x.Id));
        var windowProfileNames = new HashSet<string>(
            windowProfiles.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase);

        var matchingRecords = legacyRecords
            .Where(x =>
                windowProfileIds.Contains(x.Id) ||
                windowProfileNames.Contains(x.Name) ||
                windowProfileNames.Contains(x.ProfileName))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matchingRecords.Count == 0)
        {
            return false;
        }

        profile.ActiveSessions = matchingRecords;
        return AppProfileMigrator.Migrate(profile);
    }

    private void RestoreWindowProfiles(AppProfile profile)
    {
        WindowProfiles.Clear();
        var restoredIds = new HashSet<Guid>();
        var restoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var windowProfile in (profile.WindowProfiles ?? new List<WindowGroupProfile>()).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var normalizedName = windowProfile.Name?.Trim() ?? string.Empty;
            if (windowProfile.Id != Guid.Empty)
            {
                if (!restoredIds.Add(windowProfile.Id))
                {
                    continue;
                }
            }
            else if (!restoredNames.Add(normalizedName))
            {
                continue;
            }

            var viewModel = new WindowProfileViewModel
            {
                Id = windowProfile.Id == Guid.Empty ? Guid.NewGuid() : windowProfile.Id,
                Name = windowProfile.Name ?? string.Empty,
                AssignedTvProfileId = windowProfile.AssignedTvProfileId,
                AssignedTvProfileName = windowProfile.AssignedTvProfileName,
                KeepDisplayConnected = windowProfile.KeepDisplayConnected,
                BrowserProfileName = windowProfile.BrowserProfileName ?? string.Empty
            };

            foreach (var window in windowProfile.Windows ?? new List<WindowLinkProfile>())
            {
                viewModel.Windows.Add(new WindowProfileItemViewModel
                {
                    Id = window.Id == Guid.Empty ? Guid.NewGuid() : window.Id,
                    Nickname = window.Nickname,
                    Url = window.Url,
                    IsEnabled = window.IsEnabled,
                    IsPrimaryExclusive = window.IsPrimaryExclusive,
                    IsNavigationBarEnabled = window.IsNavigationBarEnabled,
                    StreamingMode = StreamingModeOptions.Normalize(window.StreamingMode)
                });
            }

            WindowProfiles.Add(viewModel);
        }

        SelectedWindowProfile = WindowProfiles.FirstOrDefault();
    }

    private void NormalizeWindowProfilesInMemory()
    {
        if (_isNormalizingWindowProfiles)
        {
            return;
        }

        _isNormalizingWindowProfiles = true;
        try
        {
        if (WindowProfiles.Count <= 1)
        {
            return;
        }

        var normalized = new List<WindowProfileViewModel>();
        var byId = new Dictionary<Guid, WindowProfileViewModel>();
        var byName = new Dictionary<string, WindowProfileViewModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in WindowProfiles.ToList())
        {
            var normalizedName = profile.Name?.Trim() ?? string.Empty;
            WindowProfileViewModel? existing = null;

            if (profile.Id != Guid.Empty && byId.TryGetValue(profile.Id, out var existingById))
            {
                existing = existingById;
            }
            else if (!string.IsNullOrWhiteSpace(normalizedName) && byName.TryGetValue(normalizedName, out var existingByName))
            {
                existing = existingByName;
            }

            if (existing is null)
            {
                normalized.Add(profile);
                if (profile.Id != Guid.Empty)
                {
                    byId[profile.Id] = profile;
                }

                if (!string.IsNullOrWhiteSpace(normalizedName))
                {
                    byName[normalizedName] = profile;
                }

                continue;
            }

            if ((!existing.AssignedTvProfileId.HasValue || existing.AssignedTvProfileId == Guid.Empty) &&
                profile.AssignedTvProfileId.HasValue &&
                profile.AssignedTvProfileId != Guid.Empty)
            {
                existing.AssignedTvProfileId = profile.AssignedTvProfileId;
            }

            if (string.IsNullOrWhiteSpace(existing.AssignedTvProfileName) && !string.IsNullOrWhiteSpace(profile.AssignedTvProfileName))
            {
                existing.AssignedTvProfileName = profile.AssignedTvProfileName;
            }

            if (string.IsNullOrWhiteSpace(existing.BrowserProfileName) && !string.IsNullOrWhiteSpace(profile.BrowserProfileName))
            {
                existing.BrowserProfileName = profile.BrowserProfileName ?? string.Empty;
            }

            existing.KeepDisplayConnected = existing.KeepDisplayConnected || profile.KeepDisplayConnected;

            foreach (var window in profile.Windows.ToList())
            {
                var duplicateWindow = existing.Windows.FirstOrDefault(x =>
                    (window.Id != Guid.Empty && x.Id == window.Id) ||
                    (string.Equals(x.Nickname, window.Nickname, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(x.Url, window.Url, StringComparison.OrdinalIgnoreCase)));

                if (duplicateWindow is null)
                {
                    existing.Windows.Add(window);
                    continue;
                }

                duplicateWindow.Nickname = string.IsNullOrWhiteSpace(duplicateWindow.Nickname) ? window.Nickname : duplicateWindow.Nickname;
                duplicateWindow.Url = string.IsNullOrWhiteSpace(duplicateWindow.Url) ? window.Url : duplicateWindow.Url;
                duplicateWindow.IsEnabled = duplicateWindow.IsEnabled || window.IsEnabled;
                duplicateWindow.IsPrimaryExclusive = duplicateWindow.IsPrimaryExclusive || window.IsPrimaryExclusive;
                duplicateWindow.IsNavigationBarEnabled = duplicateWindow.IsNavigationBarEnabled || window.IsNavigationBarEnabled;
            }
        }

        if (normalized.Count == WindowProfiles.Count)
        {
            return;
        }

        var selectedProfileId = SelectedWindowProfile?.Id;
        WindowProfiles.Clear();
        foreach (var profile in normalized.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            WindowProfiles.Add(profile);
        }

        SelectedWindowProfile = selectedProfileId.HasValue
            ? WindowProfiles.FirstOrDefault(x => x.Id == selectedProfileId.Value)
            : WindowProfiles.FirstOrDefault();
        }
        finally
        {
            _isNormalizingWindowProfiles = false;
        }
    }

    private void RefreshWindowProfilesCollection()
    {
        var snapshot = BuildWindowProfiles();
        var rebuilt = snapshot
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(windowProfile =>
            {
                var viewModel = new WindowProfileViewModel
                {
                    Id = windowProfile.Id == Guid.Empty ? Guid.NewGuid() : windowProfile.Id,
                    Name = windowProfile.Name,
                    AssignedTvProfileId = windowProfile.AssignedTvProfileId,
                    AssignedTvProfileName = windowProfile.AssignedTvProfileName,
                    KeepDisplayConnected = windowProfile.KeepDisplayConnected,
                    BrowserProfileName = windowProfile.BrowserProfileName ?? string.Empty
                };

                foreach (var window in windowProfile.Windows)
                {
                    viewModel.Windows.Add(new WindowProfileItemViewModel
                    {
                        Id = window.Id == Guid.Empty ? Guid.NewGuid() : window.Id,
                        Nickname = window.Nickname,
                        Url = window.Url,
                        IsEnabled = window.IsEnabled,
                        IsPrimaryExclusive = window.IsPrimaryExclusive,
                        IsNavigationBarEnabled = window.IsNavigationBarEnabled,
                        StreamingMode = StreamingModeOptions.Normalize(window.StreamingMode)
                    });
                }

                return viewModel;
            })
            .ToList();

        var selectedProfileId = SelectedWindowProfile?.Id;
        WindowProfiles.Clear();
        foreach (var profile in rebuilt)
        {
            WindowProfiles.Add(profile);
        }

        SelectedWindowProfile = selectedProfileId.HasValue
            ? WindowProfiles.FirstOrDefault(x => x.Id == selectedProfileId.Value)
            : WindowProfiles.FirstOrDefault();
    }

    private void OnWindowProfilesInternalCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isNormalizingWindowProfiles || e.Action == NotifyCollectionChangedAction.Move)
        {
            return;
        }

        NormalizeWindowProfilesInMemory();
    }

    public async Task SetStreamWindowEnabledAsync(Guid itemId, bool isEnabled)
    {
        var stream = WindowProfiles.FirstOrDefault(x => x.Windows.Any(window => window.Id == itemId));
        var item = stream?.Windows.FirstOrDefault(window => window.Id == itemId);
        if (stream is null || item is null)
        {
            return;
        }

        item.IsEnabled = isEnabled;
        if (!isEnabled)
        {
            item.IsPrimaryExclusive = false;
        }
        await ApplyStreamWindowRuntimeStateAsync(stream, item);
        await SaveProfileInternalAsync(updateStatus: false);
        UpdateBridgeSnapshot();
        await PersistActiveSessionsAsync();
    }

    public async Task SetStreamKeepDisplayConnectedAsync(Guid streamId, bool keepDisplayConnected)
    {
        var stream = WindowProfiles.FirstOrDefault(x => x.Id == streamId);
        if (stream is null)
        {
            return;
        }

        stream.KeepDisplayConnected = keepDisplayConnected;
        if (keepDisplayConnected)
        {
            _streamKeepAliveAttemptUtc.Remove(stream.Id);
            await EnsureStreamDisplayConnectedAsync(stream, forceNow: true);
        }

        await SaveProfileInternalAsync(updateStatus: false);
    }

    private TvProfileViewModel? ResolveTvProfileForStream(WindowProfileViewModel stream)
    {
        var byId = stream.AssignedTvProfileId.HasValue
            ? TvProfiles.FirstOrDefault(x => x.Id == stream.AssignedTvProfileId.Value)
            : null;

        if (byId is not null)
        {
            return byId;
        }

        if (!string.IsNullOrWhiteSpace(stream.AssignedTvProfileName))
        {
            var byName = TvProfiles.FirstOrDefault(x =>
                string.Equals(x.Name, stream.AssignedTvProfileName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                stream.AssignedTvProfileId = byName.Id;
                stream.AssignedTvProfileName = byName.Name;
                return byName;
            }
        }

        return null;
    }

    private RegisteredDisplaySnapshot? FindRegisteredDisplayForBinding(DisplayTarget target, TvProfileTargetViewModel binding)
    {
        return _webRtcPublisherService
            .GetRegisteredDisplaysSnapshot()
            .FirstOrDefault(x =>
                (!string.IsNullOrWhiteSpace(binding.NetworkAddress) &&
                 string.Equals(x.NetworkAddress, binding.NetworkAddress, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(target.NetworkAddress) &&
                 string.Equals(x.NetworkAddress, target.NetworkAddress, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(binding.DeviceUniqueId) &&
                 string.Equals(x.DeviceId, binding.DeviceUniqueId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(target.DeviceUniqueId) &&
                 string.Equals(x.DeviceId, target.DeviceUniqueId, StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsRegisteredDisplayFresh(RegisteredDisplaySnapshot? registeredDisplay, TimeSpan maxAge)
    {
        return registeredDisplay is not null &&
               DateTime.TryParse(registeredDisplay.LastSeenUtc, out var lastSeenUtc) &&
               DateTime.UtcNow - lastSeenUtc.ToUniversalTime() < maxAge;
    }

    private DisplayTarget PromoteRegisteredDisplayAsOnlineTarget(DisplayTarget resolvedTarget, TvProfileTargetViewModel binding)
    {
        var registeredDisplay = FindRegisteredDisplayForBinding(resolvedTarget, binding);

        if (registeredDisplay is null)
        {
            return resolvedTarget;
        }

        return new DisplayTarget
        {
            Id = resolvedTarget.Id != Guid.Empty ? resolvedTarget.Id : binding.DisplayTargetId,
            Name = string.IsNullOrWhiteSpace(resolvedTarget.Name) ? binding.DisplayName : resolvedTarget.Name,
            NetworkAddress = string.IsNullOrWhiteSpace(registeredDisplay.NetworkAddress)
                ? resolvedTarget.NetworkAddress
                : registeredDisplay.NetworkAddress,
            LastKnownNetworkAddress = string.IsNullOrWhiteSpace(resolvedTarget.NetworkAddress)
                ? binding.NetworkAddress
                : resolvedTarget.NetworkAddress,
            DeviceUniqueId = string.IsNullOrWhiteSpace(resolvedTarget.DeviceUniqueId)
                ? binding.DeviceUniqueId
                : resolvedTarget.DeviceUniqueId,
            MacAddress = resolvedTarget.MacAddress,
            AlternateMacAddresses = resolvedTarget.AlternateMacAddresses.ToList(),
            DiscoverySource = string.IsNullOrWhiteSpace(resolvedTarget.DiscoverySource)
                ? "TV online via registro Roku"
                : resolvedTarget.DiscoverySource,
            TransportKind = resolvedTarget.TransportKind,
            IsOnline = true,
            WasPreviouslyKnown = true,
            IsStaticTarget = resolvedTarget.IsStaticTarget,
            NativeWidth = resolvedTarget.NativeWidth > 0 ? resolvedTarget.NativeWidth : binding.NativeWidth,
            NativeHeight = resolvedTarget.NativeHeight > 0 ? resolvedTarget.NativeHeight : binding.NativeHeight
        };
    }

    public async Task EnsureKeepAliveStreamsAsync()
    {
        foreach (var stream in WindowProfiles.Where(x => x.KeepDisplayConnected).ToList())
        {
            await EnsureStreamDisplayConnectedAsync(stream, forceNow: false);
        }
    }

    private List<BrowserProfileDefinition> BuildBrowserProfiles()
    {
        return BrowserProfiles
            .Select(x => BrowserProfileStorage.NormalizeName(x.Name))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => new BrowserProfileDefinition
            {
                Name = x
            })
            .ToList();
    }

    public Task RequestStreamReloadAsync(Guid windowId)
    {
        var window = Windows.FirstOrDefault(x => x.Id == windowId);
        if (window is null)
        {
            StatusMessage = "A janela nao esta ativa para recarregar o stream.";
            return Task.CompletedTask;
        }

        _webRtcPublisherService.RequestStreamReload(windowId);
        StatusMessage = string.Format("Recarregamento do stream solicitado para '{0}'.", window.Title);
        return Task.CompletedTask;
    }

    private async Task EnsureStreamDisplayConnectedAsync(WindowProfileViewModel stream, bool forceNow)
    {
        if (!stream.KeepDisplayConnected)
        {
            return;
        }

        if (!forceNow &&
            _streamKeepAliveAttemptUtc.TryGetValue(stream.Id, out var previousAttempt) &&
            DateTime.UtcNow - previousAttempt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        var tvProfile = ResolveTvProfileForStream(stream);
        var binding = tvProfile?.Targets.FirstOrDefault();
        if (tvProfile is null || binding is null)
        {
            return;
        }

        var resolvedTarget = await _displayIdentityResolverService.ResolveCurrentTargetAsync(
            new DisplayTarget
            {
                Id = binding.DisplayTargetId,
                Name = binding.DisplayName,
                NetworkAddress = binding.NetworkAddress,
                DeviceUniqueId = binding.DeviceUniqueId,
                MacAddress = binding.MacAddress,
                AlternateMacAddresses = binding.AlternateMacAddresses.ToList(),
                DiscoverySource = binding.DiscoverySource,
                NativeWidth = binding.NativeWidth,
                NativeHeight = binding.NativeHeight,
                TransportKind = DisplayTransportKind.LanStreaming,
                IsStaticTarget = true
            },
            Targets,
            CancellationToken.None);
        resolvedTarget = PromoteRegisteredDisplayAsOnlineTarget(resolvedTarget, binding);

        var activeProbe = await _manualDisplayProbeService.ProbeAsync(resolvedTarget.NetworkAddress, CancellationToken.None);
        var tvReachable = activeProbe is not null;
        var registeredDisplay = FindRegisteredDisplayForBinding(resolvedTarget, binding);
        var registrationFresh = IsRegisteredDisplayFresh(registeredDisplay, TimeSpan.FromSeconds(8));
        var recentlyStreaming = _webRtcPublisherService.IsDisplayStreamingRecently(resolvedTarget, TimeSpan.FromSeconds(20));

        if (resolvedTarget.TransportKind == DisplayTransportKind.LanStreaming && recentlyStreaming)
        {
            AppLog.Write(
                "StreamKeepAlive",
                string.Format(
                    "Keep-alive ignorado para stream '{0}' porque a TV '{1}' esta consumindo HLS recentemente.",
                    stream.Name,
                    resolvedTarget.Name));
            return;
        }

        if (resolvedTarget.TransportKind == DisplayTransportKind.LanStreaming && registeredDisplay is not null && tvReachable)
        {
            AppLog.Write(
                "StreamKeepAlive",
                string.Format(
                    "Keep-alive ignorado para stream '{0}' porque a TV '{1}' ja esta registrada e alcancavel.",
                    stream.Name,
                    resolvedTarget.Name));
            return;
        }

        if (registrationFresh)
        {
            AppLog.Write(
                "StreamKeepAlive",
                string.Format(
                    "Keep-alive ignorado para stream '{0}' porque a TV '{1}' se registrou recentemente.",
                    stream.Name,
                    resolvedTarget.Name));
            return;
        }

        if (tvReachable && registrationFresh)
        {
            return;
        }

        _streamKeepAliveAttemptUtc[stream.Id] = DateTime.UtcNow;
        var requirePowerOn = !registrationFresh;
        var result = await _webRtcPublisherService.EnsureDisplayAppRunningAsync(resolvedTarget, requirePowerOn, CancellationToken.None);
        AppLog.Write(
            "StreamKeepAlive",
            string.Format(
                "Keep-alive do stream '{0}' para TV '{1}': {2}",
                stream.Name,
                resolvedTarget.Name,
                result));

        if (requirePowerOn)
        {
            await Task.Delay(4000);
            var freshAfterWake = IsRegisteredDisplayFresh(FindRegisteredDisplayForBinding(resolvedTarget, binding), TimeSpan.FromSeconds(6));
            if (!freshAfterWake)
            {
                var fallbackResult = await _webRtcPublisherService.ForceWakeDisplayAsync(resolvedTarget, CancellationToken.None);
                AppLog.Write(
                    "StreamKeepAlive",
                    string.Format(
                        "Wake fallback do stream '{0}' para TV '{1}': {2}",
                        stream.Name,
                        resolvedTarget.Name,
                        fallbackResult));
            }
        }
    }

    public async Task SetStreamWindowPrimaryExclusiveAsync(Guid itemId, bool isPrimaryExclusive)
    {
        var stream = WindowProfiles.FirstOrDefault(x => x.Windows.Any(window => window.Id == itemId));
        var item = stream?.Windows.FirstOrDefault(window => window.Id == itemId);
        if (stream is null || item is null)
        {
            return;
        }

        if (isPrimaryExclusive)
        {
            foreach (var streamItem in stream.Windows)
            {
                var isCurrent = streamItem.Id == itemId;
                streamItem.IsPrimaryExclusive = isCurrent;
                streamItem.IsEnabled = isCurrent;
            }

            foreach (var streamItem in stream.Windows.ToList())
            {
                await ApplyStreamWindowRuntimeStateAsync(stream, streamItem);
            }
        }
        else
        {
            item.IsPrimaryExclusive = false;
            await ApplyStreamWindowRuntimeStateAsync(stream, item);
        }

        await SaveProfileInternalAsync(updateStatus: false);
        UpdateBridgeSnapshot();
        await PersistActiveSessionsAsync();
    }

    private async Task SyncWindowProfileRuntimeAsync(WindowProfileViewModel windowProfile)
    {
        foreach (var item in windowProfile.Windows.ToList())
        {
            await ApplyStreamWindowRuntimeStateAsync(windowProfile, item);
        }

        var liveWindowsToRemove = Windows
            .Where(x => x.ActiveSessionId == windowProfile.Id &&
                        windowProfile.Windows.All(item => item.Id != x.Id || !item.IsEnabled))
            .ToList();

        foreach (var liveWindow in liveWindowsToRemove)
        {
            await _webRtcPublisherService.UnpublishAsync(liveWindow, CancellationToken.None);
            await _browserInstanceHost.CloseAsync(liveWindow.Id, CancellationToken.None);
            Windows.Remove(liveWindow);
        }

        RebuildActiveSessionsFromWindows();
    }

    private async Task ResetWindowProfileRuntimeAsync(WindowProfileViewModel windowProfile)
    {
        var liveWindows = Windows
            .Where(x => x.ActiveSessionId == windowProfile.Id || string.Equals(x.BrowserProfileName, windowProfile.BrowserProfileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var liveWindow in liveWindows)
        {
            await _webRtcPublisherService.UnpublishAsync(liveWindow, CancellationToken.None);
            await _browserInstanceHost.CloseAsync(liveWindow.Id, CancellationToken.None);
            Windows.Remove(liveWindow);
        }

        RebuildActiveSessionsFromWindows();
    }

    private async Task ApplyStreamWindowRuntimeStateAsync(WindowProfileViewModel stream, WindowProfileItemViewModel item)
    {
        if (!item.IsEnabled)
        {
            item.IsPrimaryExclusive = false;
            var existingWindow = Windows.FirstOrDefault(x => x.Id == item.Id);
            if (existingWindow is not null)
            {
                await _webRtcPublisherService.UnpublishAsync(existingWindow, CancellationToken.None);
                await _browserInstanceHost.CloseAsync(existingWindow.Id, CancellationToken.None);
                Windows.Remove(existingWindow);
                RebuildActiveSessionsFromWindows();
            }

            return;
        }

        var tvProfile = ResolveTvProfileForStream(stream);
        if (tvProfile is null)
        {
            item.IsEnabled = false;
            StatusMessage = string.Format("O stream '{0}' nao possui um perfil de TV valido vinculado.", stream.Name);
            return;
        }

        var binding = tvProfile.Targets.FirstOrDefault();
        if (binding is null)
        {
            item.IsEnabled = false;
            StatusMessage = string.Format("O perfil de TV '{0}' nao possui uma TV base configurada.", tvProfile.Name);
            return;
        }

        var resolvedTarget = await _displayIdentityResolverService.ResolveCurrentTargetAsync(
            new DisplayTarget
            {
                Id = binding.DisplayTargetId,
                Name = binding.DisplayName,
                NetworkAddress = binding.NetworkAddress,
                DeviceUniqueId = binding.DeviceUniqueId,
                MacAddress = binding.MacAddress,
                AlternateMacAddresses = binding.AlternateMacAddresses.ToList(),
                DiscoverySource = binding.DiscoverySource,
                NativeWidth = binding.NativeWidth,
                NativeHeight = binding.NativeHeight,
                TransportKind = DisplayTransportKind.LanStreaming,
                IsStaticTarget = true
            },
            Targets,
            CancellationToken.None);
        resolvedTarget = PromoteRegisteredDisplayAsOnlineTarget(resolvedTarget, binding);

        Uri? initialUri = null;
        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            Uri.TryCreate(item.Url, UriKind.Absolute, out initialUri);
        }

        var browserWindow = Windows.FirstOrDefault(x => x.Id == item.Id)
            ?? await _browserInstanceHost.CreateAsync(
                initialUri ?? new Uri("about:blank"),
                CancellationToken.None,
                item.Id,
                item.Nickname);

        browserWindow.Title = string.IsNullOrWhiteSpace(item.Nickname) ? browserWindow.Title : item.Nickname;
        browserWindow.InitialUri = initialUri;
        browserWindow.State = WindowSessionState.Created;
        browserWindow.ProfileName = stream.Name;
        browserWindow.ActiveSessionId = stream.Id;
        browserWindow.ActiveSessionName = stream.Name;
        browserWindow.AssignedTarget = resolvedTarget;
        browserWindow.IsPrimaryExclusive = item.IsPrimaryExclusive;
        browserWindow.IsNavigationBarEnabled = item.IsNavigationBarEnabled;
        browserWindow.BrowserProfileName = stream.BrowserProfileName ?? string.Empty;
        browserWindow.StreamingMode = StreamingModeOptions.Normalize(item.StreamingMode);

        if (!Windows.Any(x => x.Id == browserWindow.Id))
        {
            Windows.Add(browserWindow);
        }

        try
        {
            await _routingService.AssignWindowToTargetAsync(browserWindow, resolvedTarget, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            browserWindow.State = WindowSessionState.Created;
            browserWindow.AssignedTarget = null;

            if (Windows.Contains(browserWindow))
            {
                await _webRtcPublisherService.UnpublishAsync(browserWindow, CancellationToken.None);
                await _browserInstanceHost.CloseAsync(browserWindow.Id, CancellationToken.None);
                Windows.Remove(browserWindow);
            }

            RebuildActiveSessionsFromWindows();
            StatusMessage = string.Format(
                "A janela '{0}' do stream '{1}' ficou marcada, mas a TV esta offline: {2}",
                string.IsNullOrWhiteSpace(item.Nickname) ? item.Url : item.Nickname,
                stream.Name,
                ex.Message);
            AppLog.Write("Streams", StatusMessage);
            return;
        }

        RebuildActiveSessionsFromWindows();
        var activeSession = ActiveSessions.FirstOrDefault(x => x.Id == stream.Id);
        if (activeSession is not null)
        {
            activeSession.Name = stream.Name;
            activeSession.ProfileName = stream.Name;
            activeSession.BoundDisplays.Clear();
            activeSession.BoundDisplays.Add(new ActiveSessionDisplayBindingViewModel
            {
                DisplayTargetId = resolvedTarget.Id,
                DisplayName = resolvedTarget.Name,
                NetworkAddress = resolvedTarget.NetworkAddress,
                DeviceUniqueId = resolvedTarget.DeviceUniqueId,
                BindingName = tvProfile.Name
            });
            activeSession.TvCount = activeSession.BoundDisplays.Count;
            activeSession.BindingCount = activeSession.BoundDisplays.Count;
        }
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
                entry.AlternateMacAddresses = probedTarget.AlternateMacAddresses.ToList();
                entry.DiscoverySource = probedTarget.DiscoverySource;
                entry.NativeWidth = probedTarget.NativeWidth;
                entry.NativeHeight = probedTarget.NativeHeight;
            }
        }

        var existingTarget = Targets.FirstOrDefault(x =>
            (entry.DisplayTargetId != Guid.Empty && x.Id == entry.DisplayTargetId) ||
            (!string.IsNullOrWhiteSpace(entry.DeviceUniqueId) &&
             string.Equals(x.DeviceUniqueId, entry.DeviceUniqueId, StringComparison.OrdinalIgnoreCase)) ||
            HasMatchingMacIdentity(x, entry) ||
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

            existingTarget.AlternateMacAddresses = MergeMacLists(existingTarget.AlternateMacAddresses, entry.AlternateMacAddresses);

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
            AlternateMacAddresses = entry.AlternateMacAddresses.ToList(),
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

    private static bool HasMatchingMacIdentity(DisplayTarget target, TvProfileTargetEditorViewModel entry)
    {
        var targetMacs = MacAddressFormatter.NormalizeMany(new[] { target.MacAddress }.Concat(target.AlternateMacAddresses ?? Enumerable.Empty<string>()));
        var entryMacs = MacAddressFormatter.NormalizeMany(new[] { entry.MacAddress }.Concat(entry.AlternateMacAddresses ?? Enumerable.Empty<string>()));
        return targetMacs.Intersect(entryMacs, StringComparer.OrdinalIgnoreCase).Any();
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

    private static List<string> MergeMacLists(IEnumerable<string>? existing, IEnumerable<string>? incoming)
    {
        return MacAddressFormatter.NormalizeMany((existing ?? Enumerable.Empty<string>()).Concat(incoming ?? Enumerable.Empty<string>()));
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

    public string PrimaryDisplayName => Targets.FirstOrDefault()?.DisplayName ?? "TV nao configurada";

    public string PrimaryNetworkAddress => Targets.FirstOrDefault()?.NetworkAddress ?? "IP nao configurado";

    public void NotifyTargetSummaryChanged()
    {
        RaisePropertyChanged(nameof(PrimaryDisplayName));
        RaisePropertyChanged(nameof(PrimaryNetworkAddress));
    }
}

public sealed class TvProfileTargetViewModel : ViewModelBase
{
    private Guid _displayTargetId;
    private string _displayName = string.Empty;
    private string _networkAddress = string.Empty;
    private string _deviceUniqueId = string.Empty;
    private string _macAddress = string.Empty;
    private List<string> _alternateMacAddresses = new List<string>();
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

    public List<string> AlternateMacAddresses
    {
        get => _alternateMacAddresses;
        set => SetProperty(ref _alternateMacAddresses, value ?? new List<string>());
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
    private bool _keepDisplayConnected;
    private string _browserProfileName = string.Empty;

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

    public bool KeepDisplayConnected
    {
        get => _keepDisplayConnected;
        set => SetProperty(ref _keepDisplayConnected, value);
    }

    public string BrowserProfileName
    {
        get => _browserProfileName;
        set => SetProperty(ref _browserProfileName, value);
    }

    public ObservableCollection<WindowProfileItemViewModel> Windows { get; } = new ObservableCollection<WindowProfileItemViewModel>();
}

public sealed class BrowserProfileViewModel : ViewModelBase
{
    private string _name = string.Empty;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
}

internal sealed class ImportedBackupProfiles
{
    public List<AppProfile> Profiles { get; } = new List<AppProfile>();

    public string StartupProfileName { get; set; } = string.Empty;

    public string DefaultProfileName { get; set; } = string.Empty;
}

public sealed class WindowProfileItemViewModel : ViewModelBase
{
    private Guid _id;
    private string _nickname = string.Empty;
    private string _url = string.Empty;
    private bool _isEnabled;
    private bool _isPrimaryExclusive;
    private bool _isNavigationBarEnabled;
    private string _streamingMode = StreamingModeOptions.Interaction;

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

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool IsPrimaryExclusive
    {
        get => _isPrimaryExclusive;
        set => SetProperty(ref _isPrimaryExclusive, value);
    }

    public bool IsNavigationBarEnabled
    {
        get => _isNavigationBarEnabled;
        set => SetProperty(ref _isNavigationBarEnabled, value);
    }

    public string StreamingMode
    {
        get => _streamingMode;
        set => SetProperty(ref _streamingMode, StreamingModeOptions.Normalize(value));
    }
}












