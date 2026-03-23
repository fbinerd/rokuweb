using System;
using System.Windows;
using CefSharp;
using CefSharp.Wpf;
using WindowManager.App.Runtime.Publishing;

namespace WindowManager.App;

public sealed class BrowserCaptureWindow : Window
{
    private const string InjectedNavBarScript = @"
(function () {
  try {
    var barId = '__super_nav_bar__';
    var styleId = '__super_nav_bar_style__';
    if (!document.body) {
      return;
    }
    if (!document.getElementById(styleId)) {
      var style = document.createElement('style');
      style.id = styleId;
      style.textContent =
        '#' + barId + '{position:fixed;top:8px;left:8px;right:8px;height:40px;display:flex;gap:8px;align-items:center;padding:6px 8px;background:rgba(255,255,255,0.96);border:1px solid rgba(148,163,184,0.9);border-radius:10px;box-shadow:0 8px 24px rgba(15,23,42,0.12);z-index:2147483647;font-family:Segoe UI,Arial,sans-serif;box-sizing:border-box;}' +
        '#' + barId + ' button{height:28px;min-width:72px;border:1px solid #cbd5e1;border-radius:8px;background:#f8fafc;color:#0f172a;font-size:12px;cursor:pointer;}' +
        '#' + barId + ' input{flex:1;height:28px;border:1px solid #cbd5e1;border-radius:8px;padding:0 10px;background:#fff;color:#0f172a;font-size:12px;box-sizing:border-box;}' +
        '#' + barId + ' input[readonly]{cursor:default;}';
      document.documentElement.appendChild(style);
    }

    if (!document.body.hasAttribute('data-super-nav-original-padding-top')) {
      document.body.setAttribute('data-super-nav-original-padding-top', document.body.style.paddingTop || '');
    }
    document.body.style.paddingTop = '56px';

    var bar = document.getElementById(barId);
    if (!bar) {
      bar = document.createElement('div');
      bar.id = barId;

      var back = document.createElement('button');
      back.type = 'button';
      back.textContent = 'Voltar';
      back.onclick = function(ev){ ev.preventDefault(); ev.stopPropagation(); history.back(); };

      var forward = document.createElement('button');
      forward.type = 'button';
      forward.textContent = 'Avancar';
      forward.onclick = function(ev){ ev.preventDefault(); ev.stopPropagation(); history.forward(); };

      var address = document.createElement('input');
      address.type = 'text';
      address.readOnly = true;
      address.tabIndex = -1;

      bar.appendChild(back);
      bar.appendChild(forward);
      bar.appendChild(address);
      document.body.appendChild(bar);
    }

    var addressBox = bar.querySelector('input');
    var updateAddress = function(){ if(addressBox){ addressBox.value = window.location.href || ''; } };
    updateAddress();

    if (!window.__superNavHooksInstalled) {
      window.__superNavHooksInstalled = true;
      window.addEventListener('popstate', updateAddress, true);
      window.addEventListener('hashchange', updateAddress, true);
      var originalPushState = history.pushState;
      history.pushState = function () {
        var result = originalPushState.apply(this, arguments);
        setTimeout(updateAddress, 0);
        return result;
      };
      var originalReplaceState = history.replaceState;
      history.replaceState = function () {
        var result = originalReplaceState.apply(this, arguments);
        setTimeout(updateAddress, 0);
        return result;
      };
      setInterval(updateAddress, 1000);
    }
  } catch (e) {
  }
})();";
    private const string RemoveNavBarScript = @"
(function () {
  try {
    var bar = document.getElementById('__super_nav_bar__');
    if (bar && bar.parentNode) {
      bar.parentNode.removeChild(bar);
    }

    var style = document.getElementById('__super_nav_bar_style__');
    if (style && style.parentNode) {
      style.parentNode.removeChild(style);
    }

    if (document.body) {
      var originalPaddingTop = document.body.getAttribute('data-super-nav-original-padding-top');
      if (originalPaddingTop !== null) {
        document.body.style.paddingTop = originalPaddingTop;
        document.body.removeAttribute('data-super-nav-original-padding-top');
      } else {
        document.body.style.paddingTop = '';
      }
    }
  } catch (e) {
  }
})();";
    private bool _isNavigationBarEnabled;

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
        Browser.FrameLoadEnd += OnBrowserFrameLoadEnd;

        Content = Browser;
    }

    public ChromiumWebBrowser Browser { get; }

    public bool IsNavigationBarEnabled => _isNavigationBarEnabled;

    public void UpdateAddress(Uri? address)
    {
        var nextAddress = address?.ToString() ?? "about:blank";
        if (!string.Equals(Browser.Address, nextAddress, StringComparison.OrdinalIgnoreCase))
        {
            Browser.Address = nextAddress;
        }
    }

    public void SetNavigationBarEnabled(bool enabled)
    {
        if (_isNavigationBarEnabled == enabled)
        {
            return;
        }

        _isNavigationBarEnabled = enabled;
        _ = ApplyNavigationBarPreferenceAsync(Browser, _isNavigationBarEnabled);
    }

    protected override void OnClosed(EventArgs e)
    {
        Browser.FrameLoadEnd -= OnBrowserFrameLoadEnd;
        Browser.Dispose();
        base.OnClosed(e);
    }

    internal static Task ApplyNavigationBarPreferenceAsync(ChromiumWebBrowser browser, bool enabled)
    {
        if (browser is null)
        {
            return Task.CompletedTask;
        }

        return browser.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                browser.ExecuteScriptAsync(enabled ? InjectedNavBarScript : RemoveNavBarScript);
            }
            catch
            {
            }
        }).Task;
    }

    private void OnBrowserFrameLoadEnd(object? sender, FrameLoadEndEventArgs e)
    {
        if (!e.Frame.IsMain)
        {
            return;
        }

        try
        {
            e.Frame.ExecuteJavaScriptAsync(_isNavigationBarEnabled ? InjectedNavBarScript : RemoveNavBarScript);
        }
        catch
        {
        }
    }
}
