using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using WindowManager.Core.Models;

namespace WindowManager.App.Runtime.Discovery;

public sealed class ManualDisplayProbeService
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int physicalAddrLen);

    public async Task<DisplayTarget?> ProbeAsync(string address, CancellationToken cancellationToken)
    {
        var normalizedAddress = address?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedAddress))
        {
            return null;
        }

        return await TryProbeRokuAsync(normalizedAddress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DisplayTarget?> TryProbeRokuAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            var request = WebRequest.CreateHttp("http://" + address + ":8060/query/device-info");
            request.Method = "GET";
            request.Timeout = 1200;
            request.ReadWriteTimeout = 1200;

            using (cancellationToken.Register(() => request.Abort()))
            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var stream = response.GetResponseStream())
            {
                if (stream is null)
                {
                    return null;
                }

                var document = new XmlDocument();
                document.Load(stream);

                var root = document.DocumentElement;
                if (root is null || !string.Equals(root.Name, "device-info", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var vendorName = GetChildNodeValue(root, "vendor-name");
                var modelName = GetChildNodeValue(root, "model-name");
                var modelNumber = GetChildNodeValue(root, "model-number");
                var friendlyDeviceName = GetChildNodeValue(root, "friendly-device-name");
                var serialNumber = GetChildNodeValue(root, "serial-number");
                var deviceId = GetChildNodeValue(root, "device-id");
                var wifiMac = GetChildNodeValue(root, "wifi-mac");
                var ethernetMac = GetChildNodeValue(root, "ethernet-mac");
                var uiResolution = GetChildNodeValue(root, "ui-resolution");

                var uniqueId = FirstNonEmpty(deviceId, serialNumber, address);
                var friendlyName = FirstNonEmpty(
                    friendlyDeviceName,
                    modelName,
                    modelNumber,
                    "Roku TV");

                var ipAddress = ResolveIpv4Address(address);
                return new DisplayTarget
                {
                    Id = CreateDeterministicGuid("roku:" + uniqueId),
                    Name = friendlyName,
                    NetworkAddress = address,
                    LastKnownNetworkAddress = address,
                    MacAddress = MacAddressFormatter.Normalize(FirstNonEmpty(wifiMac, ethernetMac, ipAddress is null ? string.Empty : TryResolveMacAddress(ipAddress))),
                    DeviceUniqueId = uniqueId,
                    DiscoverySource = string.Format("TV detectada manualmente ({0} {1})", vendorName, modelName).Trim(),
                    TransportKind = DisplayTransportKind.LanStreaming,
                    IsOnline = true,
                    WasPreviouslyKnown = false,
                    IsStaticTarget = true,
                    NativeWidth = ResolveResolutionWidth(uiResolution),
                    NativeHeight = ResolveResolutionHeight(uiResolution)
                };
            }
        }
        catch
        {
            return null;
        }
    }

    private static IPAddress? ResolveIpv4Address(string address)
    {
        if (IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            return parsed;
        }

        try
        {
            return Dns.GetHostAddresses(address).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            return null;
        }
    }

    private static string GetChildNodeValue(XmlNode parent, string nodeName)
    {
        foreach (XmlNode child in parent.ChildNodes)
        {
            if (string.Equals(child.LocalName, nodeName, StringComparison.OrdinalIgnoreCase))
            {
                return child.InnerText?.Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    }

    private static string TryResolveMacAddress(IPAddress address)
    {
        try
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return string.Empty;
            }

            var macBytes = new byte[6];
            var length = macBytes.Length;
            var ipBytes = address.GetAddressBytes();
            var destination = BitConverter.ToInt32(ipBytes, 0);
            var result = SendARP(destination, 0, macBytes, ref length);
            if (result != 0 || length <= 0)
            {
                return string.Empty;
            }

            return string.Join("-", macBytes.Take(length).Select(x => x.ToString("X2")));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Guid CreateDeterministicGuid(string value)
    {
        using (var md5 = MD5.Create())
        {
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            return new Guid(hash);
        }
    }

    private static int ResolveResolutionWidth(string uiResolution)
    {
        if (ContainsIgnoreCase(uiResolution, "4k") || ContainsIgnoreCase(uiResolution, "2160"))
        {
            return 3840;
        }

        if (ContainsIgnoreCase(uiResolution, "1080") || ContainsIgnoreCase(uiResolution, "fhd"))
        {
            return 1920;
        }

        return 1280;
    }

    private static int ResolveResolutionHeight(string uiResolution)
    {
        if (ContainsIgnoreCase(uiResolution, "4k") || ContainsIgnoreCase(uiResolution, "2160"))
        {
            return 2160;
        }

        if (ContainsIgnoreCase(uiResolution, "1080") || ContainsIgnoreCase(uiResolution, "fhd"))
        {
            return 1080;
        }

        return 720;
    }

    private static bool ContainsIgnoreCase(string? value, string search)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value!.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
