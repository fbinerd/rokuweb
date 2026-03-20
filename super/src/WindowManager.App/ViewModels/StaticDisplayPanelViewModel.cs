using System;

namespace WindowManager.App.ViewModels;

public sealed class StaticDisplayPanelViewModel : ViewModelBase
{
    private string _name = string.Empty;
    private Guid _displayTargetId;
    private string _displayName = string.Empty;
    private Guid? _preferredWindowId;
    private string _preferredRouteNickname = string.Empty;
    private bool _isWebRtcEnabled;

    public Guid Id { get; set; } = Guid.NewGuid();

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

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public Guid? PreferredWindowId
    {
        get => _preferredWindowId;
        set => SetProperty(ref _preferredWindowId, value);
    }

    public string PreferredRouteNickname
    {
        get => _preferredRouteNickname;
        set => SetProperty(ref _preferredRouteNickname, value);
    }

    public bool IsWebRtcEnabled
    {
        get => _isWebRtcEnabled;
        set => SetProperty(ref _isWebRtcEnabled, value);
    }
}
