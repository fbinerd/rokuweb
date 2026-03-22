using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WindowManager.Core.Models;

namespace WindowManager.App.ViewModels;

public sealed class TvProfileSetupViewModel : ViewModelBase
{
    private string _profileName = string.Empty;
    private string _manualIp = string.Empty;
    private DisplayTarget? _selectedAvailableTarget;
    private TvProfileTargetEditorViewModel? _selectedIncludedTarget;

    public TvProfileSetupViewModel(IEnumerable<DisplayTarget> targets, TvProfileViewModel? existingProfile = null)
    {
        AvailableTargets = new ObservableCollection<DisplayTarget>(targets.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));

        if (existingProfile is not null)
        {
            ProfileName = existingProfile.Name;
            foreach (var target in existingProfile.Targets)
            {
                IncludedTargets.Add(new TvProfileTargetEditorViewModel
                {
                    DisplayTargetId = target.DisplayTargetId,
                    DisplayName = target.DisplayName,
                    NetworkAddress = target.NetworkAddress,
                    DeviceUniqueId = target.DeviceUniqueId
                });
            }
        }
    }

    public ObservableCollection<DisplayTarget> AvailableTargets { get; }

    public ObservableCollection<TvProfileTargetEditorViewModel> IncludedTargets { get; } = new ObservableCollection<TvProfileTargetEditorViewModel>();

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }

    public string ManualIp
    {
        get => _manualIp;
        set => SetProperty(ref _manualIp, value);
    }

    public DisplayTarget? SelectedAvailableTarget
    {
        get => _selectedAvailableTarget;
        set => SetProperty(ref _selectedAvailableTarget, value);
    }

    public TvProfileTargetEditorViewModel? SelectedIncludedTarget
    {
        get => _selectedIncludedTarget;
        set => SetProperty(ref _selectedIncludedTarget, value);
    }

    public void AddSelectedTarget()
    {
        if (SelectedAvailableTarget is null)
        {
            return;
        }

        if (IncludedTargets.Any(x =>
                x.DisplayTargetId == SelectedAvailableTarget.Id ||
                (!string.IsNullOrWhiteSpace(x.NetworkAddress) &&
                 string.Equals(x.NetworkAddress, SelectedAvailableTarget.NetworkAddress, StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }

        IncludedTargets.Add(new TvProfileTargetEditorViewModel
        {
            DisplayTargetId = SelectedAvailableTarget.Id,
            DisplayName = SelectedAvailableTarget.Name,
            NetworkAddress = SelectedAvailableTarget.NetworkAddress,
            DeviceUniqueId = SelectedAvailableTarget.DeviceUniqueId
        });
    }

    public bool AddManualIp()
    {
        var manualIp = ManualIp?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(manualIp))
        {
            return false;
        }

        if (IncludedTargets.Any(x => string.Equals(x.NetworkAddress, manualIp, StringComparison.OrdinalIgnoreCase)))
        {
            ManualIp = string.Empty;
            return false;
        }

        IncludedTargets.Add(new TvProfileTargetEditorViewModel
        {
            DisplayTargetId = Guid.Empty,
            DisplayName = string.Format("TV {0}", manualIp),
            NetworkAddress = manualIp,
            DeviceUniqueId = string.Empty
        });
        ManualIp = string.Empty;
        return true;
    }

    public void RemoveSelectedTarget()
    {
        if (SelectedIncludedTarget is null)
        {
            return;
        }

        IncludedTargets.Remove(SelectedIncludedTarget);
        SelectedIncludedTarget = IncludedTargets.FirstOrDefault();
    }
}

public sealed class WindowProfileSetupViewModel : ViewModelBase
{
    private string _profileName = string.Empty;
    private string _nickname = string.Empty;
    private string _url = string.Empty;
    private TvProfileViewModel? _selectedTvProfile;
    private WindowLinkEditorViewModel? _selectedWindow;

    public WindowProfileSetupViewModel(IEnumerable<TvProfileViewModel> tvProfiles, WindowProfileViewModel? existingProfile = null)
    {
        AvailableTvProfiles = new ObservableCollection<TvProfileViewModel>(tvProfiles.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));

        if (existingProfile is not null)
        {
            ProfileName = existingProfile.Name;
            foreach (var window in existingProfile.Windows)
            {
                Windows.Add(new WindowLinkEditorViewModel
                {
                    Id = window.Id,
                    Nickname = window.Nickname,
                    Url = window.Url
                });
            }

            SelectedTvProfile = AvailableTvProfiles.FirstOrDefault(x => x.Id == existingProfile.AssignedTvProfileId);
        }
    }

    public ObservableCollection<TvProfileViewModel> AvailableTvProfiles { get; }

    public ObservableCollection<WindowLinkEditorViewModel> Windows { get; } = new ObservableCollection<WindowLinkEditorViewModel>();

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }

    public string Nickname
    {
        get => _nickname;
        set => SetProperty(ref _nickname, value);
    }

    public string Url
    {
        get => _url;
        set => SetProperty(ref _url, value);
    }

    public TvProfileViewModel? SelectedTvProfile
    {
        get => _selectedTvProfile;
        set => SetProperty(ref _selectedTvProfile, value);
    }

    public WindowLinkEditorViewModel? SelectedWindow
    {
        get => _selectedWindow;
        set => SetProperty(ref _selectedWindow, value);
    }

    public bool AddWindow()
    {
        var nickname = Nickname?.Trim() ?? string.Empty;
        var url = Url?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(nickname) || string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        Windows.Add(new WindowLinkEditorViewModel
        {
            Id = Guid.NewGuid(),
            Nickname = nickname,
            Url = url
        });

        Nickname = string.Empty;
        Url = string.Empty;
        return true;
    }

    public void RemoveSelectedWindow()
    {
        if (SelectedWindow is null)
        {
            return;
        }

        Windows.Remove(SelectedWindow);
        SelectedWindow = Windows.FirstOrDefault();
    }
}

public sealed class TvProfileTargetEditorViewModel : ViewModelBase
{
    private Guid _displayTargetId;
    private string _displayName = string.Empty;
    private string _networkAddress = string.Empty;
    private string _deviceUniqueId = string.Empty;

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

    public string NetworkAddress
    {
        get => _networkAddress;
        set => SetProperty(ref _networkAddress, value);
    }

    public string DeviceUniqueId
    {
        get => _deviceUniqueId;
        set => SetProperty(ref _deviceUniqueId, value);
    }
}

public sealed class WindowLinkEditorViewModel : ViewModelBase
{
    private Guid _id;
    private string _nickname = string.Empty;
    private string _url = string.Empty;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Nickname
    {
        get => _nickname;
        set => SetProperty(ref _nickname, value);
    }

    public string Url
    {
        get => _url;
        set => SetProperty(ref _url, value);
    }
}
