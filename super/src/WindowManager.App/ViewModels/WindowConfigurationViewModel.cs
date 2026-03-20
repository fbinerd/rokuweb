using System;
using System.Collections.ObjectModel;
using WindowManager.Core.Models;

namespace WindowManager.App.ViewModels;

public sealed class WindowConfigurationViewModel : ViewModelBase
{
    private string _nickname = string.Empty;
    private DisplayTarget? _selectedTarget;
    private bool _isWebRtcEnabled;

    public WindowConfigurationViewModel(WindowSession session, ObservableCollection<DisplayTarget> targets)
    {
        Session = session;
        Targets = targets;
        Nickname = session.Title;
        SelectedTarget = session.AssignedTarget;
        IsWebRtcEnabled = session.IsWebRtcPublishingEnabled;
    }

    public WindowSession Session { get; }

    public ObservableCollection<DisplayTarget> Targets { get; }

    public string Nickname
    {
        get => _nickname;
        set => SetProperty(ref _nickname, value);
    }

    public DisplayTarget? SelectedTarget
    {
        get => _selectedTarget;
        set => SetProperty(ref _selectedTarget, value);
    }

    public bool IsWebRtcEnabled
    {
        get => _isWebRtcEnabled;
        set => SetProperty(ref _isWebRtcEnabled, value);
    }
}
