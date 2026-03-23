using System;
using System.Threading.Tasks;
using System.Windows;
using CefSharp.Wpf;
using WindowManager.App.Runtime.Publishing;

namespace WindowManager.App;

public sealed class BrowserCaptureWindow : Window
{
    public BrowserCaptureWindow(Guid windowId, Uri? initialUri, BrowserAudioCaptureService audioCaptureService)
    {
        Width = 1280;
        Height = 720;
        Left = -20000;
        Top = 0;
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;

        Browser = new ChromiumWebBrowser
        {
            Address = initialUri?.ToString() ?? "about:blank"
        };
        Browser.AudioHandler = audioCaptureService.CreateHandler(windowId);

        Content = Browser;
    }

    public ChromiumWebBrowser Browser { get; }

    public static Task ApplyNavigationBarPreferenceAsync(ChromiumWebBrowser browser, bool enabled)
    {
        return Task.CompletedTask;
    }

    public void UpdateAddress(Uri? address)
    {
        var nextAddress = address?.ToString() ?? "about:blank";
        if (!string.Equals(Browser.Address, nextAddress, StringComparison.OrdinalIgnoreCase))
        {
            Browser.Address = nextAddress;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Browser.Dispose();
        base.OnClosed(e);
    }
}
