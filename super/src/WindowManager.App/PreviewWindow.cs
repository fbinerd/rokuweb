using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CefSharp.Wpf;
using WindowManager.App.Runtime;
using WindowManager.Core.Models;

namespace WindowManager.App;

public sealed class PreviewWindow : Window
{
    private readonly WindowSession _session;
    private readonly ChromiumWebBrowser? _browser;
    private readonly Action? _restoreSharedContent;
    private readonly TextBlock? _addressText;

    public PreviewWindow(WindowSession session)
        : this(session, null, null)
    {
    }

    public PreviewWindow(WindowSession session, ChromiumWebBrowser? sharedBrowser, Action? restoreSharedContent)
    {
        _session = session;
        _browser = sharedBrowser;
        _restoreSharedContent = restoreSharedContent;
        _session.PropertyChanged += OnSessionPropertyChanged;

        Title = BuildTitle(session);
        Width = 1200;
        Height = 800;
        MinWidth = 800;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (_browser is not null)
        {
            Content = _browser;
        }
        else if (AppRuntimeState.BrowserEngineAvailable)
        {
            _browser = new ChromiumWebBrowser
            {
                Address = session.InitialUri?.ToString() ?? "about:blank"
            };

            Content = _browser;
        }
        else
        {
            _addressText = new TextBlock
            {
                Text = session.InitialUri?.ToString() ?? "about:blank",
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            var stack = new StackPanel
            {
                Margin = new Thickness(24)
            };

            stack.Children.Add(new TextBlock
            {
                Text = "Visualizacao ampliada sem CEF nesta maquina.",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 10, 0, 0),
                Text = AppRuntimeState.BrowserEngineStatusMessage,
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(_addressText);
            Content = stack;
        }

        Closed += OnClosed;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WindowSession.Title) || e.PropertyName == nameof(WindowSession.InitialUri))
        {
            Dispatcher.Invoke(() =>
            {
                Title = BuildTitle(_session);
                var address = _session.InitialUri?.ToString() ?? "about:blank";
                if (_browser is not null && !string.Equals(_browser.Address, address, StringComparison.OrdinalIgnoreCase))
                {
                    _browser.Address = address;
                }

                if (_addressText is not null)
                {
                    _addressText.Text = address;
                }
            });
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _restoreSharedContent?.Invoke();
        if (_restoreSharedContent is null)
        {
            _browser?.Dispose();
        }
    }

    private static string BuildTitle(WindowSession session)
    {
        return $"Visualizacao ampliada - {session.Title}";
    }
}
