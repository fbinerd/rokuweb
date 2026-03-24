using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using WindowManager.App.Runtime.Publishing;
using WindowManager.Core.Models;

namespace WindowManager.App.Profiles;

[DataContract]
public sealed class AppProfile
{
    [DataMember(Order = 1)]
    public string Name { get; set; } = "default";

    [DataMember(Order = 2)]
    public int SchemaVersion { get; set; } = AppProfileSchema.CurrentVersion;

    [DataMember(Order = 3)]
    public Guid? SelectedWindowId { get; set; }

    [DataMember(Order = 4)]
    public Guid? SelectedTargetId { get; set; }

    [DataMember(Order = 5)]
    public List<WindowSessionProfile> Windows { get; set; } = new List<WindowSessionProfile>();

    [DataMember(Order = 6)]
    public List<StaticPanelProfile> StaticPanels { get; set; } = new List<StaticPanelProfile>();

    [DataMember(Order = 7)]
    public int WebRtcServerPort { get; set; } = 8090;

    [DataMember(Order = 8)]
    public WebRtcBindMode WebRtcBindMode { get; set; } = WebRtcBindMode.Lan;

    [DataMember(Order = 9)]
    public string WebRtcSpecificIp { get; set; } = string.Empty;

    [DataMember(Order = 10)]
    public List<DisplayTargetProfile> DisplayTargets { get; set; } = new List<DisplayTargetProfile>();

    [DataMember(Order = 11)]
    public List<DisplayBindingProfile> DisplayBindings { get; set; } = new List<DisplayBindingProfile>();

    [DataMember(Order = 12)]
    public List<TvProfileDefinition> TvProfiles { get; set; } = new List<TvProfileDefinition>();

    [DataMember(Order = 13)]
    public List<WindowGroupProfile> WindowProfiles { get; set; } = new List<WindowGroupProfile>();

    [DataMember(Order = 14)]
    public List<ActiveSessionRecord> ActiveSessions { get; set; } = new List<ActiveSessionRecord>();

    [DataMember(Order = 15)]
    public List<BrowserProfileDefinition> BrowserProfiles { get; set; } = new List<BrowserProfileDefinition>();
}

[DataContract]
public sealed class WindowSessionProfile
{
    [DataMember(Order = 1)]
    public Guid Id { get; set; }

