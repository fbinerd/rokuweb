using System;
using System.Runtime.Serialization;
using WindowManager.Core.Models;

using System.Collections.Generic;

namespace WindowManager.App.Runtime.Discovery;

[DataContract]
public sealed class KnownDisplayRecord
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
    public int NativeWidth { get; set; }

    [DataMember(Order = 11)]
    public int NativeHeight { get; set; }

    [DataMember(Order = 12)]
    public bool IsStaticTarget { get; set; }
}
