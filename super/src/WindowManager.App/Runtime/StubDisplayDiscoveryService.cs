using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using WindowManager.App.Runtime.Discovery;
using WindowManager.Core.Abstractions;
using WindowManager.Core.Models;

namespace WindowManager.App.Runtime;

public sealed class StubDisplayDiscoveryService : IDisplayDiscoveryService
{
    private static readonly Guid LegacyTvSalaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LegacyDongleMiracastId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly IPEndPoint SsdpEndpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly string[] SearchTargets =
    {
        "urn:schemas-upnp-org:device:MediaRenderer:1",
        "urn:dial-multiscreen-org:service:dial:1",
        "ssdp:all"
    };

    private readonly KnownDisplayStore _knownDisplayStore;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int physicalAddrLen);

    public StubDisplayDiscoveryService(KnownDisplayStore knownDisplayStore)
    {
        _knownDisplayStore = knownDisplayStore;
    }

    public async Task<IReadOnlyList<DisplayTarget>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var knownDisplays = (await _knownDisplayStore.LoadAsync(cancellationToken))
            .Where(x => !IsLegacyFakeRecord(x))
            .ToList();
        var liveCandidates = await DiscoverLiveCandidatesAsync(cancellationToken);

        var prioritizedCandidates = liveCandidates
            .OrderByDescending(candidate => knownDisplays.Any(known =>
                string.Equals(known.NetworkAddress, candidate.NetworkAddress, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(candidate => knownDisplays.Any(known => known.IsStaticTarget && IsSameDevice(known, candidate)))
            .ThenBy(candidate => candidate.Name)
            .ToList();

        foreach (var candidate in prioritizedCandidates)
        {
            var knownMatch = knownDisplays.FirstOrDefault(known => IsSameDevice(known, candidate));
            if (knownMatch is null)
            {
                candidate.DiscoverySource = string.IsNullOrWhiteSpace(candidate.DiscoverySource)
                    ? "TV encontrada na rede local"
                    : candidate.DiscoverySource;
                continue;
            }

            candidate.Id = knownMatch.Id != Guid.Empty ? knownMatch.Id : candidate.Id;
            candidate.WasPreviouslyKnown = true;
            candidate.IsStaticTarget = knownMatch.IsStaticTarget;
            candidate.LastKnownNetworkAddress = string.IsNullOrWhiteSpace(knownMatch.NetworkAddress)
                ? knownMatch.LastKnownNetworkAddress
                : knownMatch.NetworkAddress;
            candidate.DiscoverySource = string.Equals(candidate.NetworkAddress, knownMatch.NetworkAddress, StringComparison.OrdinalIgnoreCase)
                ? "TV real no IP conhecido"
                : "TV real com IP atualizado";
        }

        var knownOffline = knownDisplays
            .Where(known => prioritizedCandidates.All(candidate => !IsSameDevice(known, candidate)))
            .Select(known => new DisplayTarget
            {
                Id = known.Id,
                Name = known.Name,
                NetworkAddress = known.NetworkAddress,
                LastKnownNetworkAddress = known.LastKnownNetworkAddress,
                MacAddress = known.MacAddress,
                DeviceUniqueId = known.DeviceUniqueId,
                DiscoverySource = known.IsStaticTarget ? "TV estatica offline" : "Historico offline",
                TransportKind = known.TransportKind,
                IsOnline = false,
                WasPreviouslyKnown = true,
                IsStaticTarget = known.IsStaticTarget,
                NativeWidth = known.NativeWidth,
                NativeHeight = known.NativeHeight
            });

        var results = prioritizedCandidates
            .Concat(knownOffline)
            .Where(x => !IsLegacyFakeTarget(x))
            .GroupBy(x => x.NetworkAddress, StringComparer.OrdinalIgnoreCase)
            .Select(ChooseBestTarget)
            .ToList();
        await _knownDisplayStore.SaveAsync(results.Select(ToKnownRecord), cancellationToken);
        return results;
    }

    private static async Task<List<DisplayTarget>> DiscoverLiveCandidatesAsync(CancellationToken cancellationToken)
    {
        var responses = await QuerySsdpAsync(cancellationToken);
        var results = new List<DisplayTarget>();

        foreach (var response in responses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = await TryCreateTargetAsync(response, cancellationToken);
            if (target is not null)
            {
                results.Add(target);
            }
        }

        return results
            .GroupBy(x => x.NetworkAddress, StringComparer.OrdinalIgnoreCase)
            .Select(ChooseBestTarget)
            .ToList();
    }

    private static async Task<List<SsdpResponse>> QuerySsdpAsync(CancellationToken cancellationToken)
    {
        var results = new List<SsdpResponse>();

        using (var client = new UdpClient(AddressFamily.InterNetwork))
        {
            client.EnableBroadcast = true;
            client.MulticastLoopback = false;
            client.Client.ReceiveTimeout = 1000;
            client.Client.SendTimeout = 1000;

            foreach (var searchTarget in SearchTargets)
            {
                var requestBytes = Encoding.ASCII.GetBytes(BuildSearchRequest(searchTarget));
                await client.SendAsync(requestBytes, requestBytes.Length, SsdpEndpoint);
            }

            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var receiveTask = client.ReceiveAsync();
                    var completedTask = await Task.WhenAny(receiveTask, Task.Delay(350, cancellationToken));
                    if (completedTask != receiveTask)
                    {
                        continue;
                    }

                    var packet = receiveTask.Result;
                    var parsed = ParseSsdpResponse(Encoding.UTF8.GetString(packet.Buffer), packet.RemoteEndPoint.Address);
                    if (parsed is not null)
                    {
                        results.Add(parsed);
                    }
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        return results
            .GroupBy(x => string.Format("{0}|{1}|{2}", x.Location, x.Usn, x.RemoteAddress))
            .Select(x => x.First())
            .ToList();
    }

    private static async Task<DisplayTarget?> TryCreateTargetAsync(SsdpResponse response, CancellationToken cancellationToken)
    {
        var description = await TryReadDescriptionAsync(response.Location, cancellationToken);
        var friendlyName = FirstNonEmpty(
            description?.FriendlyName,
            BuildNameFromResponse(response),
            response.RemoteAddress.ToString(),
            "Display na rede");

        if (!LooksLikeTv(response, description, friendlyName))
        {
            return null;
        }

        var uniqueId = FirstNonEmpty(
            description?.DeviceUniqueId,
            response.Usn,
            response.Location,
            response.RemoteAddress.ToString());

        var isSamsung =
            ContainsIgnoreCase(description?.Manufacturer, "Samsung") ||
            ContainsIgnoreCase(description?.ModelName, "Samsung") ||
            ContainsIgnoreCase(friendlyName, "Samsung") ||
            ContainsIgnoreCase(response.Server, "Samsung");

        var transportKind = ResolveTransportKind(response, description, friendlyName, isSamsung);
        var discoverySource = BuildDiscoverySource(isSamsung, transportKind);

        return new DisplayTarget
        {
            Id = CreateDeterministicGuid(uniqueId),
            Name = friendlyName,
            NetworkAddress = response.RemoteAddress.ToString(),
            LastKnownNetworkAddress = response.RemoteAddress.ToString(),
            MacAddress = TryResolveMacAddress(response.RemoteAddress),
            DeviceUniqueId = uniqueId,
            DiscoverySource = discoverySource,
            TransportKind = transportKind,
            IsOnline = true,
            WasPreviouslyKnown = false,
            IsStaticTarget = false,
            NativeWidth = 1920,
            NativeHeight = 1080
        };
    }

    private static bool LooksLikeTv(SsdpResponse response, DeviceDescription? description, string friendlyName)
    {
        return ContainsIgnoreCase(response.SearchTarget, "MediaRenderer") ||
               ContainsIgnoreCase(response.SearchTarget, "dial") ||
               ContainsIgnoreCase(response.Usn, "MediaRenderer") ||
               ContainsIgnoreCase(response.Server, "Samsung") ||
               ContainsIgnoreCase(response.Server, "TV") ||
               ContainsIgnoreCase(description?.DeviceType, "MediaRenderer") ||
               ContainsIgnoreCase(description?.Manufacturer, "Samsung") ||
               ContainsIgnoreCase(description?.ModelName, "Samsung") ||
               ContainsIgnoreCase(friendlyName, "TV") ||
               ContainsIgnoreCase(friendlyName, "Samsung");
    }

    private static DisplayTransportKind ResolveTransportKind(
        SsdpResponse response,
        DeviceDescription? description,
        string friendlyName,
        bool isSamsung)
    {
        if (isSamsung ||
            ContainsIgnoreCase(description?.Manufacturer, "Miracast") ||
            ContainsIgnoreCase(description?.ModelName, "Miracast") ||
            ContainsIgnoreCase(friendlyName, "Miracast") ||
            ContainsIgnoreCase(response.Server, "Miracast"))
        {
            return DisplayTransportKind.Miracast;
        }

        return DisplayTransportKind.LanStreaming;
    }

    private static string BuildDiscoverySource(bool isSamsung, DisplayTransportKind transportKind)
    {
        if (isSamsung && transportKind == DisplayTransportKind.Miracast)
        {
            return "Samsung detectada para transmissao";
        }

        if (transportKind == DisplayTransportKind.Miracast)
        {
            return "Destino Miracast detectado na rede local";
        }

        if (isSamsung)
        {
            return "Samsung detectada na rede local";
        }

        return "TV detectada na rede local";
    }

    private static bool IsSameDevice(KnownDisplayRecord known, DisplayTarget target)
    {
        if (!string.IsNullOrWhiteSpace(known.DeviceUniqueId) &&
            string.Equals(known.DeviceUniqueId, target.DeviceUniqueId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(known.MacAddress) &&
            !string.IsNullOrWhiteSpace(target.MacAddress) &&
            string.Equals(known.MacAddress, target.MacAddress, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(known.NetworkAddress) &&
               string.Equals(known.NetworkAddress, target.NetworkAddress, StringComparison.OrdinalIgnoreCase);
    }

    private static KnownDisplayRecord ToKnownRecord(DisplayTarget target)
    {
        return new KnownDisplayRecord
        {
            Id = target.Id,
            Name = target.Name,
            NetworkAddress = target.NetworkAddress,
            LastKnownNetworkAddress = string.IsNullOrWhiteSpace(target.LastKnownNetworkAddress)
                ? target.NetworkAddress
                : target.LastKnownNetworkAddress,
            MacAddress = target.MacAddress,
            DeviceUniqueId = target.DeviceUniqueId,
            DiscoverySource = target.DiscoverySource,
            TransportKind = target.TransportKind,
            NativeWidth = target.NativeWidth,
            NativeHeight = target.NativeHeight,
            IsStaticTarget = target.IsStaticTarget
        };
    }

    private static DisplayTarget ChooseBestTarget(IGrouping<string, DisplayTarget> group)
    {
        return group
            .OrderByDescending(x => HasReadableDisplayName(x.Name))
            .ThenByDescending(x => x.IsStaticTarget)
            .ThenByDescending(x => x.WasPreviouslyKnown)
            .ThenBy(x => x.Name)
            .First();
    }

    private static bool HasReadableDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return !name!.StartsWith("urn:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyFakeRecord(KnownDisplayRecord record)
    {
        return record.Id == LegacyTvSalaId ||
               record.Id == LegacyDongleMiracastId ||
               string.Equals(record.DeviceUniqueId, "TV-SALA-AA-BB-CC-11-22-33", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(record.DeviceUniqueId, "DONGLE-MIRACAST-DD-EE-FF-44-55-66", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyFakeTarget(DisplayTarget target)
    {
        return target.Id == LegacyTvSalaId ||
               target.Id == LegacyDongleMiracastId ||
               string.Equals(target.DeviceUniqueId, "TV-SALA-AA-BB-CC-11-22-33", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target.DeviceUniqueId, "DONGLE-MIRACAST-DD-EE-FF-44-55-66", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSearchRequest(string searchTarget)
    {
        return string.Join("\r\n", new[]
        {
            "M-SEARCH * HTTP/1.1",
            "HOST: 239.255.255.250:1900",
            "MAN: \"ssdp:discover\"",
            "MX: 2",
            string.Format("ST: {0}", searchTarget),
            string.Empty,
            string.Empty
        });
    }

    private static SsdpResponse? ParseSsdpResponse(string rawResponse, IPAddress remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return null;
        }

        var lines = rawResponse.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        if (lines.Length == 0 || lines[0].IndexOf("200", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            headers[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
        }

        if (!headers.TryGetValue("LOCATION", out var location) || string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        return new SsdpResponse
        {
            Location = location,
            Usn = headers.ContainsKey("USN") ? headers["USN"] : string.Empty,
            SearchTarget = headers.ContainsKey("ST") ? headers["ST"] : string.Empty,
            Server = headers.ContainsKey("SERVER") ? headers["SERVER"] : string.Empty,
            RemoteAddress = remoteAddress
        };
    }

    private static async Task<DeviceDescription?> TryReadDescriptionAsync(string location, CancellationToken cancellationToken)
    {
        try
        {
            var request = WebRequest.CreateHttp(location);
            request.Method = "GET";
            request.Timeout = 2500;
            request.ReadWriteTimeout = 2500;

            using (cancellationToken.Register(() => request.Abort()))
            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var stream = response.GetResponseStream())
            {
                if (stream is null)
                {
                    return null;
                }

                var document = new XmlDocument();
                document.Load(stream);

                var deviceNode = document.GetElementsByTagName("device").OfType<XmlNode>().FirstOrDefault();
                if (deviceNode is null)
                {
                    return null;
                }

                return new DeviceDescription
                {
                    FriendlyName = GetChildNodeValue(deviceNode, "friendlyName"),
                    Manufacturer = GetChildNodeValue(deviceNode, "manufacturer"),
                    ModelName = GetChildNodeValue(deviceNode, "modelName"),
                    DeviceType = GetChildNodeValue(deviceNode, "deviceType"),
                    DeviceUniqueId = GetChildNodeValue(deviceNode, "UDN")
                };
            }
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

    private static string BuildNameFromResponse(SsdpResponse response)
    {
        if (ContainsIgnoreCase(response.Server, "Samsung"))
        {
            return "Samsung TV";
        }

        if (ContainsIgnoreCase(response.Server, "TV"))
        {
            return response.Server;
        }

        return response.SearchTarget;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    }

    private static bool ContainsIgnoreCase(string? value, string search)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value!.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
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

    private sealed class SsdpResponse
    {
        public string Location { get; set; } = string.Empty;

        public string Usn { get; set; } = string.Empty;

        public string SearchTarget { get; set; } = string.Empty;

        public string Server { get; set; } = string.Empty;

        public IPAddress RemoteAddress { get; set; } = IPAddress.None;
    }

    private sealed class DeviceDescription
    {
        public string FriendlyName { get; set; } = string.Empty;

        public string Manufacturer { get; set; } = string.Empty;

        public string ModelName { get; set; } = string.Empty;

        public string DeviceType { get; set; } = string.Empty;

        public string DeviceUniqueId { get; set; } = string.Empty;
    }
}