    [DataMember(Order = 2)]
    public string Title { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string InitialUrl { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public WindowSessionState State { get; set; }

    [DataMember(Order = 5)]
    public Guid? AssignedTargetId { get; set; }

    [DataMember(Order = 6)]
    public RenderResolutionMode BrowserResolutionMode { get; set; }

    [DataMember(Order = 7)]
    public int BrowserManualWidth { get; set; }

    [DataMember(Order = 8)]
    public int BrowserManualHeight { get; set; }

    [DataMember(Order = 9)]
    public RenderResolutionMode TargetResolutionMode { get; set; }

    [DataMember(Order = 10)]
    public int TargetManualWidth { get; set; }

    [DataMember(Order = 11)]
    public int TargetManualHeight { get; set; }

    [DataMember(Order = 12)]
    public bool IsWebRtcPublishingEnabled { get; set; }

    [DataMember(Order = 13)]
    public string ProfileName { get; set; } = string.Empty;

    [DataMember(Order = 14)]
    public Guid ActiveSessionId { get; set; }

    [DataMember(Order = 15)]
    public string ActiveSessionName { get; set; } = string.Empty;

    [DataMember(Order = 16)]
    public bool IsNavigationBarEnabled { get; set; }

    [DataMember(Order = 17)]
    public string BrowserProfileName { get; set; } = string.Empty;
}

[DataContract]
public sealed class StaticPanelProfile
{
    [DataMember(Order = 1)]
    public Guid Id { get; set; }

    [DataMember(Order = 2)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public Guid DisplayTargetId { get; set; }

    [DataMember(Order = 4)]
    public string DisplayName { get; set; } = string.Empty;

    [DataMember(Order = 5)]
    public Guid? PreferredWindowId { get; set; }

    [DataMember(Order = 6)]
    public string PreferredRouteNickname { get; set; } = string.Empty;

    [DataMember(Order = 7)]
    public bool IsWebRtcEnabled { get; set; }
}

[DataContract]
public sealed class DisplayTargetProfile
{
    [DataMember(Order = 1)]
    public Guid Id { get; set; }

    [DataMember(Order = 2)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string NetworkAddress { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public string LastKnownNetworkAddress { get; set; } = string.Empty;

    [DataMember(Order = 5)]
    public string MacAddress { get; set; } = string.Empty;

    [DataMember(Order = 6)]
    public List<string> AlternateMacAddresses { get; set; } = new List<string>();

    [DataMember(Order = 7)]
    public string DeviceUniqueId { get; set; } = string.Empty;

    [DataMember(Order = 8)]
    public string DiscoverySource { get; set; } = string.Empty;

    [DataMember(Order = 9)]
    public DisplayTransportKind TransportKind { get; set; }

    [DataMember(Order = 10)]
    public bool IsOnline { get; set; }

    [DataMember(Order = 11)]
    public bool WasPreviouslyKnown { get; set; }

    [DataMember(Order = 12)]
    public bool IsStaticTarget { get; set; }

    [DataMember(Order = 13)]
    public int NativeWidth { get; set; } = 1920;

    [DataMember(Order = 14)]
    public int NativeHeight { get; set; } = 1080;
}

[DataContract]
public sealed class DisplayBindingProfile
{
    [DataMember(Order = 1)]
    public Guid Id { get; set; }

    [DataMember(Order = 2)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public Guid DisplayTargetId { get; set; }

    [DataMember(Order = 4)]
    public string DisplayTargetName { get; set; } = string.Empty;

    [DataMember(Order = 5)]
    public string DeviceUniqueId { get; set; } = string.Empty;

    [DataMember(Order = 6)]
    public string NetworkAddress { get; set; } = string.Empty;
}

[DataContract]
public sealed class TvProfileDefinition
{
    [DataMember(Order = 1)]
    public Guid Id { get; set; }

    [DataMember(Order = 2)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public List<TvProfileTargetDefinition> Targets { get; set; } = new List<TvProfileTargetDefinition>();
}

[DataContract]
public sealed class TvProfileTargetDefinition
{
    [DataMember(Order = 1)]
    public Guid DisplayTargetId { get; set; }

    [DataMember(Order = 2)]
    public string DisplayName { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string NetworkAddress { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public string DeviceUniqueId { get; set; } = string.Empty;

    [DataMember(Order = 5)]
    public string MacAddress { get; set; } = string.Empty;

    [DataMember(Order = 6)]
    public List<string> AlternateMacAddresses { get; set; } = new List<string>();

    [DataMember(Order = 7)]
    public string DiscoverySource { get; set; } = string.Empty;

    [DataMember(Order = 8)]
    public int NativeWidth { get; set; } = 1920;

    [DataMember(Order = 9)]
    public int NativeHeight { get; set; } = 1080;
}

[DataContract]
public sealed class WindowGroupProfile
{
    [DataMember(Order = 1)]
    public Guid Id { get; set; }

    [DataMember(Order = 2)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public Guid? AssignedTvProfileId { get; set; }

    [DataMember(Order = 4)]
    public string AssignedTvProfileName { get; set; } = string.Empty;

    [DataMember(Order = 5)]
    public bool KeepDisplayConnected { get; set; }

    [DataMember(Order = 6)]
    public List<WindowLinkProfile> Windows { get; set; } = new List<WindowLinkProfile>();

    [DataMember(Order = 7)]
    public string BrowserProfileName { get; set; } = string.Empty;
}

[DataContract]
public sealed class WindowLinkProfile
{
    [DataMember(Order = 1)]
    public Guid Id { get; set; }

    [DataMember(Order = 2)]
    public string Nickname { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string Url { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public bool IsEnabled { get; set; }

    [DataMember(Order = 5)]
    public bool IsPrimaryExclusive { get; set; }

    [DataMember(Order = 6)]
    public bool IsNavigationBarEnabled { get; set; }
}

[DataContract]
public sealed class BrowserProfileDefinition
{
    [DataMember(Order = 1)]
    public string Name { get; set; } = string.Empty;
}
