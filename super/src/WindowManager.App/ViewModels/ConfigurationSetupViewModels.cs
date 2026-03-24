using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WindowManager.App.Runtime.Discovery;
using WindowManager.Core.Models;

namespace WindowManager.App.ViewModels;

public sealed class TvProfileSetupViewModel : ViewModelBase
{
    private string _profileName = string.Empty;
    private string _manualIp = string.Empty;
    private DisplayTarget? _selectedAvailableTarget;
    private TvProfileTargetEditorViewModel? _selectedIncludedTarget;
    private DisplayTarget? _selectedAssociationTarget;
    private bool _isRefreshingTargets;
    private readonly Func<Task<IReadOnlyList<DisplayTarget>>>? _refreshTargetsAsync;
    private readonly Func<DisplayTarget, Task<DisplayTarget>>? _resolveCurrentTargetAsync;

    public TvProfileSetupViewModel(
        IEnumerable<DisplayTarget> targets,
        TvProfileViewModel? existingProfile = null,
        Func<Task<IReadOnlyList<DisplayTarget>>>? refreshTargetsAsync = null,
        Func<DisplayTarget, Task<DisplayTarget>>? resolveCurrentTargetAsync = null)
    {
        _refreshTargetsAsync = refreshTargetsAsync;
        _resolveCurrentTargetAsync = resolveCurrentTargetAsync;
        AvailableTargets = new ObservableCollection<DisplayTarget>();
        ReplaceAvailableTargets(targets);
        ProfileName = string.Empty;
        ManualIp = string.Empty;
        SelectedAvailableTarget = null;
        SelectedIncludedTarget = null;
        SelectedAssociationTarget = null;
        IncludedTargets.Clear();

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
                    AlternateMacAddresses = target.AlternateMacAddresses.ToList(),
                    DiscoverySource = target.DiscoverySource,
                    NativeWidth = target.NativeWidth,
                    NativeHeight = target.NativeHeight
                });
            }

            SelectedIncludedTarget = IncludedTargets.FirstOrDefault();
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

    public bool IsRefreshingTargets
    {
        get => _isRefreshingTargets;
        set => SetProperty(ref _isRefreshingTargets, value);
    }

    public async Task RefreshAvailableTargetsAsync()
    {
        if (_refreshTargetsAsync is null)
        {
            return;
        }

        IsRefreshingTargets = true;
        try
        {
            var refreshedTargets = await _refreshTargetsAsync();
            ReplaceAvailableTargets(refreshedTargets);
            await ReconcileIncludedTargetAsync();
        }
        finally
        {
            IsRefreshingTargets = false;
        }
    }

    public void AddSelectedTarget()
    {
        if (SelectedAvailableTarget is null)
        {
            return;
        }

        IncludedTargets.Clear();
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

        IncludedTargets.Clear();
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
            .Concat(new[] { SelectedAssociationTarget.MacAddress })
            .Concat(SelectedAssociationTarget.AlternateMacAddresses ?? Enumerable.Empty<string>())
            .Select(MacAddressFormatter.Normalize)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (macsToMerge.Count == 0)
        {
            return false;
        }

        SelectedIncludedTarget.DisplayTargetId = SelectedAssociationTarget.Id;
        SelectedIncludedTarget.DisplayName = SelectedAssociationTarget.Name;
        SelectedIncludedTarget.NetworkAddress = SelectedAssociationTarget.NetworkAddress;
        SelectedIncludedTarget.DeviceUniqueId = SelectedAssociationTarget.DeviceUniqueId;
        SelectedIncludedTarget.MacAddress = string.IsNullOrWhiteSpace(SelectedAssociationTarget.MacAddress)
            ? macsToMerge.FirstOrDefault() ?? string.Empty
            : SelectedAssociationTarget.MacAddress;
        SelectedIncludedTarget.AlternateMacAddresses = macsToMerge
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

    private void ReplaceAvailableTargets(IEnumerable<DisplayTarget> targets)
    {
        var selectedAvailableId = SelectedAvailableTarget?.Id;
        var selectedAssociationId = SelectedAssociationTarget?.Id;
        var safeTargets = targets ?? Enumerable.Empty<DisplayTarget>();

        AvailableTargets.Clear();
        foreach (var target in safeTargets.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            AvailableTargets.Add(target);
        }

        SelectedAvailableTarget = selectedAvailableId is null
            ? null
            : AvailableTargets.FirstOrDefault(x => x.Id == selectedAvailableId.Value);
        SelectedAssociationTarget = selectedAssociationId is null
            ? null
            : AvailableTargets.FirstOrDefault(x => x.Id == selectedAssociationId.Value);
    }

    private async Task ReconcileIncludedTargetAsync()
    {
        if (_resolveCurrentTargetAsync is null || SelectedIncludedTarget is null)
        {
            return;
        }

        var resolved = await _resolveCurrentTargetAsync(new DisplayTarget
        {
            Id = SelectedIncludedTarget.DisplayTargetId,
            Name = SelectedIncludedTarget.DisplayName,
            NetworkAddress = SelectedIncludedTarget.NetworkAddress,
            DeviceUniqueId = SelectedIncludedTarget.DeviceUniqueId,
            MacAddress = SelectedIncludedTarget.MacAddress,
            AlternateMacAddresses = SelectedIncludedTarget.AlternateMacAddresses.ToList(),
            DiscoverySource = SelectedIncludedTarget.DiscoverySource,
            NativeWidth = SelectedIncludedTarget.NativeWidth,
            NativeHeight = SelectedIncludedTarget.NativeHeight,
            TransportKind = DisplayTransportKind.LanStreaming,
            IsStaticTarget = true
        });

        SelectedIncludedTarget.DisplayTargetId = resolved.Id;
        SelectedIncludedTarget.DisplayName = resolved.Name;
        SelectedIncludedTarget.NetworkAddress = resolved.NetworkAddress;
        SelectedIncludedTarget.DeviceUniqueId = resolved.DeviceUniqueId;
        SelectedIncludedTarget.MacAddress = resolved.MacAddress;
        SelectedIncludedTarget.AlternateMacAddresses = resolved.AlternateMacAddresses.ToList();
        SelectedIncludedTarget.DiscoverySource = resolved.DiscoverySource;
        SelectedIncludedTarget.NativeWidth = resolved.NativeWidth;
        SelectedIncludedTarget.NativeHeight = resolved.NativeHeight;
    }
}

