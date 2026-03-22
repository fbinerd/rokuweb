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
    public Guid? SelectedWindowId { get; set; }

    [DataMember(Order = 3)]
    public Guid? SelectedTargetId { get; set; }

    [DataMember(Order = 4)]
    public List<WindowSessionProfile> Windows { get; set; } = new List<WindowSessionProfile>();

    [DataMember(Order = 5)]
    public List<StaticPanelProfile> StaticPanels { get; set; } = new List<StaticPanelProfile>();

    [DataMember(Order = 6)]
    public int WebRtcServerPort { get; set; } = 8090;

    [DataMember(Order = 7)]
    public WebRtcBindMode WebRtcBindMode { get; set; } = WebRtcBindMode.Lan;

    [DataMember(Order = 8)]
    public string WebRtcSpecificIp { get; set; } = string.Empty;

    [DataMember(Order = 9)]
    public List<DisplayTargetProfile> DisplayTargets { get; set; } = new List<DisplayTargetProfile>();
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
    public string DeviceUniqueId { get; set; } = string.Empty;

    [DataMember(Order = 7)]
    public string DiscoverySource { get; set; } = string.Empty;

    [DataMember(Order = 8)]
    public DisplayTransportKind TransportKind { get; set; }

    [DataMember(Order = 9)]
    public bool IsOnline { get; set; }

    [DataMember(Order = 10)]
    public bool WasPreviouslyKnown { get; set; }

    [DataMember(Order = 11)]
    public bool IsStaticTarget { get; set; }

    [DataMember(Order = 12)]
    public int NativeWidth { get; set; } = 1920;

    [DataMember(Order = 13)]
    public int NativeHeight { get; set; } = 1080;
}
