using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowManager.App.Runtime.Publishing;

namespace WindowManager.App;

public partial class StreamPreviewWindow : Window
{
    private readonly Guid _windowId;
    private readonly BrowserSnapshotService _browserSnapshotService;
    private readonly bool _allowRemoteInteraction;
    private Point? _lastMousePoint;

    public StreamPreviewWindow(Guid windowId, BrowserSnapshotService browserSnapshotService, bool allowRemoteInteraction = true)
    {
        InitializeComponent();
        _windowId = windowId;
        _browserSnapshotService = browserSnapshotService;
        _allowRemoteInteraction = allowRemoteInteraction;

        if (!_allowRemoteInteraction)
        {
            PreviewHost.Cursor = Cursors.Arrow;
        }
    }

    public Guid WindowId => _windowId;

    public void UpdatePreview(ImageSource? source, string? address, bool showFallback)
    {
        AddressTextBlock.Text = string.IsNullOrWhiteSpace(address)
            ? "Selecione uma janela para visualizar."
            : address;
        PreviewImage.Source = source;
        FallbackTextBlock.Visibility = showFallback ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_allowRemoteInteraction)
        {
            return;
        }

        var point = e.GetPosition(PreviewImage);
        if (!TryMapPoint(point, out var mappedPoint))
        {
            return;
        }

        if (_lastMousePoint.HasValue)
        {
            var previous = _lastMousePoint.Value;
            if (Math.Abs(previous.X - mappedPoint.X) < 2 && Math.Abs(previous.Y - mappedPoint.Y) < 2)
            {
                return;
            }
        }

        _lastMousePoint = mappedPoint;
        await _browserSnapshotService.SendRemoteCommandAsync(
            _windowId,
            "move",
            (int)Math.Round(mappedPoint.X),
            (int)Math.Round(mappedPoint.Y),
            null,
            default);
    }

    private async void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_allowRemoteInteraction)
        {
            return;
        }

        PreviewHost.Focus();

        var point = e.GetPosition(PreviewImage);
        if (!TryMapPoint(point, out var mappedPoint))
        {
            return;
        }

        _lastMousePoint = mappedPoint;
        await _browserSnapshotService.SendRemoteCommandAsync(
            _windowId,
            "click",
            (int)Math.Round(mappedPoint.X),
            (int)Math.Round(mappedPoint.Y),
            null,
            default);
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_allowRemoteInteraction)
        {
            return;
        }

        if (e.Key == Key.System || IsTextProducingKey(e.Key))
        {
            return;
        }

        var handled = await _browserSnapshotService.SendKeyInputAsync(_windowId, e.Key, default);
        if (handled)
        {
            e.Handled = true;
        }
    }

    private async void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_allowRemoteInteraction)
        {
            return;
        }

        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        var handled = await _browserSnapshotService.SendTextInputAsync(_windowId, e.Text, default);
        if (handled)
        {
            e.Handled = true;
        }
    }

    private bool TryMapPoint(Point point, out Point mappedPoint)
    {
        mappedPoint = default;
        if (PreviewImage.Source is not BitmapSource source)
        {
            return false;
        }

        var containerWidth = PreviewImage.ActualWidth;
        var containerHeight = PreviewImage.ActualHeight;
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
}
