using System;

using System.Collections.Generic;
using System.Linq;

namespace WindowManager.Core.Models;

public sealed class DisplayTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string NetworkAddress { get; set; } = string.Empty;

    public string LastKnownNetworkAddress { get; set; } = string.Empty;

    public string MacAddress { get; set; } = string.Empty;

    public List<string> AlternateMacAddresses { get; set; } = new List<string>();

    public string DeviceUniqueId { get; set; } = string.Empty;

    public string DiscoverySource { get; set; } = string.Empty;

    public DisplayTransportKind TransportKind { get; set; }

    public bool IsOnline { get; set; }

    public bool WasPreviouslyKnown { get; set; }

    public bool IsStaticTarget { get; set; }

    public int NativeWidth { get; set; } = 1920;

    public int NativeHeight { get; set; } = 1080;

    public string AddressSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(NetworkAddress) && string.IsNullOrWhiteSpace(LastKnownNetworkAddress))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(LastKnownNetworkAddress) ||
                string.Equals(NetworkAddress, LastKnownNetworkAddress, StringComparison.OrdinalIgnoreCase))
            {
                return NetworkAddress;
            }

            return string.Format("{0} (ultimo: {1})", NetworkAddress, LastKnownNetworkAddress);
        }
    }

    public string MetadataSummary
    {
        get
        {
            var source = DiscoverySource;
            var transport = GetTransportLabel();
            var staticLabel = IsStaticTarget ? "Estatica" : string.Empty;
            var macs = new[] { MacAddress }
                .Concat(AlternateMacAddresses ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var macLabel = macs.Length == 0 ? string.Empty : string.Format("MAC {0}", string.Join(", ", macs));

            if (!string.IsNullOrWhiteSpace(staticLabel) && !string.IsNullOrWhiteSpace(macLabel))
            {
                return string.Format("{0} • {1} • {2} • {3}", transport, source, macLabel, staticLabel);
            }

            if (!string.IsNullOrWhiteSpace(staticLabel))
            {
                return string.Format("{0} • {1} • {2}", transport, source, staticLabel);
            }

            if (!string.IsNullOrWhiteSpace(macLabel))
            {
                return string.Format("{0} • {1} • {2}", transport, source, macLabel);
            }

            return string.Format("{0} • {1}", transport, source);
        }
    }

    private string GetTransportLabel()
    {
        return TransportKind switch
        {
            DisplayTransportKind.Miracast => "Miracast",
            DisplayTransportKind.LanStreaming => "LAN",
            _ => TransportKind.ToString()
        };
    }
}
