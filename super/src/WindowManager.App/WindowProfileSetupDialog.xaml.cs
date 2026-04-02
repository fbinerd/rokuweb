using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowManager.App.Runtime;
using WindowManager.App.Runtime.Publishing;
using WindowManager.App.ViewModels;

namespace WindowManager.App;

public partial class WindowProfileSetupDialog : Window
{
    private readonly BrowserSnapshotService _browserSnapshotService;
    private readonly BrowserAudioCaptureService _browserAudioCaptureService;
    private readonly Guid _temporaryPreviewWindowId = Guid.NewGuid();
    private readonly Dictionary<Guid, string> _originalWindowUrls = new Dictionary<Guid, string>();
    private readonly Dictionary<Guid, bool> _originalWindowNavigationBars = new Dictionary<Guid, bool>();
    private readonly DispatcherTimer _previewRefreshTimer;
    private BrowserCaptureWindow? _temporaryPreviewCaptureWindow;
    private StreamPreviewWindow? _expandedPreviewWindow;
    private string? _currentPreviewAddress;
    private Guid? _activePreviewWindowId;
    private bool _saved;
    private string _temporaryPreviewBrowserProfileName = string.Empty;

    public WindowProfileSetupDialog(
        WindowProfileSetupViewModel viewModel,
        BrowserSnapshotService browserSnapshotService,
        BrowserAudioCaptureService browserAudioCaptureService)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
        _browserSnapshotService = browserSnapshotService;
        _browserAudioCaptureService = browserAudioCaptureService;
        _previewRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _previewRefreshTimer.Tick += OnPreviewRefreshTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;

