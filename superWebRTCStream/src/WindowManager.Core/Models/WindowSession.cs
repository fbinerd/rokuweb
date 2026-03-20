using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowManager.Core.Models;

public sealed class WindowSession : INotifyPropertyChanged
{
    private string _title = "Nova janela";
    private Uri? _initialUri;
    private nint _nativeHandle;
    private WindowSessionState _state = WindowSessionState.Created;
    private DisplayTarget? _assignedTarget;
    private RenderResolutionMode _browserResolutionMode = RenderResolutionMode.Automatic;
    private int _browserManualWidth = 1920;
    private int _browserManualHeight = 1080;
    private RenderResolutionMode _targetResolutionMode = RenderResolutionMode.Automatic;
    private int _targetManualWidth = 1920;
    private int _targetManualHeight = 1080;
    private bool _isWebRtcPublishingEnabled;
    private string _publishedWebRtcUrl = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public Uri? InitialUri
    {
        get => _initialUri;
        set => SetProperty(ref _initialUri, value);
    }

    public nint NativeHandle
    {
        get => _nativeHandle;
        set => SetProperty(ref _nativeHandle, value);
    }

    public WindowSessionState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    public DisplayTarget? AssignedTarget
    {
        get => _assignedTarget;
        set => SetProperty(ref _assignedTarget, value);
    }

    public RenderResolutionMode BrowserResolutionMode
    {
        get => _browserResolutionMode;
        set => SetProperty(ref _browserResolutionMode, value);
    }

    public int BrowserManualWidth
    {
        get => _browserManualWidth;
        set => SetProperty(ref _browserManualWidth, value);
    }

    public int BrowserManualHeight
    {
        get => _browserManualHeight;
        set => SetProperty(ref _browserManualHeight, value);
    }

    public RenderResolutionMode TargetResolutionMode
    {
        get => _targetResolutionMode;
        set => SetProperty(ref _targetResolutionMode, value);
    }

    public int TargetManualWidth
    {
        get => _targetManualWidth;
        set => SetProperty(ref _targetManualWidth, value);
    }

    public int TargetManualHeight
    {
        get => _targetManualHeight;
        set => SetProperty(ref _targetManualHeight, value);
    }

    public bool IsWebRtcPublishingEnabled
    {
        get => _isWebRtcPublishingEnabled;
        set => SetProperty(ref _isWebRtcPublishingEnabled, value);
    }

    public string PublishedWebRtcUrl
    {
        get => _publishedWebRtcUrl;
        set => SetProperty(ref _publishedWebRtcUrl, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value))
        {
            return;
        }

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