public sealed class WindowProfileSetupViewModel : ViewModelBase
{
    private string _profileName = string.Empty;
    private TvProfileViewModel? _selectedTvProfile;
    private WindowLinkEditorViewModel? _selectedWindow;
    private string _selectedBrowserProfileName = string.Empty;
    private readonly Func<string, Task<BrowserProfileMutationResult>>? _createBrowserProfileAsync;
    private readonly Func<string, Task<BrowserProfileMutationResult>>? _deleteBrowserProfileAsync;

    public WindowProfileSetupViewModel(
        IEnumerable<TvProfileViewModel> tvProfiles,
        IEnumerable<string> browserProfiles,
        WindowProfileViewModel? existingProfile = null,
        Func<string, Task<BrowserProfileMutationResult>>? createBrowserProfileAsync = null,
        Func<string, Task<BrowserProfileMutationResult>>? deleteBrowserProfileAsync = null)
    {
        _createBrowserProfileAsync = createBrowserProfileAsync;
        _deleteBrowserProfileAsync = deleteBrowserProfileAsync;
        AvailableTvProfiles = new ObservableCollection<TvProfileViewModel>(tvProfiles.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));
        AvailableBrowserProfiles = new ObservableCollection<string>(browserProfiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        if (existingProfile is not null)
        {
            EditingProfileId = existingProfile.Id;
            ProfileName = existingProfile.Name;
            SelectedBrowserProfileName = existingProfile.BrowserProfileName;
            foreach (var window in GetDistinctWindows(
                existingProfile.Windows.Select(x => new WindowLinkEditorViewModel
                {
                    Id = x.Id,
                    Nickname = x.Nickname,
                    Url = x.Url,
                    IsEnabled = x.IsEnabled,
                    IsPrimaryExclusive = x.IsPrimaryExclusive,
                    IsNavigationBarEnabled = x.IsNavigationBarEnabled
                })))
            {
                Windows.Add(new WindowLinkEditorViewModel
                {
                    Id = window.Id,
                    Nickname = window.Nickname,
                    Url = window.Url,
                    IsEnabled = window.IsEnabled,
                    IsPrimaryExclusive = window.IsPrimaryExclusive,
                    IsNavigationBarEnabled = window.IsNavigationBarEnabled,
                    Number = Windows.Count + 1
                });
            }

            SelectedTvProfile = AvailableTvProfiles.FirstOrDefault(x => x.Id == existingProfile.AssignedTvProfileId);
        }
    }

