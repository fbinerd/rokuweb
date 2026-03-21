using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowManager.App.Runtime;
using WindowManager.App.Runtime.Publishing;
using WindowManager.App.ViewModels;
using WindowManager.Core.Models;
using Microsoft.Win32;

namespace WindowManager.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly BrowserSnapshotService _browserSnapshotService;
    private readonly Dictionary<Guid, Border> _previewCards = new Dictionary<Guid, Border>();
    private readonly Dictionary<Guid, Image> _previewImages = new Dictionary<Guid, Image>();
    private readonly Dictionary<Guid, BrowserCaptureWindow> _captureWindows = new Dictionary<Guid, BrowserCaptureWindow>();
    private readonly DispatcherTimer _previewRefreshTimer;
    private TransmissionLogWindow? _logWindow;
    private Guid? _expandedPreviewWindowId;
    private bool _isRefreshingPreviews;
    private Point? _lastExpandedPreviewMousePoint;

    public MainWindow(MainViewModel viewModel, BrowserSnapshotService browserSnapshotService)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _browserSnapshotService = browserSnapshotService;
        DataContext = viewModel;

        _viewModel.Windows.CollectionChanged += OnWindowsCollectionChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        foreach (var window in _viewModel.Windows)
        {
            AddPreview(window);
        }

        _previewRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _previewRefreshTimer.Tick += OnPreviewRefreshTimerTick;

        Loaded += OnLoaded;
        UpdateSelectionVisuals();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _viewModel.InitializeAfterStartupAsync();
            _previewRefreshTimer.Start();
            _ = RefreshPreviewImagesAsync();
        }
        catch (Exception ex)
        {
            _viewModel.ReportStartupFailure(string.Format("Falha ao restaurar a inicializacao automatica: {0}", ex.Message));
        }
    }

    private void OnWindowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (WindowSession window in e.NewItems)
            {
                AddPreview(window);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (WindowSession window in e.OldItems)
            {
                RemovePreview(window);
            }
        }

        UpdateSelectionVisuals();
    }

    private void AddPreview(WindowSession session)
    {
        session.PropertyChanged += OnWindowSessionPropertyChanged;
        EnsureCaptureWindow(session);

        var headerTitle = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 8, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        headerTitle.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(WindowSession.Title)) { Source = session });

        var headerUrl = new TextBlock
        {
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11
        };
        headerUrl.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(WindowSession.InitialUri)) { Source = session });

        var headerState = new TextBlock
        {
            Foreground = Brushes.DarkSlateGray,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11
        };
        headerState.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(WindowSession.State)) { Source = session });

        var contentGrid = new Grid();
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var deleteButton = new Button
        {
            Content = "Excluir",
            Width = 72,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            Tag = session
        };
        deleteButton.Click += OnDeleteWindowButtonClick;

        var headerTopRow = new Grid();
        headerTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(headerTitle, 0);
        Grid.SetColumn(deleteButton, 1);
        headerTopRow.Children.Add(headerTitle);
        headerTopRow.Children.Add(deleteButton);

        var header = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 8)
        };
        header.Children.Add(headerTopRow);
        header.Children.Add(headerUrl);
        header.Children.Add(headerState);

        var previewHost = new Border
        {
            Margin = new Thickness(12, 0, 12, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            Child = CreatePreviewContent(session)
        };

        Grid.SetRow(header, 0);
        Grid.SetRow(previewHost, 1);
        contentGrid.Children.Add(header);
        contentGrid.Children.Add(previewHost);

        var card = new Border
        {
            Width = 340,
            Height = 250,
            Margin = new Thickness(0, 0, 12, 12),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            Background = Brushes.White,
            Child = contentGrid,
            Tag = session,
            SnapsToDevicePixels = true
        };

        var contextMenu = new ContextMenu();
        var settingsItem = new MenuItem
        {
            Header = "Configuracoes da janela",
            Tag = session
        };
        settingsItem.Click += OnWindowSettingsClick;
        contextMenu.Items.Add(settingsItem);

        var linkRtcItem = new MenuItem
        {
            Header = "Ir para LinkRTC",
            Tag = session
        };
        linkRtcItem.Click += OnOpenLinkRtcClick;
        contextMenu.Items.Add(linkRtcItem);

        var deleteItem = new MenuItem
        {
            Header = "Excluir painel",
            Tag = session
        };
        deleteItem.Click += OnDeleteWindowClick;
        contextMenu.Items.Add(deleteItem);

        card.ContextMenu = contextMenu;
        card.MouseLeftButtonDown += OnPreviewCardMouseLeftButtonDown;

        _previewCards[session.Id] = card;
        PreviewPanel.Children.Add(card);
    }

    private UIElement CreatePreviewContent(WindowSession session)
    {
        if (AppRuntimeState.BrowserEngineAvailable)
        {
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            _previewImages[session.Id] = image;
            return image;
        }

        var stack = new StackPanel
        {
            Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(new TextBlock
        {
            Text = "Visualizacao embutida indisponivel nesta maquina.",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        stack.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Text = AppRuntimeState.BrowserEngineStatusMessage,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap
        });

        stack.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Text = session.InitialUri?.ToString() ?? "about:blank",
            Foreground = Brushes.DarkSlateGray,
            TextWrapping = TextWrapping.Wrap
        });

        return stack;
    }

    private void RemovePreview(WindowSession session)
    {
        session.PropertyChanged -= OnWindowSessionPropertyChanged;

        if (_previewCards.TryGetValue(session.Id, out var card))
        {
            if (card.Child is Grid contentGrid &&
                contentGrid.Children.Count > 0 &&
                contentGrid.Children[0] is StackPanel headerStack &&
                headerStack.Children.Count > 0 &&
                headerStack.Children[0] is Grid headerTopRow)
            {
                foreach (var child in headerTopRow.Children)
                {
                    if (child is Button deleteButton)
                    {
                        deleteButton.Click -= OnDeleteWindowButtonClick;
                    }
                }
            }

            if (card.ContextMenu is not null)
            {
                foreach (var item in card.ContextMenu.Items)
                {
                    if (item is MenuItem menuItem)
                    {
                        menuItem.Click -= OnWindowSettingsClick;
                        menuItem.Click -= OnOpenLinkRtcClick;
                        menuItem.Click -= OnDeleteWindowClick;
                    }
                }
            }

            card.MouseLeftButtonDown -= OnPreviewCardMouseLeftButtonDown;
            PreviewPanel.Children.Remove(card);
            _previewCards.Remove(session.Id);
        }

        _previewImages.Remove(session.Id);

        if (_captureWindows.TryGetValue(session.Id, out var captureWindow))
        {
            _browserSnapshotService.Unregister(session.Id);
            captureWindow.Close();
            _captureWindows.Remove(session.Id);
        }

        if (_expandedPreviewWindowId == session.Id)
        {
            CloseExpandedPreview();
        }
    }

    private void OnPreviewCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not WindowSession session)
        {
            return;
        }

        _viewModel.SelectedWindow = session;

        if (e.ClickCount >= 2)
        {
            ToggleExpandedPreview(session);
        }
    }

    private async void OnWindowSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not WindowSession session)
        {
            return;
        }

        _viewModel.SelectedWindow = session;

        var dialogViewModel = new WindowConfigurationViewModel(session, _viewModel.Targets);
        var dialog = new WindowSettingsDialog(dialogViewModel)
        {
            Owner = this
        };

        var result = dialog.ShowDialog();
        if (result == true)
        {
            await _viewModel.ApplyWindowSettingsAsync(
                session,
                dialogViewModel.Nickname,
                dialogViewModel.SelectedTarget,
                dialogViewModel.IsWebRtcEnabled);
        }
    }

    private async void OnOpenLinkRtcClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not WindowSession session)
        {
            return;
        }

        _viewModel.SelectedWindow = session;
        await _viewModel.OpenWindowLinkRtcAsync(session);
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        if (_logWindow is not null)
        {
            _logWindow.Activate();
            return;
        }

        _logWindow = new TransmissionLogWindow
        {
            Owner = this
        };

        _logWindow.Closed += (_, __) => _logWindow = null;
        _logWindow.Show();
    }

    private async void OnExportApplicationDataClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salvar backup da base do super",
            Filter = "Arquivo zip (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = string.Format("super-base-{0:yyyyMMdd-HHmmss}.zip", DateTime.Now)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await _viewModel.ExportApplicationDataAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Falha ao salvar backup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnImportApplicationDataClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Restaurar backup da base do super",
            Filter = "Arquivo zip (*.zip)|*.zip",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "A restauracao vai substituir a base local atual. Deseja continuar?",
            "Restaurar backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _viewModel.ImportApplicationDataAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Falha ao restaurar backup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnResetApplicationDataClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "Isso vai apagar perfis, TVs conhecidas e preferencias locais do aplicativo. Deseja continuar?",
            "Resetar base local",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _viewModel.ResetApplicationDataAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Falha ao resetar base", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDeleteWindowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not WindowSession session)
        {
            return;
        }

        _viewModel.SelectedWindow = session;
        _viewModel.DeleteSelectedWindowCommand.Execute(null);
    }

    private void OnDeleteWindowButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not WindowSession session)
        {
            return;
        }

        _viewModel.SelectedWindow = session;
        _viewModel.DeleteSelectedWindowCommand.Execute(null);
        e.Handled = true;
    }

    private void ToggleExpandedPreview(WindowSession session)
    {
        if (_expandedPreviewWindowId == session.Id)
        {
            CloseExpandedPreview();
            return;
        }

        if (!AppRuntimeState.BrowserEngineAvailable)
        {
            return;
        }

        CloseExpandedPreview();
        ExpandedPreviewTitle.Text = string.Format("Visualizacao ampliada - {0}", session.Title);
        ExpandedPreviewOverlay.Visibility = Visibility.Visible;
        _expandedPreviewWindowId = session.Id;
        ExpandedPreviewHost.Focus();
        _ = RefreshPreviewImagesAsync();
    }

    private void CloseExpandedPreview()
    {
        if (!_expandedPreviewWindowId.HasValue)
        {
            return;
        }

        var windowId = _expandedPreviewWindowId.Value;
        _expandedPreviewWindowId = null;
        _lastExpandedPreviewMousePoint = null;
        ExpandedPreviewOverlay.Visibility = Visibility.Collapsed;
        ExpandedPreviewTitle.Text = string.Empty;
        ExpandedPreviewImage.Source = null;
    }

    private void OnWindowSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not WindowSession session)
        {
            return;
        }

        if (e.PropertyName == nameof(WindowSession.InitialUri))
        {
            _browserSnapshotService.InvalidateCapture(session.Id);
            if (_captureWindows.TryGetValue(session.Id, out var captureWindow))
            {
                captureWindow.UpdateAddress(session.InitialUri);
            }

            _ = Dispatcher.InvokeAsync(RefreshPreviewImagesAsync);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedWindow))
        {
            UpdateSelectionVisuals();
        }
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var pair in _previewCards)
        {
            var isSelected = _viewModel.SelectedWindow?.Id == pair.Key;
            pair.Value.BorderBrush = isSelected
                ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
                : new SolidColorBrush(Color.FromRgb(209, 213, 219));
            pair.Value.Background = isSelected
                ? new SolidColorBrush(Color.FromRgb(239, 246, 255))
                : Brushes.White;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnLoaded;
        _viewModel.Windows.CollectionChanged -= OnWindowsCollectionChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _previewRefreshTimer.Stop();
        _previewRefreshTimer.Tick -= OnPreviewRefreshTimerTick;

        foreach (var window in _viewModel.Windows)
        {
            window.PropertyChanged -= OnWindowSessionPropertyChanged;
        }

        _logWindow?.Close();
        CloseExpandedPreview();

        foreach (var captureWindow in _captureWindows.Values)
        {
            captureWindow.Close();
        }
        _captureWindows.Clear();

        base.OnClosed(e);
    }

    private void EnsureCaptureWindow(WindowSession session)
    {
        if (!AppRuntimeState.BrowserEngineAvailable || _captureWindows.ContainsKey(session.Id))
        {
            return;
        }

        var captureWindow = new BrowserCaptureWindow(session.InitialUri);
        _captureWindows[session.Id] = captureWindow;
        _browserSnapshotService.Register(session.Id, captureWindow.Browser);
        captureWindow.Show();
    }

    private void OnCloseExpandedPreviewClick(object sender, RoutedEventArgs e)
    {
        CloseExpandedPreview();
    }

    private async void OnExpandedPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_expandedPreviewWindowId.HasValue)
        {
            return;
        }

        var point = e.GetPosition(ExpandedPreviewImage);
        if (!TryMapExpandedPreviewPoint(point, out var mappedPoint))
        {
            return;
        }

        if (_lastExpandedPreviewMousePoint.HasValue)
        {
            var previous = _lastExpandedPreviewMousePoint.Value;
            if (Math.Abs(previous.X - mappedPoint.X) < 2 && Math.Abs(previous.Y - mappedPoint.Y) < 2)
            {
                return;
            }
        }

        _lastExpandedPreviewMousePoint = mappedPoint;
        await _browserSnapshotService.SendRemoteCommandAsync(
            _expandedPreviewWindowId.Value,
            "move",
            (int)Math.Round(mappedPoint.X),
            (int)Math.Round(mappedPoint.Y),
            null,
            default);
    }

    private async void OnExpandedPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_expandedPreviewWindowId.HasValue)
        {
            return;
        }

        ExpandedPreviewHost.Focus();

        var point = e.GetPosition(ExpandedPreviewImage);
        if (!TryMapExpandedPreviewPoint(point, out var mappedPoint))
        {
            return;
        }

        _lastExpandedPreviewMousePoint = mappedPoint;
        await _browserSnapshotService.SendRemoteCommandAsync(
            _expandedPreviewWindowId.Value,
            "click",
            (int)Math.Round(mappedPoint.X),
            (int)Math.Round(mappedPoint.Y),
            null,
            default);
    }

    private async void OnExpandedPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_expandedPreviewWindowId.HasValue)
        {
            return;
        }

        if (e.Key == Key.System)
        {
            return;
        }

        if (IsTextProducingKey(e.Key))
        {
            return;
        }

        var handled = await _browserSnapshotService.SendKeyInputAsync(_expandedPreviewWindowId.Value, e.Key, default);
        if (handled)
        {
            e.Handled = true;
        }
    }

    private async void OnExpandedPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_expandedPreviewWindowId.HasValue || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        var handled = await _browserSnapshotService.SendTextInputAsync(_expandedPreviewWindowId.Value, e.Text, default);
        if (handled)
        {
            e.Handled = true;
        }
    }

    private async void OnPreviewRefreshTimerTick(object? sender, EventArgs e)
    {
        await RefreshPreviewImagesAsync();
    }

    private async Task RefreshPreviewImagesAsync()
    {
        if (_isRefreshingPreviews || !AppRuntimeState.BrowserEngineAvailable)
        {
            return;
        }

        _isRefreshingPreviews = true;
        try
        {
            foreach (var session in _viewModel.Windows.ToArray())
            {
                var jpegBytes = await _browserSnapshotService.CaptureJpegAsync(session.Id, default);
                if (jpegBytes is null || jpegBytes.Length == 0)
                {
                    continue;
                }

                var imageSource = CreateImageSource(jpegBytes);
                if (_previewImages.TryGetValue(session.Id, out var image))
                {
                    image.Source = imageSource;
                }

                if (_expandedPreviewWindowId == session.Id)
                {
                    ExpandedPreviewImage.Source = imageSource;
                }
            }
        }
        finally
        {
            _isRefreshingPreviews = false;
        }
    }

    private static BitmapImage CreateImageSource(byte[] jpegBytes)
    {
        using var stream = new MemoryStream(jpegBytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool IsTextProducingKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            return true;
        }

        if (key >= Key.D0 && key <= Key.D9)
        {
            return true;
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return true;
        }

        switch (key)
        {
            case Key.Space:
            case Key.OemMinus:
            case Key.OemPlus:
            case Key.OemOpenBrackets:
            case Key.Oem6:
            case Key.Oem5:
            case Key.Oem1:
            case Key.Oem7:
            case Key.OemComma:
            case Key.OemPeriod:
            case Key.Oem2:
            case Key.Oem3:
            case Key.Decimal:
                return true;
            default:
                return false;
        }
    }

    private bool TryMapExpandedPreviewPoint(Point point, out Point mappedPoint)
    {
        mappedPoint = default;
        if (ExpandedPreviewImage.Source is not BitmapSource source)
        {
            return false;
        }

        var containerWidth = ExpandedPreviewImage.ActualWidth;
        var containerHeight = ExpandedPreviewImage.ActualHeight;
        if (containerWidth <= 0 || containerHeight <= 0 || source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return false;
        }

        var sourceAspect = (double)source.PixelWidth / source.PixelHeight;
        var containerAspect = containerWidth / containerHeight;

        double renderWidth;
        double renderHeight;
        double offsetX;
        double offsetY;

        if (containerAspect > sourceAspect)
        {
            renderHeight = containerHeight;
            renderWidth = renderHeight * sourceAspect;
            offsetX = (containerWidth - renderWidth) / 2;
            offsetY = 0;
        }
        else
        {
            renderWidth = containerWidth;
            renderHeight = renderWidth / sourceAspect;
            offsetX = 0;
            offsetY = (containerHeight - renderHeight) / 2;
        }

        if (point.X < offsetX || point.Y < offsetY || point.X > offsetX + renderWidth || point.Y > offsetY + renderHeight)
        {
            return false;
        }

        var normalizedX = (point.X - offsetX) / renderWidth;
        var normalizedY = (point.Y - offsetY) / renderHeight;
        mappedPoint = new Point(normalizedX * source.PixelWidth, normalizedY * source.PixelHeight);
        return true;
    }
}