        foreach (var window in viewModel.Windows)
        {
            if (window.Id != Guid.Empty)
            {
                _originalWindowUrls[window.Id] = window.Url ?? string.Empty;
                _originalWindowNavigationBars[window.Id] = window.IsNavigationBarEnabled;
            }
        }
    }

    public WindowProfileSetupViewModel ViewModel { get; }

    private void OnAddWindowClick(object sender, RoutedEventArgs e)
    {
        OpenWindowEditor(null);
    }

    private void OnEditSelectedWindowClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedWindow is null)
        {
            return;
        }

        OpenWindowEditor(ViewModel.SelectedWindow);
    }

    private void OnWindowsListDoubleClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedWindow is null)
        {
            return;
        }

        OpenWindowEditor(ViewModel.SelectedWindow);
    }

    private void OnWindowsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not ListView)
        {
            return;
        }

        if (ViewModel.SelectedWindow is null)
        {
            return;
        }

        NavigatePreview(ViewModel.SelectedWindow, useSharedInstance: true, navigateSharedInstance: false);
    }

    private void OnPreviewWindowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink hyperlink || hyperlink.Tag is not WindowLinkEditorViewModel window)
        {
            return;
        }

        ViewModel.SelectedWindow = window;
        NavigatePreview(window, useSharedInstance: true, navigateSharedInstance: false);
    }

    private void OnNavigationBarToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not WindowLinkEditorViewModel window)
        {
            return;
        }

        ViewModel.SelectedWindow = window;
        ApplyNavigationBarPreference(window);
        _ = RefreshPreviewImageAsync();
    }

    private async void OnCreateBrowserProfileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new BrowserProfileNameDialog
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var result = await ViewModel.CreateBrowserProfileAsync(dialog.ViewModel.ProfileName);
        if (!result.Succeeded)
        {
            MessageBox.Show(this, result.Message, "Perfil do navegador", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RecreateTemporaryPreviewCaptureWindow();
    }

    private async void OnDeleteBrowserProfileClick(object sender, RoutedEventArgs e)
    {
        var selectedProfile = ViewModel.SelectedBrowserProfileName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedProfile))
        {
            MessageBox.Show(this, "Selecione um perfil de navegador para excluir.", "Perfil do navegador", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            string.Format("Excluir o perfil de navegador '{0}' e resetar completamente a base persistida dele?", selectedProfile),
            "Excluir perfil do navegador",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await ViewModel.DeleteSelectedBrowserProfileAsync();
        if (!result.Succeeded)
        {
            MessageBox.Show(this, result.Message, "Perfil do navegador", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RecreateTemporaryPreviewCaptureWindow();
    }

    private void OnBrowserProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RecreateTemporaryPreviewCaptureWindow();
    }

    private void OnRemoveWindowClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RemoveSelectedWindow();
    }

    private void OnPreviewBorderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2)
        {
            return;
        }

        OpenExpandedPreview();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _saved = true;
        DialogResult = true;
        Close();
    }

    private void OpenWindowEditor(WindowLinkEditorViewModel? existingWindow)
    {
        var editorViewModel = new StreamWindowEditorViewModel
        {
            Nickname = existingWindow?.Nickname ?? string.Empty,
            Url = existingWindow?.Url ?? string.Empty
        };

        var dialog = new StreamWindowEditorDialog(editorViewModel)
        {
            Owner = this,
            Title = existingWindow is null ? "Nova Janela" : string.Format("Editar {0}", existingWindow.Nickname)
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ViewModel.AddOrUpdateWindow(editorViewModel.Nickname, editorViewModel.Url, existingWindow);
        if (existingWindow is not null)
        {
            NavigatePreview(editorViewModel.Url, sharedWindowId: null, navigateSharedInstance: false);
            return;
        }

        NavigatePreview(ViewModel.SelectedWindow, useSharedInstance: false, navigateSharedInstance: false);
    }

    private void NavigatePreview(WindowLinkEditorViewModel? window, bool useSharedInstance, bool navigateSharedInstance)
    {
        if (window is null)
        {
            NavigatePreview((string?)null, null, false);
            return;
        }

        ApplyNavigationBarPreference(window);
        var sharedWindowId = useSharedInstance &&
                             window.Id != Guid.Empty &&
                             _browserSnapshotService.IsRegistered(window.Id)
            ? window.Id
            : (Guid?)null;
        NavigatePreview(window.Url, sharedWindowId, navigateSharedInstance);
    }

    private void NavigatePreview(string? url, Guid? sharedWindowId, bool navigateSharedInstance)
    {
        var normalized = url?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            PreviewAddressText.Text = "Selecione uma janela ou clique no link para visualizar.";
            _currentPreviewAddress = null;
            _activePreviewWindowId = null;
            return;
        }

        if (!normalized.Contains("://"))
        {
            normalized = "https://" + normalized;
        }

        PreviewAddressText.Text = normalized;
        _currentPreviewAddress = normalized;

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            _activePreviewWindowId = null;
            return;
        }

        if (!AppRuntimeState.BrowserEngineAvailable)
        {
            PreviewFallbackText.Visibility = Visibility.Visible;
            _activePreviewWindowId = null;
            return;
        }

        PreviewFallbackText.Visibility = Visibility.Collapsed;
        if (sharedWindowId.HasValue)
        {
            _activePreviewWindowId = sharedWindowId.Value;
            if (navigateSharedInstance)
            {
                _browserSnapshotService.NavigateRegisteredBrowser(sharedWindowId.Value, uri);
            }
            _browserSnapshotService.InvalidateCapture(sharedWindowId.Value);
        }
        else
        {
            EnsureTemporaryPreviewCaptureWindow();
            _temporaryPreviewCaptureWindow?.UpdateAddress(uri);
            _browserSnapshotService.InvalidateCapture(_temporaryPreviewWindowId);
            _activePreviewWindowId = _temporaryPreviewWindowId;
        }
        _ = RefreshPreviewImageAsync();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (!AppRuntimeState.BrowserEngineAvailable)
        {
            PreviewFallbackText.Visibility = Visibility.Visible;
            return;
        }

        EnsureTemporaryPreviewCaptureWindow();
        _previewRefreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _previewRefreshTimer.Stop();
        if (!_saved)
        {
            RestoreSharedWindowAddresses();
            RestoreSharedWindowNavigationBars();
        }
        CloseTemporaryPreviewCaptureWindow();
        _expandedPreviewWindow?.Close();
        _expandedPreviewWindow = null;
    }

    private void EnsureTemporaryPreviewCaptureWindow()
    {
        EnsureTemporaryPreviewCaptureWindow(forceRecreate: false);
    }

    private void EnsureTemporaryPreviewCaptureWindow(bool forceRecreate)
    {
        if (!AppRuntimeState.BrowserEngineAvailable)
        {
            return;
        }

        var browserProfileName = ViewModel.SelectedBrowserProfileName?.Trim() ?? string.Empty;
        if (!forceRecreate &&
            _temporaryPreviewCaptureWindow is not null &&
            string.Equals(_temporaryPreviewBrowserProfileName, browserProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CloseTemporaryPreviewCaptureWindow();
        _temporaryPreviewBrowserProfileName = browserProfileName;
        _temporaryPreviewCaptureWindow = new BrowserCaptureWindow(
            _temporaryPreviewWindowId,
            new Uri("about:blank"),
            _browserAudioCaptureService,
            browserProfileName);
        _temporaryPreviewCaptureWindow.Show();
        _browserSnapshotService.Register(_temporaryPreviewWindowId, _temporaryPreviewCaptureWindow.Browser);
    }

    private void RecreateTemporaryPreviewCaptureWindow()
    {
        if (!AppRuntimeState.BrowserEngineAvailable)
        {
            return;
        }

        var shouldRefreshTemporaryPreview = _activePreviewWindowId == _temporaryPreviewWindowId;
        EnsureTemporaryPreviewCaptureWindow(forceRecreate: true);
        if (shouldRefreshTemporaryPreview && !string.IsNullOrWhiteSpace(_currentPreviewAddress))
        {
            NavigatePreview(_currentPreviewAddress, sharedWindowId: null, navigateSharedInstance: false);
        }
    }

    private void CloseTemporaryPreviewCaptureWindow()
    {
        _browserSnapshotService.Unregister(_temporaryPreviewWindowId);
        _browserAudioCaptureService.Unregister(_temporaryPreviewWindowId);
        _temporaryPreviewCaptureWindow?.Close();
        _temporaryPreviewCaptureWindow = null;
    }

    private void ApplyNavigationBarPreference(WindowLinkEditorViewModel? window)
    {
        var enabled = window?.IsNavigationBarEnabled == true;

        if (_activePreviewWindowId.HasValue && _activePreviewWindowId.Value != _temporaryPreviewWindowId)
        {
            _ = _browserSnapshotService.SetNavigationBarEnabledAsync(_activePreviewWindowId.Value, enabled);
        }

        if (_temporaryPreviewCaptureWindow is not null)
        {
            _temporaryPreviewCaptureWindow.SetNavigationBarEnabled(enabled);
        }
    }

    private async void OnPreviewRefreshTimerTick(object? sender, EventArgs e)
    {
        await RefreshPreviewImageAsync();
    }

    private async Task RefreshPreviewImageAsync()
    {
        if (!IsVisible || !_activePreviewWindowId.HasValue)
        {
            return;
        }

        var jpegBytes = await _browserSnapshotService.CaptureJpegAsync(_activePreviewWindowId.Value, default);
        if (jpegBytes is null || jpegBytes.Length == 0)
        {
            return;
        }

        using (var stream = new MemoryStream(jpegBytes))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            PreviewImage.Source = bitmap;
            _expandedPreviewWindow?.UpdatePreview(bitmap, PreviewAddressText.Text, false);
        }
    }

    private void OpenExpandedPreview()
    {
        if (!AppRuntimeState.BrowserEngineAvailable || !_activePreviewWindowId.HasValue)
        {
            return;
        }

        if (_expandedPreviewWindow is not null)
        {
            _expandedPreviewWindow.Close();
            _expandedPreviewWindow = null;
        }

        _expandedPreviewWindow = new StreamPreviewWindow(_activePreviewWindowId.Value, _browserSnapshotService, allowRemoteInteraction: false)
        {
            Owner = this
        };
        _expandedPreviewWindow.Closed += (_, _) => _expandedPreviewWindow = null;

        _expandedPreviewWindow.Title = GetExpandedPreviewTitle();
        _expandedPreviewWindow.UpdatePreview(
            PreviewImage.Source,
            PreviewAddressText.Text,
            PreviewFallbackText.Visibility == Visibility.Visible);

        _expandedPreviewWindow.Show();
        _expandedPreviewWindow.Activate();
    }

    private void RestoreSharedWindowAddresses()
    {
        foreach (var pair in _originalWindowUrls)
        {
            if (!_browserSnapshotService.IsRegistered(pair.Key))
            {
                continue;
            }

            var url = pair.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!url.Contains("://"))
            {
                url = "https://" + url;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                _browserSnapshotService.NavigateRegisteredBrowser(pair.Key, uri);
                _browserSnapshotService.InvalidateCapture(pair.Key);
            }
        }
    }

    private void RestoreSharedWindowNavigationBars()
    {
        foreach (var pair in _originalWindowNavigationBars)
        {
            if (!_browserSnapshotService.IsRegistered(pair.Key))
            {
                continue;
            }

            _ = _browserSnapshotService.SetNavigationBarEnabledAsync(pair.Key, pair.Value);
            _browserSnapshotService.InvalidateCapture(pair.Key);
        }
    }

    private string GetExpandedPreviewTitle()
    {
        var windowName = ViewModel.SelectedWindow?.Nickname;
        return string.IsNullOrWhiteSpace(windowName)
            ? "Visualizacao Expandida"
            : string.Format("Visualizacao Expandida - {0}", windowName);
    }
}
