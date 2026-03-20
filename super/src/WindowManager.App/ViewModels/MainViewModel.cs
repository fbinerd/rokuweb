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
    private readonly LocalWebRtcPublisherService _webRtcPublisherService;
    private readonly KnownDisplayStore _knownDisplayStore;

    private bool _isApplyingProfile;
    private bool _isRefreshingProfileNames;
    private WindowSession? _selectedWindow;
    private DisplayTarget? _selectedTarget;
    private StaticDisplayPanelViewModel? _selectedStaticPanel;
    private string _profileName = "default";
    private string _browserUrlInput = "https://emei.lovable.app";
    private string _currentBrowserAddress = "https://emei.lovable.app";
    private int _webRtcServerPort = 8090;
    private WebRtcBindMode _webRtcBindMode = WebRtcBindMode.Lan;
    private string _webRtcSpecificIp = string.Empty;
    private bool _isDefaultProfile;
    private string _statusMessage = "Pronto para criar janelas e associar destinos.";

    public MainViewModel(
        IBrowserInstanceHost browserInstanceHost,
        IDisplayDiscoveryService displayDiscoveryService,
        RoutingService routingService,
        ProfileStore profileStore,
        LocalWebRtcPublisherService webRtcPublisherService,
        KnownDisplayStore knownDisplayStore)
    {
        _browserInstanceHost = browserInstanceHost;
        _displayDiscoveryService = displayDiscoveryService;
        _routingService = routingService;
        _profileStore = profileStore;
        _webRtcPublisherService = webRtcPublisherService;
        _knownDisplayStore = knownDisplayStore;

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

        UpdateBridgeSnapshot();
    }

    public ObservableCollection<WindowSession> Windows { get; } = new ObservableCollection<WindowSession>();

    public ObservableCollection<DisplayTarget> Targets { get; } = new ObservableCollection<DisplayTarget>();

    public ObservableCollection<string> AvailableProfiles { get; } = new ObservableCollection<string>();

    public ObservableCollection<StaticDisplayPanelViewModel> StaticPanels { get; } = new ObservableCollection<StaticDisplayPanelViewModel>();

    public IReadOnlyList<RenderResolutionMode> ResolutionModes { get; }

    public IReadOnlyList<WebRtcBindMode> WebRtcBindModes { get; }

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
                SaveProfileCommand.RaiseCanExecuteChanged();
                SetDefaultProfileCommand.RaiseCanExecuteChanged();
                LoadProfileCommand.RaiseCanExecuteChanged();
                DeleteProfileCommand.RaiseCanExecuteChanged();
                _ = SyncDefaultProfileSelectionAsync();
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
        await RefreshProfileNamesAsync();

        var startupProfileName = await _profileStore.GetStartupProfileNameAsync(CancellationToken.None);
        ProfileName = startupProfileName;
        await LoadProfileAsync();

        _ = RefreshTargetsAfterStartupAsync();
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
        SelectedWindow = Windows.FirstOrDefault();
        CurrentBrowserAddress = SelectedWindow?.InitialUri?.ToString() ?? "about:blank";
        StatusMessage = string.Format("Painel '{0}' removido.", deletedTitle);
        await SaveProfileInternalAsync(updateStatus: false);
        UpdateBridgeSnapshot();
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
        try
        {
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
                    IsWebRtcPublishingEnabled = persistedWindow.IsWebRtcPublishingEnabled
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
        }

        await RefreshProfileNamesAsync();
        await SyncDefaultProfileSelectionAsync();
        StatusMessage = string.Format("Perfil '{0}' restaurado com sucesso. As rotas WebRTC podem ser atualizadas manualmente apos a abertura.", ProfileName);
        UpdateBridgeSnapshot();
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
            Windows = Windows.Select(x => new WindowSessionProfile
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
                IsWebRtcPublishingEnabled = x.IsWebRtcPublishingEnabled
            }).ToList(),
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
            _webRtcPublisherService.UpdateWindowSnapshots(Windows, WebRtcServerPort, WebRtcBindMode, WebRtcSpecificIp);
        }
        catch
        {
        }
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