    public ObservableCollection<TvProfileViewModel> AvailableTvProfiles { get; }

    public ObservableCollection<string> AvailableBrowserProfiles { get; }

    public ObservableCollection<WindowLinkEditorViewModel> Windows { get; } = new ObservableCollection<WindowLinkEditorViewModel>();

    public Guid? EditingProfileId { get; }

    public IReadOnlyList<WindowLinkEditorViewModel> GetDistinctWindowDefinitions()
    {
        return GetDistinctWindows(Windows).ToList();
    }

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
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

    public string SelectedBrowserProfileName
    {
        get => _selectedBrowserProfileName;
        set => SetProperty(ref _selectedBrowserProfileName, value);
    }

    public async Task<BrowserProfileMutationResult> CreateBrowserProfileAsync(string? browserProfileName)
    {
        if (_createBrowserProfileAsync is null)
        {
            return BrowserProfileMutationResult.Fail("Criacao de perfil de navegador indisponivel.");
        }

        var result = await _createBrowserProfileAsync(browserProfileName ?? string.Empty);
        if (!result.Succeeded)
        {
            return result;
        }

        InsertBrowserProfileSorted(result.ProfileName);
        SelectedBrowserProfileName = result.ProfileName;
        return result;
    }

    public async Task<BrowserProfileMutationResult> DeleteSelectedBrowserProfileAsync()
    {
        if (_deleteBrowserProfileAsync is null)
        {
            return BrowserProfileMutationResult.Fail("Exclusao de perfil de navegador indisponivel.");
        }

        var profileName = SelectedBrowserProfileName?.Trim() ?? string.Empty;
        var result = await _deleteBrowserProfileAsync(profileName);
        if (!result.Succeeded)
        {
            return result;
        }

        var existing = AvailableBrowserProfiles.FirstOrDefault(x => string.Equals(x, result.ProfileName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            AvailableBrowserProfiles.Remove(existing);
        }

        if (string.Equals(SelectedBrowserProfileName, result.ProfileName, StringComparison.OrdinalIgnoreCase))
        {
            SelectedBrowserProfileName = string.Empty;
        }

        return result;
    }

    public void AddOrUpdateWindow(string? nickname, string? url, WindowLinkEditorViewModel? existingWindow = null)
    {
        var normalizedUrl = url?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            return;
        }

        var normalizedNickname = nickname?.Trim() ?? string.Empty;
        var resolvedNickname = string.IsNullOrWhiteSpace(normalizedNickname)
            ? GetNextDefaultWindowNickname()
            : normalizedNickname;

        if (existingWindow is not null)
        {
            existingWindow.Nickname = resolvedNickname;
            existingWindow.Url = normalizedUrl;
            SelectedWindow = existingWindow;
            return;
        }

        var window = new WindowLinkEditorViewModel
        {
            Id = Guid.NewGuid(),
            Nickname = resolvedNickname,
            Url = normalizedUrl,
            Number = Windows.Count + 1
        };

        Windows.Add(window);
        SelectedWindow = window;
        RenumberWindows();
    }

    public void RemoveSelectedWindow()
    {
        if (SelectedWindow is null)
        {
            return;
        }

        Windows.Remove(SelectedWindow);
        SelectedWindow = Windows.FirstOrDefault();
        RenumberWindows();
    }

