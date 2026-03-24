using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Data;
using WindowManager.App.Runtime;
using WindowManager.App.Runtime.Publishing;
using WindowManager.App.ViewModels;
using WindowManager.Core.Models;
using Microsoft.Win32;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using Forms = System.Windows.Forms;

namespace WindowManager.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly BrowserSnapshotService _browserSnapshotService;
    private readonly BrowserAudioCaptureService _browserAudioCaptureService;
    private readonly Dictionary<Guid, Border> _previewCards = new Dictionary<Guid, Border>();
    private readonly Dictionary<Guid, Image> _previewImages = new Dictionary<Guid, Image>();
    private readonly Dictionary<Guid, BrowserCaptureWindow> _captureWindows = new Dictionary<Guid, BrowserCaptureWindow>();
    private readonly Dictionary<Guid, Border> _streamDefinitionPreviewCards = new Dictionary<Guid, Border>();
    private readonly Dictionary<Guid, Image> _streamDefinitionPreviewImages = new Dictionary<Guid, Image>();
    private readonly Dictionary<Guid, BrowserCaptureWindow> _streamDefinitionCaptureWindows = new Dictionary<Guid, BrowserCaptureWindow>();
    private readonly Dictionary<Guid, string> _streamDefinitionSectionKeys = new Dictionary<Guid, string>();
    private readonly Dictionary<string, GroupBox> _streamPreviewGroups = new Dictionary<string, GroupBox>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WrapPanel> _streamPreviewPanels = new Dictionary<string, WrapPanel>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _streamPreviewPlaceholders = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _previewRefreshTimer;
    private readonly DispatcherTimer _expandedPreviewRefreshTimer;
    private readonly Forms.NotifyIcon _notifyIcon;
    private TransmissionLogWindow? _logWindow;
    private Guid? _expandedPreviewWindowId;
    private bool _isRefreshingPreviews;
    private bool _allowApplicationExit;
    private bool _hasShownTrayHint;
    private Point? _lastExpandedPreviewMousePoint;
    private DateTime _nextKeepAliveCheckUtc = DateTime.MinValue;
    private bool _isRefreshingExpandedPreview;
    private const string UnassignedStreamSection = "__UNASSIGNED_STREAMS__";

    public MainWindow(MainViewModel viewModel, BrowserSnapshotService browserSnapshotService, BrowserAudioCaptureService browserAudioCaptureService)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _browserSnapshotService = browserSnapshotService;
        _browserAudioCaptureService = browserAudioCaptureService;
        DataContext = viewModel;

        _viewModel.Windows.CollectionChanged += OnWindowsCollectionChanged;
        _viewModel.WindowProfiles.CollectionChanged += OnWindowProfilesCollectionChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        foreach (var stream in _viewModel.WindowProfiles)
        {
            stream.PropertyChanged += OnWindowProfilePropertyChanged;
            stream.Windows.CollectionChanged += OnWindowProfileWindowsCollectionChanged;
            foreach (var item in stream.Windows)
            {
                item.PropertyChanged += OnWindowProfileItemPropertyChanged;
                AddStreamDefinitionPreview(stream, item);
            }
        }

        RefreshStreamPreviewSections();

        foreach (var window in _viewModel.Windows)
        {
            AddPreview(window);
        }

        _previewRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _previewRefreshTimer.Tick += OnPreviewRefreshTimerTick;

        _expandedPreviewRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _expandedPreviewRefreshTimer.Tick += OnExpandedPreviewRefreshTimerTick;

        _notifyIcon = CreateNotifyIcon();

        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        UpdateSelectionVisuals();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _viewModel.InitializeAfterStartupAsync();
            RefreshStreamProfilesListView();
            await Dispatcher.InvokeAsync(RefreshStreamProfilesListView, DispatcherPriority.Background);
            ApplyPreviewMode();
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

    private void OnWindowProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (WindowProfileViewModel stream in e.NewItems)
            {
                stream.PropertyChanged += OnWindowProfilePropertyChanged;
                stream.Windows.CollectionChanged += OnWindowProfileWindowsCollectionChanged;
                foreach (WindowProfileItemViewModel item in stream.Windows)
                {
                    item.PropertyChanged += OnWindowProfileItemPropertyChanged;
                    AddStreamDefinitionPreview(stream, item);
                }
            }
        }

        if (e.OldItems is not null)
        {
            foreach (WindowProfileViewModel stream in e.OldItems)
            {
                stream.PropertyChanged -= OnWindowProfilePropertyChanged;
                stream.Windows.CollectionChanged -= OnWindowProfileWindowsCollectionChanged;
                foreach (WindowProfileItemViewModel item in stream.Windows)
                {
                    item.PropertyChanged -= OnWindowProfileItemPropertyChanged;
                    RemoveStreamDefinitionPreview(item.Id);
                }
            }
        }

        RefreshStreamPreviewSections();
        RefreshStreamProfilesListView();
    }

    private void RefreshStreamProfilesListView()
    {
        StreamProfilesListBox.Items.Refresh();
        CollectionViewSource.GetDefaultView(StreamProfilesListBox.ItemsSource)?.Refresh();
    }

    private void OnWindowProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WindowProfileViewModel.Name) ||
            e.PropertyName == nameof(WindowProfileViewModel.AssignedTvProfileName))
        {
            RefreshStreamDefinitionPreviewSection(sender as WindowProfileViewModel);
            RefreshStreamPreviewSections();
        }

        if (e.PropertyName == nameof(WindowProfileViewModel.BrowserProfileName) &&
            sender is WindowProfileViewModel streamWithBrowserProfile)
        {
            RecreateStreamDefinitionCaptureWindows(streamWithBrowserProfile);
        }
    }

    private void OnWindowProfileWindowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var stream = _viewModel.WindowProfiles.FirstOrDefault(x => ReferenceEquals(x.Windows, sender));
        if (stream is null)
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            var streamKey = GetStreamKey(stream);
            var existingIds = _streamDefinitionSectionKeys
                .Where(x => string.Equals(x.Value, streamKey, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Key)
                .ToList();

            foreach (var itemId in existingIds)
            {
                RemoveStreamDefinitionPreview(itemId);
            }

            foreach (var item in stream.Windows)
            {
                item.PropertyChanged -= OnWindowProfileItemPropertyChanged;
                item.PropertyChanged += OnWindowProfileItemPropertyChanged;
                AddStreamDefinitionPreview(stream, item);
            }

            RefreshStreamPreviewSections();
            return;
        }

        if (e.NewItems is not null)
        {
            foreach (WindowProfileItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnWindowProfileItemPropertyChanged;
                AddStreamDefinitionPreview(stream, item);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (WindowProfileItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnWindowProfileItemPropertyChanged;
                RemoveStreamDefinitionPreview(item.Id);
            }
        }

        RefreshStreamPreviewSections();
    }

    private void OnWindowProfileItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not WindowProfileItemViewModel item)
        {
            return;
        }

        if (e.PropertyName == nameof(WindowProfileItemViewModel.Url))
        {
            if (_viewModel.ShowWindowPreviews && _streamDefinitionCaptureWindows.TryGetValue(item.Id, out var captureWindow))
            {
                captureWindow.UpdateAddress(TryCreateUri(item.Url));
                _browserSnapshotService.InvalidateCapture(item.Id);
                _ = Dispatcher.InvokeAsync(RefreshPreviewImagesAsync);
            }
        }

        if (e.PropertyName == nameof(WindowProfileItemViewModel.Url) ||
            e.PropertyName == nameof(WindowProfileItemViewModel.Nickname) ||
            e.PropertyName == nameof(WindowProfileItemViewModel.IsEnabled) ||
            e.PropertyName == nameof(WindowProfileItemViewModel.IsPrimaryExclusive))
        {
            RefreshStreamDefinitionPreviewCardHeader(item);

            var session = _viewModel.Windows.FirstOrDefault(x => x.Id == item.Id);
            if (session is not null)
            {
                RefreshRuntimePreviewCardHeader(session);
            }
        }
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

        var streamToggle = new CheckBox
        {
            Content = "Transmitir",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = true,
            Tag = session.Id
        };
        streamToggle.Click += OnStreamWindowEnabledToggleClick;

        var exclusiveToggle = new CheckBox
        {
            Content = "So esta janela",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = session.IsPrimaryExclusive,
            Tag = session.Id
        };
        exclusiveToggle.Click += OnStreamWindowPrimaryExclusiveToggleClick;

        var reloadButton = new Button
        {
            Content = "Recarregar",
            Margin = new Thickness(0, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = session.Id,
            MinWidth = 84
        };
        reloadButton.Click += OnReloadStreamButtonClick;

        var headerTopRow = new Grid();
        headerTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(streamToggle, 0);
        Grid.SetColumn(exclusiveToggle, 1);
        Grid.SetColumn(reloadButton, 2);
        headerTopRow.Children.Add(streamToggle);
        headerTopRow.Children.Add(exclusiveToggle);
        headerTopRow.Children.Add(reloadButton);

        var header = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 8)
        };
        header.Children.Add(headerTitle);
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

        card.ContextMenu = contextMenu;
        card.MouseLeftButtonDown += OnPreviewCardMouseLeftButtonDown;

        _previewCards[session.Id] = card;
        AttachPreviewCardToSection(session, card);
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
                    if (child is CheckBox checkBox)
                    {
                        checkBox.Click -= OnStreamWindowEnabledToggleClick;
                        checkBox.Click -= OnStreamWindowPrimaryExclusiveToggleClick;
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
                    }
                }
            }

            card.MouseLeftButtonDown -= OnPreviewCardMouseLeftButtonDown;
            if (card.Parent is Panel parentPanel)
            {
                parentPanel.Children.Remove(card);
            }
            _previewCards.Remove(session.Id);
        }

        _previewImages.Remove(session.Id);

        if (_captureWindows.TryGetValue(session.Id, out var captureWindow))
        {
            _browserSnapshotService.Unregister(session.Id);
            _browserAudioCaptureService.Unregister(session.Id);
            captureWindow.Close();
            _captureWindows.Remove(session.Id);
        }

        if (_expandedPreviewWindowId == session.Id)
        {
            CloseExpandedPreview();
        }

        RestoreStreamDefinitionPreviewIfNeeded(session.Id);

        RefreshStreamPreviewSections();
    }

    private void AddStreamDefinitionPreview(WindowProfileViewModel stream, WindowProfileItemViewModel item)
    {
        item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
        RemoveStreamDefinitionPreviewCardOnly(item.Id);
        if (_viewModel.ShowWindowPreviews)
        {
            EnsureStreamDefinitionCaptureWindow(stream, item);
        }

        var headerTitle = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Text = item.Nickname
        };

        var headerUrl = new TextBlock
        {
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Text = item.Url
        };

        var headerState = new TextBlock
        {
            Foreground = Brushes.DarkSlateGray,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11,
            Text = "Previa do stream"
        };

        var streamToggle = new CheckBox
        {
            Content = "Transmitir",
            Margin = new Thickness(0, 0, 0, 6),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = item.IsEnabled,
            Tag = item.Id
        };
        streamToggle.Click += OnStreamWindowEnabledToggleClick;

        var exclusiveToggle = new CheckBox
        {
            Content = "So esta janela",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = item.IsPrimaryExclusive,
            Tag = item.Id
        };
        exclusiveToggle.Click += OnStreamWindowPrimaryExclusiveToggleClick;

        var headerTopRow = new Grid();
        headerTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(streamToggle, 0);
        Grid.SetColumn(exclusiveToggle, 1);
        headerTopRow.Children.Add(streamToggle);
        headerTopRow.Children.Add(exclusiveToggle);

        var header = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 8),
            Tag = item.Id
        };
        header.Children.Add(headerTitle);
        header.Children.Add(headerTopRow);
        header.Children.Add(headerUrl);
        header.Children.Add(headerState);

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        _streamDefinitionPreviewImages[item.Id] = image;

        var previewHost = new Border
        {
            Margin = new Thickness(12, 0, 12, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            Child = image
        };

        var contentGrid = new Grid();
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
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
            Tag = item,
            SnapsToDevicePixels = true
        };
        card.MouseLeftButtonDown += OnStreamDefinitionPreviewMouseLeftButtonDown;

        _streamDefinitionPreviewCards[item.Id] = card;
        _streamDefinitionSectionKeys[item.Id] = GetStreamKey(stream);
        AttachStreamDefinitionPreviewCard(item.Id, card);
        RefreshStreamDefinitionPreviewCardHeader(item);
    }

    private void RemoveStreamDefinitionPreview(Guid itemId)
    {
        RemoveStreamDefinitionPreviewCardOnly(itemId);
        _streamDefinitionSectionKeys.Remove(itemId);

        if (_streamDefinitionCaptureWindows.TryGetValue(itemId, out var captureWindow))
        {
            _browserSnapshotService.Unregister(itemId);
            _browserAudioCaptureService.Unregister(itemId);
            captureWindow.Close();
            _streamDefinitionCaptureWindows.Remove(itemId);
        }
    }

    private void RemoveStreamDefinitionPreviewCardOnly(Guid itemId)
    {
        if (_streamDefinitionPreviewCards.TryGetValue(itemId, out var card))
        {
            card.MouseLeftButtonDown -= OnStreamDefinitionPreviewMouseLeftButtonDown;
            if (card.Child is Grid contentGrid &&
                contentGrid.Children.Count > 0 &&
                contentGrid.Children[0] is StackPanel header)
            {
                foreach (var child in header.Children)
                {
                    if (child is CheckBox checkBox)
                    {
                        checkBox.Click -= OnStreamWindowEnabledToggleClick;
                    }
                }
            }

            if (card.Parent is Panel parentPanel)
            {
                parentPanel.Children.Remove(card);
            }

            _streamDefinitionPreviewCards.Remove(itemId);
        }

        _streamDefinitionPreviewImages.Remove(itemId);
    }

    private void EnsureStreamDefinitionCaptureWindow(WindowProfileViewModel stream, WindowProfileItemViewModel item, bool allowWhenPreviewsDisabled = false)
    {
        if (!AppRuntimeState.BrowserEngineAvailable || (!_viewModel.ShowWindowPreviews && !allowWhenPreviewsDisabled))
        {
            return;
        }

        if (_streamDefinitionCaptureWindows.TryGetValue(item.Id, out var existingCaptureWindow))
        {
            existingCaptureWindow.SetNavigationBarEnabled(item.IsNavigationBarEnabled);
            return;
        }

        var captureWindow = new BrowserCaptureWindow(item.Id, TryCreateUri(item.Url), _browserAudioCaptureService, stream.BrowserProfileName);
        captureWindow.SetNavigationBarEnabled(item.IsNavigationBarEnabled);
        _streamDefinitionCaptureWindows[item.Id] = captureWindow;
        _browserSnapshotService.Register(item.Id, captureWindow.Browser);
        captureWindow.Show();
    }

    private void RecreateStreamDefinitionCaptureWindows(WindowProfileViewModel stream)
    {
        if (!_viewModel.ShowWindowPreviews)
        {
            return;
        }

        foreach (var item in stream.Windows)
        {
            if (_streamDefinitionCaptureWindows.TryGetValue(item.Id, out var captureWindow))
            {
                _browserSnapshotService.Unregister(item.Id);
                _browserAudioCaptureService.Unregister(item.Id);
                captureWindow.Close();
                _streamDefinitionCaptureWindows.Remove(item.Id);
            }

            EnsureStreamDefinitionCaptureWindow(stream, item);
            _browserSnapshotService.InvalidateCapture(item.Id);
        }

        _ = Dispatcher.InvokeAsync(RefreshPreviewImagesAsync);
    }

    private void OnStreamDefinitionPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not WindowProfileItemViewModel item)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            ToggleExpandedPreview(item.Id, item.Nickname);
        }
    }

    private void RestoreStreamDefinitionPreviewIfNeeded(Guid itemId)
    {
        if (_streamDefinitionPreviewCards.ContainsKey(itemId))
        {
            return;
        }

        WindowProfileViewModel? ownerStream = null;
        WindowProfileItemViewModel? ownerItem = null;

        foreach (var stream in _viewModel.WindowProfiles)
        {
            var match = stream.Windows.FirstOrDefault(x => x.Id == itemId);
            if (match is not null)
            {
                ownerStream = stream;
                ownerItem = match;
                break;
            }
        }

        if (ownerStream is null || ownerItem is null)
        {
            return;
        }

        AddStreamDefinitionPreview(ownerStream, ownerItem);
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
            PrepareForApplicationDataReplace();
            await _viewModel.ImportApplicationDataAsync(dialog.FileName);
            RefreshStreamPreviewSections();
            RefreshStreamProfilesListView();
            _ = RefreshPreviewImagesAsync();
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
            PrepareForApplicationDataReplace();
            await _viewModel.ResetApplicationDataAsync();
            RefreshStreamPreviewSections();
            RefreshStreamProfilesListView();
            _ = RefreshPreviewImagesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Falha ao resetar base", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PrepareForApplicationDataReplace()
    {
        CloseExpandedPreview();

        foreach (var captureWindow in _captureWindows.Values.ToList())
        {
            try
            {
                captureWindow.Close();
            }
            catch
            {
            }
        }

        foreach (var captureWindow in _streamDefinitionCaptureWindows.Values.ToList())
        {
            try
            {
                captureWindow.Close();
            }
            catch
            {
            }
        }

        _captureWindows.Clear();
        _streamDefinitionCaptureWindows.Clear();
        _previewCards.Clear();
        _previewImages.Clear();
        _streamDefinitionPreviewCards.Clear();
        _streamDefinitionPreviewImages.Clear();
        _streamDefinitionSectionKeys.Clear();
        _streamPreviewGroups.Clear();
        _streamPreviewPanels.Clear();
        _streamPreviewPlaceholders.Clear();
        StreamPreviewSectionsHost.Children.Clear();
    }

    private void RestartApplicationAfterDataReplace()
    {
        var executablePath = Path.Combine(AppContext.BaseDirectory, "SuperPainel.exe");
        if (File.Exists(executablePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            });
        }

        ExitApplication();
    }

    private void StartBackupRestoreAndRestart(string sourceZipPath)
    {
        if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath))
        {
            throw new FileNotFoundException("Nao foi possivel localizar o backup informado.", sourceZipPath);
        }

        var appDataRoot = AppDataPaths.Root;
        var executablePath = Path.Combine(AppContext.BaseDirectory, "SuperPainel.exe");
        var processId = Process.GetCurrentProcess().Id;
        var tempRoot = Path.Combine(Path.GetTempPath(), "super-restore");
        Directory.CreateDirectory(tempRoot);
        var scriptPath = Path.Combine(tempRoot, string.Format("restore-backup-{0:yyyyMMdd-HHmmssfff}.ps1", DateTime.Now));
        var logPath = Path.Combine(tempRoot, "restore-backup.log");

        string EscapePowerShell(string value) => value.Replace("'", "''");

        var scriptContent = string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'Stop'",
            string.Format("$sourceZip = '{0}'", EscapePowerShell(sourceZipPath)),
            string.Format("$exePath = '{0}'", EscapePowerShell(executablePath)),
            string.Format("$logPath = '{0}'", EscapePowerShell(logPath)),
            string.Format("$processId = {0}", processId),
            "function Write-RestoreLog([string]$message) {",
            "  $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'",
            "  Add-Content -Path $logPath -Value (\"[$timestamp] \" + $message)",
            "}",
            "Write-RestoreLog 'Aguardando encerramento do SuperPainel atual.'",
            "while (Get-Process -Id $processId -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }",
            "Write-RestoreLog 'Encerrando subprocessos remanescentes do CEF.'",
            "try {",
            "  Get-CimInstance Win32_Process -Filter \"Name = 'SuperPainel.exe'\" |",
            "    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith((Split-Path -Parent $exePath), [System.StringComparison]::OrdinalIgnoreCase) } |",
            "    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }",
            "} catch { Write-RestoreLog ('Falha ao encerrar subprocessos: ' + $_.Exception.Message) }",
            "Write-RestoreLog 'Reiniciando SuperPainel em modo de restauracao.'",
            "Start-Process -FilePath $exePath -ArgumentList @('--restore-backup', $sourceZip) -WorkingDirectory (Split-Path -Parent $exePath)",
            "Write-RestoreLog 'Reinicio solicitado com sucesso.'"
        });

        File.WriteAllText(scriptPath, scriptContent);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = string.Format("-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{0}\"", scriptPath),
            WorkingDirectory = tempRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        ExitApplication();
    }

    private async void OnOpenTvProfileSetupClick(object sender, RoutedEventArgs e)
    {
        var dialogViewModel = new TvProfileSetupViewModel(
            _viewModel.Targets,
            refreshTargetsAsync: _viewModel.RefreshTargetsForSetupAsync,
            resolveCurrentTargetAsync: _viewModel.ResolveCurrentTargetForSetupAsync);
        var dialog = new TvProfileSetupDialog(dialogViewModel)
        {
            Owner = this,
            Title = "Novo Perfil de TV"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _viewModel.ApplyTvProfileSetupAsync(dialogViewModel);
    }

    private async void OnEditSelectedTvProfileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var tvProfile = ResolveItemFromEvent<TvProfileViewModel>(e.OriginalSource as DependencyObject) ?? _viewModel.SelectedTvProfile;
        if (tvProfile is null)
        {
            return;
        }

        _viewModel.SelectedTvProfile = tvProfile;

        var dialogViewModel = new TvProfileSetupViewModel(
            _viewModel.Targets,
            tvProfile,
            _viewModel.RefreshTargetsForSetupAsync,
            _viewModel.ResolveCurrentTargetForSetupAsync);
        var dialog = new TvProfileSetupDialog(dialogViewModel)
        {
            Owner = this,
            Title = string.Format("Editar Perfil de TV - {0}", tvProfile.Name)
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _viewModel.ApplyTvProfileSetupAsync(dialogViewModel);
    }

    private static T? ResolveItemFromEvent<T>(DependencyObject? originalSource)
        where T : class
    {
        var current = originalSource;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async void OnOpenWindowProfileSetupClick(object sender, RoutedEventArgs e)
    {
        var dialogViewModel = new WindowProfileSetupViewModel(
            GetAvailableTvProfilesForStream(),
            GetAvailableBrowserProfilesForStream(),
            createBrowserProfileAsync: name => _viewModel.CreateBrowserProfileAsync(name, null),
            deleteBrowserProfileAsync: name => _viewModel.DeleteBrowserProfileAsync(name, null));
        var dialog = new WindowProfileSetupDialog(dialogViewModel, _browserSnapshotService, _browserAudioCaptureService)
        {
            Owner = this,
            Title = "Novo Stream"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _viewModel.ApplyWindowProfileSetupAsync(dialogViewModel);
    }

    private async void OnEditSelectedWindowProfileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var streamProfile = ResolveItemFromEvent<WindowProfileViewModel>(e.OriginalSource as DependencyObject) ?? _viewModel.SelectedWindowProfile;
        if (streamProfile is null)
        {
            return;
        }

        _viewModel.SelectedWindowProfile = streamProfile;

        var dialogViewModel = new WindowProfileSetupViewModel(
            GetAvailableTvProfilesForStream(streamProfile),
            GetAvailableBrowserProfilesForStream(streamProfile),
            streamProfile,
            name => _viewModel.CreateBrowserProfileAsync(name, streamProfile.Id),
            name => _viewModel.DeleteBrowserProfileAsync(name, streamProfile.Id));
        var dialog = new WindowProfileSetupDialog(dialogViewModel, _browserSnapshotService, _browserAudioCaptureService)
        {
            Owner = this,
            Title = string.Format("Editar Stream - {0}", streamProfile.Name)
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _viewModel.ApplyWindowProfileSetupAsync(dialogViewModel);
    }

    private IReadOnlyList<TvProfileViewModel> GetAvailableTvProfilesForStream(WindowProfileViewModel? editingStream = null)
    {
        var occupiedTvProfileIds = _viewModel.WindowProfiles
            .Where(x => editingStream is null || x.Id != editingStream.Id)
            .Select(x => x.AssignedTvProfileId)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToHashSet();

        return _viewModel.TvProfiles
            .Where(x => !occupiedTvProfileIds.Contains(x.Id) ||
                        (editingStream?.AssignedTvProfileId.HasValue == true && editingStream.AssignedTvProfileId.Value == x.Id))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<string> GetAvailableBrowserProfilesForStream(WindowProfileViewModel? editingStream = null)
    {
        var occupiedBrowserProfiles = _viewModel.WindowProfiles
            .Where(x => editingStream is null || x.Id != editingStream.Id)
            .Select(x => x.BrowserProfileName?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _viewModel.BrowserProfiles
            .Select(x => x.Name)
            .Where(x =>
                string.Equals(x, editingStream?.BrowserProfileName, StringComparison.OrdinalIgnoreCase) ||
                !occupiedBrowserProfiles.Contains(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ToggleExpandedPreview(WindowSession session)
    {
        ToggleExpandedPreview(session.Id, session.Title);
    }

    private void ToggleExpandedPreview(Guid previewId, string title)
    {
        if (_expandedPreviewWindowId == previewId)
        {
            CloseExpandedPreview();
            return;
        }

        if (!AppRuntimeState.BrowserEngineAvailable)
        {
            return;
        }

        CloseExpandedPreview();
        ExpandedPreviewTitle.Text = string.Format("Visualizacao ampliada - {0}", title);
        ExpandedPreviewOverlay.Visibility = Visibility.Visible;
        _expandedPreviewWindowId = previewId;
        ExpandedPreviewHost.Focus();
        EnsureExpandedPreviewCaptureWindow(previewId);
        if (_viewModel.ShowWindowPreviews)
        {
            _expandedPreviewRefreshTimer.Stop();
            _ = RefreshPreviewImagesAsync();
        }
        else
        {
            _expandedPreviewRefreshTimer.Start();
            _ = RefreshExpandedPreviewNowAsync(previewId);
        }
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
        _expandedPreviewRefreshTimer.Stop();

        if (!_viewModel.ShowWindowPreviews &&
            _streamDefinitionCaptureWindows.TryGetValue(windowId, out var captureWindow))
        {
            _browserSnapshotService.Unregister(windowId);
            _browserAudioCaptureService.Unregister(windowId);
            captureWindow.Close();
            _streamDefinitionCaptureWindows.Remove(windowId);
        }
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
        else if (e.PropertyName == nameof(WindowSession.IsPrimaryExclusive))
        {
            RefreshRuntimePreviewCardHeader(session);
        }
        else if (e.PropertyName == nameof(WindowSession.IsNavigationBarEnabled))
        {
            if (_captureWindows.TryGetValue(session.Id, out var captureWindow))
            {
                captureWindow.SetNavigationBarEnabled(session.IsNavigationBarEnabled);
            }

            _browserSnapshotService.InvalidateCapture(session.Id);
            _ = Dispatcher.InvokeAsync(RefreshPreviewImagesAsync);
        }
        else if (e.PropertyName == nameof(WindowSession.ProfileName))
        {
            if (_previewCards.TryGetValue(session.Id, out var card))
            {
                AttachPreviewCardToSection(session, card);
            }

            RefreshStreamPreviewSections();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedWindow))
        {
            UpdateSelectionVisuals();
        }

        if (e.PropertyName == nameof(MainViewModel.ShowWindowPreviews))
        {
            ApplyPreviewMode();
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
        StateChanged -= OnStateChanged;
        _viewModel.Windows.CollectionChanged -= OnWindowsCollectionChanged;
        _viewModel.WindowProfiles.CollectionChanged -= OnWindowProfilesCollectionChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _previewRefreshTimer.Stop();
        _previewRefreshTimer.Tick -= OnPreviewRefreshTimerTick;

        foreach (var window in _viewModel.Windows)
        {
            window.PropertyChanged -= OnWindowSessionPropertyChanged;
        }

        foreach (var stream in _viewModel.WindowProfiles)
        {
            stream.PropertyChanged -= OnWindowProfilePropertyChanged;
            stream.Windows.CollectionChanged -= OnWindowProfileWindowsCollectionChanged;
            foreach (var item in stream.Windows)
            {
                item.PropertyChanged -= OnWindowProfileItemPropertyChanged;
            }
        }

        _logWindow?.Close();
        CloseExpandedPreview();

        foreach (var captureWindow in _captureWindows.Values)
        {
            captureWindow.Close();
        }
        _captureWindows.Clear();

        foreach (var captureWindow in _streamDefinitionCaptureWindows.Values)
        {
            captureWindow.Close();
        }
        _streamDefinitionCaptureWindows.Clear();

        _notifyIcon.Visible = false;
        _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
        _notifyIcon.Dispose();

        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowApplicationExit)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    private void EnsureCaptureWindow(WindowSession session)
    {
        if (!AppRuntimeState.BrowserEngineAvailable)
        {
            return;
        }

        if (_captureWindows.TryGetValue(session.Id, out var existingCaptureWindow))
        {
            existingCaptureWindow.SetNavigationBarEnabled(session.IsNavigationBarEnabled);
            return;
        }

        if (_streamDefinitionCaptureWindows.TryGetValue(session.Id, out var existingDefinitionCapture))
        {
            _streamDefinitionCaptureWindows.Remove(session.Id);
            existingDefinitionCapture.SetNavigationBarEnabled(session.IsNavigationBarEnabled);
            _captureWindows[session.Id] = existingDefinitionCapture;
            RemoveStreamDefinitionPreviewCardOnly(session.Id);
            return;
        }

        var captureWindow = new BrowserCaptureWindow(session.Id, session.InitialUri, _browserAudioCaptureService, session.BrowserProfileName);
        captureWindow.SetNavigationBarEnabled(session.IsNavigationBarEnabled);
        _captureWindows[session.Id] = captureWindow;
        _browserSnapshotService.Register(session.Id, captureWindow.Browser);
        captureWindow.Show();
    }

    private void RefreshStreamPreviewSections()
    {
        var liveWindowIds = _viewModel.Windows.Select(x => x.Id).ToHashSet();
        foreach (var liveWindowId in liveWindowIds)
        {
            RemoveStreamDefinitionPreviewCardOnly(liveWindowId);
        }

        var orderedKeys = _viewModel.WindowProfiles
            .Select(GetStreamKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var liveKeys = _viewModel.Windows
            .Select(GetStreamSectionKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var key in liveKeys)
        {
            if (!orderedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                orderedKeys.Add(key);
            }
        }

        if (!orderedKeys.Any())
        {
            orderedKeys.Add(UnassignedStreamSection);
        }

        var obsoleteKeys = _streamPreviewGroups.Keys
            .Where(existingKey => !orderedKeys.Contains(existingKey, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in obsoleteKeys)
        {
            var group = _streamPreviewGroups[key];
            StreamPreviewSectionsHost.Children.Remove(group);
            _streamPreviewGroups.Remove(key);
            _streamPreviewPanels.Remove(key);
            _streamPreviewPlaceholders.Remove(key);
        }

        foreach (var key in orderedKeys)
        {
            EnsureStreamSection(key);
        }

        StreamPreviewSectionsHost.Children.Clear();
        foreach (var key in orderedKeys)
        {
            StreamPreviewSectionsHost.Children.Add(_streamPreviewGroups[key]);
        }

        foreach (var pair in _previewCards.ToArray())
        {
            var session = _viewModel.Windows.FirstOrDefault(x => x.Id == pair.Key);
            if (session is not null)
            {
                AttachPreviewCardToSection(session, pair.Value);
            }
        }

        foreach (var pair in _streamDefinitionPreviewCards.ToArray())
        {
            if (liveWindowIds.Contains(pair.Key))
            {
                continue;
            }

            AttachStreamDefinitionPreviewCard(pair.Key, pair.Value);
        }

        UpdateStreamSectionPlaceholders();
    }

    private void EnsureStreamSection(string key)
    {
        if (_streamPreviewGroups.ContainsKey(key))
        {
            UpdateStreamSectionHeader(key);
            return;
        }

        var panel = new WrapPanel
        {
            ItemWidth = 360,
            ItemHeight = 260
        };

        var placeholder = new TextBlock
        {
            Margin = new Thickness(8),
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Text = "Nenhuma janela ativa neste stream ainda."
        };

        var container = new Grid();
        container.Children.Add(placeholder);
        container.Children.Add(panel);

        var group = new GroupBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            Content = container
        };

        _streamPreviewGroups[key] = group;
        _streamPreviewPanels[key] = panel;
        _streamPreviewPlaceholders[key] = placeholder;
        UpdateStreamSectionHeader(key);
    }

    private void UpdateStreamSectionHeader(string key)
    {
        if (!_streamPreviewGroups.TryGetValue(key, out var group))
        {
            return;
        }

        if (string.Equals(key, UnassignedStreamSection, StringComparison.OrdinalIgnoreCase))
        {
            group.Header = "Janelas Avulsas";
            return;
        }

        var stream = _viewModel.WindowProfiles.FirstOrDefault(x => string.Equals(GetStreamKey(x), key, StringComparison.OrdinalIgnoreCase));
        if (stream is null)
        {
            group.Header = key;
            return;
        }

        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };

        headerPanel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(stream.AssignedTvProfileName)
                ? key
                : string.Format("{0}  |  TV: {1}", key, stream.AssignedTvProfileName),
            VerticalAlignment = VerticalAlignment.Center
        });

        headerPanel.Children.Add(new CheckBox
        {
            Content = "Manter TV ligada e conectada",
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = stream.KeepDisplayConnected,
            Tag = stream.Id
        });

        if (headerPanel.Children[1] is CheckBox keepAliveToggle)
        {
            keepAliveToggle.Click += OnStreamKeepDisplayConnectedToggleClick;
        }

        group.Header = headerPanel;
    }

    private async void OnStreamKeepDisplayConnectedToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not Guid streamId)
        {
            return;
        }

        await _viewModel.SetStreamKeepDisplayConnectedAsync(streamId, checkBox.IsChecked == true);

        var stream = _viewModel.WindowProfiles.FirstOrDefault(x => x.Id == streamId);
        if (stream is not null)
        {
            checkBox.IsChecked = stream.KeepDisplayConnected;
        }
    }

    private async void OnReloadStreamButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not Guid windowId)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await _viewModel.RequestStreamReloadAsync(windowId);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void AttachPreviewCardToSection(WindowSession session, Border card)
    {
        var key = GetStreamSectionKey(session);
        EnsureStreamSection(key);

        if (card.Parent is Panel parentPanel && !ReferenceEquals(parentPanel, _streamPreviewPanels[key]))
        {
            parentPanel.Children.Remove(card);
        }

        if (!_streamPreviewPanels[key].Children.Contains(card))
        {
            _streamPreviewPanels[key].Children.Add(card);
        }

        UpdateStreamSectionPlaceholders();
    }

    private void AttachStreamDefinitionPreviewCard(Guid itemId, Border card)
    {
        if (_viewModel.Windows.Any(x => x.Id == itemId))
        {
            if (card.Parent is Panel attachedPanel)
            {
                attachedPanel.Children.Remove(card);
            }

            return;
        }

        var key = _streamDefinitionSectionKeys.TryGetValue(itemId, out var storedKey) && !string.IsNullOrWhiteSpace(storedKey)
            ? storedKey
            : UnassignedStreamSection;

        EnsureStreamSection(key);

        if (card.Parent is Panel parentPanel && !ReferenceEquals(parentPanel, _streamPreviewPanels[key]))
        {
            parentPanel.Children.Remove(card);
        }

        if (!_streamPreviewPanels[key].Children.Contains(card))
        {
            _streamPreviewPanels[key].Children.Add(card);
        }

        UpdateStreamSectionPlaceholders();
    }

    private void RefreshStreamDefinitionPreviewSection(WindowProfileViewModel? stream)
    {
        if (stream is null)
        {
            return;
        }

        var key = GetStreamKey(stream);
        foreach (var item in stream.Windows)
        {
            _streamDefinitionSectionKeys[item.Id] = key;
            if (_streamDefinitionPreviewCards.TryGetValue(item.Id, out var card))
            {
                AttachStreamDefinitionPreviewCard(item.Id, card);
            }
        }
    }

    private void RefreshStreamDefinitionPreviewCardHeader(WindowProfileItemViewModel item)
    {
        if (!_streamDefinitionPreviewCards.TryGetValue(item.Id, out var card) ||
            card.Child is not Grid contentGrid ||
            contentGrid.Children.Count == 0 ||
            contentGrid.Children[0] is not StackPanel header ||
            header.Children.Count < 4)
        {
            return;
        }

        if (header.Children[1] is Grid headerTopRow &&
            headerTopRow.Children.Count >= 2 &&
            headerTopRow.Children[0] is CheckBox toggle)
        {
            toggle.IsChecked = item.IsEnabled;
        }

        if (header.Children[1] is Grid headerTopRowExclusive &&
            headerTopRowExclusive.Children.Count >= 2 &&
            headerTopRowExclusive.Children[1] is CheckBox exclusiveToggle)
        {
            exclusiveToggle.IsChecked = item.IsPrimaryExclusive;
        }

        if (header.Children[0] is TextBlock titleText)
        {
            titleText.Text = item.Nickname;
        }

        if (header.Children[2] is TextBlock urlText)
        {
            urlText.Text = item.Url;
        }
    }

    private async void OnStreamWindowEnabledToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not Guid itemId)
        {
            return;
        }

        await _viewModel.SetStreamWindowEnabledAsync(itemId, checkBox.IsChecked == true);

        var item = _viewModel.WindowProfiles
            .SelectMany(x => x.Windows)
            .FirstOrDefault(x => x.Id == itemId);

        if (item is not null)
        {
            checkBox.IsChecked = item.IsEnabled;
        }
    }

    private async void OnStreamWindowPrimaryExclusiveToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not Guid itemId)
        {
            return;
        }

        await _viewModel.SetStreamWindowPrimaryExclusiveAsync(itemId, checkBox.IsChecked == true);

        var stream = _viewModel.WindowProfiles.FirstOrDefault(x => x.Windows.Any(w => w.Id == itemId));
        if (stream is null)
        {
            return;
        }

        foreach (var item in stream.Windows)
        {
            RefreshStreamDefinitionPreviewCardHeader(item);

            var session = _viewModel.Windows.FirstOrDefault(x => x.Id == item.Id);
            if (session is not null)
            {
                RefreshRuntimePreviewCardHeader(session);
            }
        }
    }

    private void RefreshRuntimePreviewCardHeader(WindowSession session)
    {
        if (!_previewCards.TryGetValue(session.Id, out var card) ||
            card.Child is not Grid contentGrid ||
            contentGrid.Children.Count == 0 ||
            contentGrid.Children[0] is not StackPanel headerStack ||
            headerStack.Children.Count == 0 ||
            headerStack.Children[0] is not Grid headerTopRow)
        {
            return;
        }

        foreach (var child in headerTopRow.Children)
        {
            if (child is not CheckBox checkBox || checkBox.Tag is not Guid taggedId || taggedId != session.Id)
            {
                continue;
            }

            if (string.Equals(checkBox.Content?.ToString(), "Transmitir", StringComparison.OrdinalIgnoreCase))
            {
                checkBox.IsChecked = true;
            }
            else if (string.Equals(checkBox.Content?.ToString(), "So esta janela", StringComparison.OrdinalIgnoreCase))
            {
                checkBox.IsChecked = session.IsPrimaryExclusive;
            }
        }
    }

    private void UpdateStreamSectionPlaceholders()
    {
        foreach (var key in _streamPreviewPanels.Keys.ToList())
        {
            var hasChildren = _streamPreviewPanels[key].Children.Count > 0;
            _streamPreviewPlaceholders[key].Visibility = hasChildren ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static string GetStreamSectionKey(WindowSession session)
    {
        var profileName = session.ProfileName?.Trim();
        return string.IsNullOrWhiteSpace(profileName) ? UnassignedStreamSection : profileName!;
    }

    private static string GetStreamKey(WindowProfileViewModel stream)
    {
        return stream.Name?.Trim() ?? string.Empty;
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
        await RefreshExpandedPreviewNowAsync(_expandedPreviewWindowId.Value);
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
        await RefreshExpandedPreviewNowAsync(_expandedPreviewWindowId.Value);
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
            await RefreshExpandedPreviewNowAsync(_expandedPreviewWindowId.Value);
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
            await RefreshExpandedPreviewNowAsync(_expandedPreviewWindowId.Value);
            e.Handled = true;
        }
    }

    private async Task RefreshExpandedPreviewNowAsync(Guid windowId)
    {
        var jpegBytes = await CaptureExpandedPreviewFrameAsync(windowId);
        if (jpegBytes is null || jpegBytes.Length == 0)
        {
            return;
        }

        var imageSource = CreateImageSource(jpegBytes);
        ExpandedPreviewImage.Source = imageSource;

        if (_previewImages.TryGetValue(windowId, out var runtimeImage))
        {
            runtimeImage.Source = imageSource;
        }

        if (_streamDefinitionPreviewImages.TryGetValue(windowId, out var definitionImage))
        {
            definitionImage.Source = imageSource;
        }
    }

    private async Task<byte[]?> CaptureExpandedPreviewFrameAsync(Guid windowId)
    {
        const int maxAttempts = 5;
        byte[]? lastFrame = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            _browserSnapshotService.InvalidateCapture(windowId);
            await Task.Delay(attempt == 0 ? 80 : 120);

            var jpegBytes = await _browserSnapshotService.CaptureJpegAsync(windowId, default);
            if (jpegBytes is null || jpegBytes.Length == 0)
            {
                continue;
            }

            lastFrame = jpegBytes;
            if (!IsProbablyBlankPreview(jpegBytes))
            {
                return jpegBytes;
            }
        }

        return lastFrame is not null && !IsProbablyBlankPreview(lastFrame) ? lastFrame : null;
    }

    private async void OnPreviewRefreshTimerTick(object? sender, EventArgs e)
    {
        await RefreshPreviewImagesAsync();
        if (DateTime.UtcNow >= _nextKeepAliveCheckUtc)
        {
            _nextKeepAliveCheckUtc = DateTime.UtcNow.AddSeconds(3);
            await _viewModel.EnsureKeepAliveStreamsAsync();
        }
    }

    private async void OnExpandedPreviewRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_viewModel.ShowWindowPreviews || !_expandedPreviewWindowId.HasValue || _isRefreshingExpandedPreview)
        {
            return;
        }

        try
        {
            _isRefreshingExpandedPreview = true;
            await RefreshExpandedPreviewNowAsync(_expandedPreviewWindowId.Value);
        }
        finally
        {
            _isRefreshingExpandedPreview = false;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void OnNotifyIconDoubleClick(object? sender, EventArgs e)
    {
        RestoreFromTray();
    }

    private void OnOpenFromTrayClick(object? sender, EventArgs e)
    {
        RestoreFromTray();
    }

    private void OnExitFromTrayClick(object? sender, EventArgs e)
    {
        ExitApplication();
    }

    private void OnExitApplicationClick(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private Forms.NotifyIcon CreateNotifyIcon()
    {
        var contextMenu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Abrir SuperPainel");
        openItem.Click += OnOpenFromTrayClick;
        contextMenu.Items.Add(openItem);

        var exitItem = new Forms.ToolStripMenuItem("Encerrar aplicacao");
        exitItem.Click += OnExitFromTrayClick;
        contextMenu.Items.Add(exitItem);

        var icon = TryGetTrayIcon();
        var notifyIcon = new Forms.NotifyIcon
        {
            Text = "SuperPainel",
            Visible = true,
            ContextMenuStrip = contextMenu,
            Icon = icon
        };
        notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
        return notifyIcon;
    }

    private static DrawingIcon TryGetTrayIcon()
    {
        try
        {
            var icon = DrawingIcon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
            if (icon is not null)
            {
                return icon;
            }
        }
        catch
        {
        }

        return DrawingSystemIcons.Application;
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;

        if (!_hasShownTrayHint)
        {
            _notifyIcon.ShowBalloonTip(2500, "SuperPainel", "O aplicativo continua rodando na bandeja do sistema.", Forms.ToolTipIcon.Info);
            _hasShownTrayHint = true;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void ExitApplication()
    {
        _allowApplicationExit = true;
        _notifyIcon.Visible = false;
        Application.Current.Shutdown();
    }

    private async Task RefreshPreviewImagesAsync()
    {
        if (_isRefreshingPreviews || !AppRuntimeState.BrowserEngineAvailable || !_viewModel.ShowWindowPreviews)
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

            foreach (var itemId in _streamDefinitionPreviewImages.Keys.ToArray())
            {
                var jpegBytes = await _browserSnapshotService.CaptureJpegAsync(itemId, default);
                if (jpegBytes is null || jpegBytes.Length == 0)
                {
                    continue;
                }

                var imageSource = CreateImageSource(jpegBytes);
                if (_streamDefinitionPreviewImages.TryGetValue(itemId, out var image))
                {
                    image.Source = imageSource;
                }

                if (_expandedPreviewWindowId == itemId)
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

    private void ApplyPreviewMode()
    {
        if (_viewModel.ShowWindowPreviews)
        {
            EnsureAllStreamDefinitionCaptureWindows();
            _expandedPreviewRefreshTimer.Stop();
            if (!_previewRefreshTimer.IsEnabled)
            {
                _previewRefreshTimer.Start();
            }

            _ = RefreshPreviewImagesAsync();
            return;
        }

        _previewRefreshTimer.Stop();
        CloseExpandedPreview();
        ClearPreviewImages();
        CloseStreamDefinitionCaptureWindows();
    }

    private void EnsureAllStreamDefinitionCaptureWindows()
    {
        foreach (var stream in _viewModel.WindowProfiles)
        {
            foreach (var item in stream.Windows)
            {
                EnsureStreamDefinitionCaptureWindow(stream, item);
            }
        }
    }

    private void CloseStreamDefinitionCaptureWindows()
    {
        foreach (var pair in _streamDefinitionCaptureWindows.ToArray())
        {
            _browserSnapshotService.Unregister(pair.Key);
            _browserAudioCaptureService.Unregister(pair.Key);
            pair.Value.Close();
        }

        _streamDefinitionCaptureWindows.Clear();
    }

    private void ClearPreviewImages()
    {
        foreach (var image in _previewImages.Values)
        {
            image.Source = null;
        }

        foreach (var image in _streamDefinitionPreviewImages.Values)
        {
            image.Source = null;
        }

        ExpandedPreviewImage.Source = null;
    }

    private void EnsureExpandedPreviewCaptureWindow(Guid previewId)
    {
        if (_captureWindows.ContainsKey(previewId))
        {
            return;
        }

        foreach (var stream in _viewModel.WindowProfiles)
        {
            var item = stream.Windows.FirstOrDefault(x => x.Id == previewId);
            if (item is null)
            {
                continue;
            }

            EnsureStreamDefinitionCaptureWindow(stream, item, allowWhenPreviewsDisabled: true);
            return;
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

    private static bool IsProbablyBlankPreview(byte[] jpegBytes)
    {
        try
        {
            using var stream = new MemoryStream(jpegBytes);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                return true;
            }

            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 2 || frame.PixelHeight <= 2)
            {
                return true;
            }

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            var samplePoints = new[]
            {
                new Point(frame.PixelWidth / 2.0, frame.PixelHeight / 2.0),
                new Point(frame.PixelWidth * 0.2, frame.PixelHeight * 0.2),
                new Point(frame.PixelWidth * 0.8, frame.PixelHeight * 0.2),
                new Point(frame.PixelWidth * 0.2, frame.PixelHeight * 0.8),
                new Point(frame.PixelWidth * 0.8, frame.PixelHeight * 0.8)
            };

            var whiteOrBlackSamples = 0;
            foreach (var point in samplePoints)
            {
                var x = Math.Max(0, Math.Min(frame.PixelWidth - 1, (int)Math.Round(point.X)));
                var y = Math.Max(0, Math.Min(frame.PixelHeight - 1, (int)Math.Round(point.Y)));
                var pixels = new byte[4];
                converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);

                var brightness = (pixels[0] + pixels[1] + pixels[2]) / 3;
                if (brightness <= 10 || brightness >= 245)
                {
                    whiteOrBlackSamples++;
                }
            }

            return whiteOrBlackSamples >= 4;
        }
        catch
        {
            return false;
        }
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

    private static Uri? TryCreateUri(string? url)
    {
        var normalized = url?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (!normalized.Contains("://"))
        {
            normalized = "https://" + normalized;
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ? uri : null;
    }
}

