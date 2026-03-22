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
    private DisplayTarget? _selectedAssociationTarget;

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
                    DeviceUniqueId = target.DeviceUniqueId,
                    MacAddress = target.MacAddress,
                    DiscoverySource = target.DiscoverySource,
                    NativeWidth = target.NativeWidth,
                    NativeHeight = target.NativeHeight
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

    public DisplayTarget? SelectedAssociationTarget
    {
        get => _selectedAssociationTarget;
        set => SetProperty(ref _selectedAssociationTarget, value);
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
            DeviceUniqueId = SelectedAvailableTarget.DeviceUniqueId,
            MacAddress = SelectedAvailableTarget.MacAddress,
            AlternateMacAddresses = SelectedAvailableTarget.AlternateMacAddresses.ToList(),
            DiscoverySource = SelectedAvailableTarget.DiscoverySource,
            NativeWidth = SelectedAvailableTarget.NativeWidth,
            NativeHeight = SelectedAvailableTarget.NativeHeight
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
            DeviceUniqueId = string.Empty,
            MacAddress = string.Empty,
            AlternateMacAddresses = new List<string>(),
            DiscoverySource = "Manual",
            NativeWidth = 1920,
            NativeHeight = 1080
        });
        ManualIp = string.Empty;
        return true;
    }

    public bool AssociateSelectedTarget()
    {
        if (SelectedIncludedTarget is null || SelectedAssociationTarget is null)
        {
            return false;
        }

        var macsToMerge = new[] { SelectedIncludedTarget.MacAddress }
            .Concat(SelectedIncludedTarget.AlternateMacAddresses ?? Enumerable.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (macsToMerge.Count == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SelectedAssociationTarget.MacAddress))
        {
            macsToMerge.Add(SelectedAssociationTarget.MacAddress);
        }

        macsToMerge.AddRange(SelectedAssociationTarget.AlternateMacAddresses ?? Enumerable.Empty<string>());
        var mergedMacs = macsToMerge
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        SelectedIncludedTarget.DisplayTargetId = SelectedAssociationTarget.Id;
        SelectedIncludedTarget.DisplayName = SelectedAssociationTarget.Name;
        SelectedIncludedTarget.NetworkAddress = SelectedAssociationTarget.NetworkAddress;
        SelectedIncludedTarget.DeviceUniqueId = SelectedAssociationTarget.DeviceUniqueId;
        SelectedIncludedTarget.MacAddress = string.IsNullOrWhiteSpace(SelectedAssociationTarget.MacAddress)
            ? mergedMacs.FirstOrDefault() ?? string.Empty
            : SelectedAssociationTarget.MacAddress;
        SelectedIncludedTarget.AlternateMacAddresses = mergedMacs
            .Where(x => !string.Equals(x, SelectedIncludedTarget.MacAddress, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SelectedIncludedTarget.DiscoverySource = SelectedAssociationTarget.DiscoverySource;
        SelectedIncludedTarget.NativeWidth = SelectedAssociationTarget.NativeWidth;
        SelectedIncludedTarget.NativeHeight = SelectedAssociationTarget.NativeHeight;
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
    private string _macAddress = string.Empty;
    private List<string> _alternateMacAddresses = new List<string>();
    private string _discoverySource = string.Empty;
    private int _nativeWidth = 1920;
    private int _nativeHeight = 1080;

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

    public string MacAddress
    {
        get => _macAddress;
        set
        {
            if (SetProperty(ref _macAddress, value))
            {
                RaisePropertyChanged(nameof(MacSummary));
            }
        }
    }

    public List<string> AlternateMacAddresses
    {
        get => _alternateMacAddresses;
        set
        {
            if (SetProperty(ref _alternateMacAddresses, value ?? new List<string>()))
            {
                RaisePropertyChanged(nameof(MacSummary));
            }
        }
    }

    public string MacSummary
    {
        get
        {
            var macs = new[] { MacAddress }
                .Concat(AlternateMacAddresses ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return macs.Length == 0 ? "MAC nao identificado" : string.Join(", ", macs);
        }
    }

    public string ResolutionSummary => string.Format("{0}x{1}", NativeWidth, NativeHeight);

    public string DiscoverySource
    {
        get => _discoverySource;
        set => SetProperty(ref _discoverySource, value);
    }

    public int NativeWidth
    {
        get => _nativeWidth;
        set
        {
            if (SetProperty(ref _nativeWidth, value))
            {
                RaisePropertyChanged(nameof(ResolutionSummary));
            }
        }
    }

    public int NativeHeight
    {
        get => _nativeHeight;
        set
        {
            if (SetProperty(ref _nativeHeight, value))
            {
                RaisePropertyChanged(nameof(ResolutionSummary));
            }
        }
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