    private string GetNextDefaultWindowNickname()
    {
        var usedNumbers = Windows
            .Select(x => x.Nickname?.Trim() ?? string.Empty)
            .Where(x => x.StartsWith("Janela ", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Substring(7))
            .Select(x => int.TryParse(x, out var number) ? number : -1)
            .Where(x => x > 0)
            .ToHashSet();

        var next = 1;
        while (usedNumbers.Contains(next))
        {
            next++;
        }

        return string.Format("Janela {0}", next);
    }

    private void RenumberWindows()
    {
        for (var index = 0; index < Windows.Count; index++)
        {
            Windows[index].Number = index + 1;
        }
    }

    private static IEnumerable<WindowLinkEditorViewModel> GetDistinctWindows(IEnumerable<WindowLinkEditorViewModel> windows)
    {
        var seenIds = new HashSet<Guid>();
        var seenFallbacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var window in windows)
        {
            if (window is null)
            {
                continue;
            }

            if (window.Id != Guid.Empty)
            {
                if (!seenIds.Add(window.Id))
                {
                    continue;
                }
            }
            else
            {
                var fallbackKey = string.Format(
                    "{0}|{1}",
                    window.Nickname?.Trim() ?? string.Empty,
                    window.Url?.Trim() ?? string.Empty);

                if (!seenFallbacks.Add(fallbackKey))
                {
                    continue;
                }
            }

            yield return window;
        }
    }

    private void InsertBrowserProfileSorted(string profileName)
    {
        if (AvailableBrowserProfiles.Any(x => string.Equals(x, profileName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var insertIndex = 0;
        while (insertIndex < AvailableBrowserProfiles.Count &&
               string.Compare(AvailableBrowserProfiles[insertIndex], profileName, StringComparison.OrdinalIgnoreCase) < 0)
        {
            insertIndex++;
        }

        AvailableBrowserProfiles.Insert(insertIndex, profileName);
    }
}

public sealed class BrowserProfileMutationResult
{
    public bool Succeeded { get; set; }

    public string Message { get; set; } = string.Empty;

    public string ProfileName { get; set; } = string.Empty;

    public static BrowserProfileMutationResult Success(string profileName, string message)
    {
        return new BrowserProfileMutationResult
        {
            Succeeded = true,
            Message = message,
            ProfileName = profileName
        };
    }

    public static BrowserProfileMutationResult Fail(string message)
    {
        return new BrowserProfileMutationResult
        {
            Succeeded = false,
            Message = message
        };
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
            var normalized = MacAddressFormatter.Normalize(value);
            if (SetProperty(ref _macAddress, normalized))
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
            var normalized = MacAddressFormatter.NormalizeMany(value);
            if (SetProperty(ref _alternateMacAddresses, normalized))
            {
                RaisePropertyChanged(nameof(MacSummary));
            }
        }
    }

    public string MacSummary
    {
        get
        {
            var macs = MacAddressFormatter.NormalizeMany(new[] { MacAddress }.Concat(AlternateMacAddresses ?? Enumerable.Empty<string>())).ToArray();
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
    private int _number;
    private bool _isEnabled;
    private bool _isPrimaryExclusive;
    private bool _isNavigationBarEnabled;

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

    public int Number
    {
        get => _number;
        set
        {
            if (SetProperty(ref _number, value))
            {
                RaisePropertyChanged(nameof(NumberLabel));
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool IsPrimaryExclusive
    {
        get => _isPrimaryExclusive;
        set => SetProperty(ref _isPrimaryExclusive, value);
    }

    public bool IsNavigationBarEnabled
    {
        get => _isNavigationBarEnabled;
        set => SetProperty(ref _isNavigationBarEnabled, value);
    }

    public string NumberLabel => Number <= 0 ? string.Empty : Number.ToString();
}

public sealed class StreamWindowEditorViewModel : ViewModelBase
{
    private string _nickname = string.Empty;
    private string _url = string.Empty;

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
