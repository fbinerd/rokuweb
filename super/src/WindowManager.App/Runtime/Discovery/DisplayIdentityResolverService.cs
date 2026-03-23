using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowManager.Core.Models;

namespace WindowManager.App.Runtime.Discovery;

public sealed class DisplayIdentityResolverService
{
    private readonly KnownDisplayStore _knownDisplayStore;
    private readonly ManualDisplayProbeService _manualDisplayProbeService;

    public DisplayIdentityResolverService(KnownDisplayStore knownDisplayStore, ManualDisplayProbeService manualDisplayProbeService)
    {
        _knownDisplayStore = knownDisplayStore;
        _manualDisplayProbeService = manualDisplayProbeService;
    }

    public async Task<DisplayTarget> ResolveCurrentTargetAsync(
        DisplayTarget target,
        IEnumerable<DisplayTarget>? liveTargets,
        CancellationToken cancellationToken)
    {
        var resolved = FindMatchingLiveTarget(target, liveTargets);
        if (resolved is not null)
        {
            return MergeTargets(target, resolved, "TV reconciliada por identidade");
        }

        var knownDisplays = await _knownDisplayStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var knownMatch = knownDisplays.FirstOrDefault(x => IsSameDevice(x, target));
        if (knownMatch is not null && !string.IsNullOrWhiteSpace(knownMatch.NetworkAddress))
        {
            var probed = await _manualDisplayProbeService.ProbeAsync(knownMatch.NetworkAddress, cancellationToken).ConfigureAwait(false);
            if (probed is not null && IsSameDevice(target, probed))
            {
                return MergeTargets(target, probed, "TV reconciliada via endereco conhecido");
            }
        }

        if (!string.IsNullOrWhiteSpace(target.NetworkAddress))
        {
            var probed = await _manualDisplayProbeService.ProbeAsync(target.NetworkAddress, cancellationToken).ConfigureAwait(false);
            if (probed is not null && IsSameDevice(target, probed))
            {
                return MergeTargets(target, probed, "TV confirmada no endereco atual");
            }
        }

        return CloneTarget(target);
    }

    public async Task ReconcileKnownDisplaysAsync(IEnumerable<DisplayTarget> liveTargets, CancellationToken cancellationToken)
    {
        var liveList = liveTargets?.ToList() ?? new List<DisplayTarget>();
        if (liveList.Count == 0)
        {
            return;
        }

        var knownDisplays = (await _knownDisplayStore.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var changed = false;

        foreach (var known in knownDisplays)
        {
            var liveMatch = liveList.FirstOrDefault(x => IsSameDevice(known, x));
            if (liveMatch is null)
            {
                continue;
            }

            var mergedMacs = MacAddressFormatter.NormalizeMany(GetKnownMacs(known).Concat(GetTargetMacs(liveMatch)));

            var preferredMac = MacAddressFormatter.Normalize(!string.IsNullOrWhiteSpace(liveMatch.MacAddress)
                ? liveMatch.MacAddress
                : known.MacAddress);

            var alternateMacs = mergedMacs
                .Where(x => !string.Equals(x, preferredMac, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!string.Equals(known.NetworkAddress, liveMatch.NetworkAddress, StringComparison.OrdinalIgnoreCase))
            {
                known.LastKnownNetworkAddress = string.IsNullOrWhiteSpace(known.NetworkAddress)
                    ? liveMatch.NetworkAddress
                    : known.NetworkAddress;
                known.NetworkAddress = liveMatch.NetworkAddress;
                changed = true;
            }

            if (!string.Equals(known.Name, liveMatch.Name, StringComparison.Ordinal))
            {
                known.Name = liveMatch.Name;
                changed = true;
            }

            if (!string.Equals(known.DeviceUniqueId, liveMatch.DeviceUniqueId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(liveMatch.DeviceUniqueId))
            {
                known.DeviceUniqueId = liveMatch.DeviceUniqueId;
                changed = true;
            }

            if (!string.Equals(known.MacAddress, preferredMac, StringComparison.OrdinalIgnoreCase))
            {
                known.MacAddress = preferredMac;
                changed = true;
            }

            if (!known.AlternateMacAddresses.SequenceEqual(alternateMacs, StringComparer.OrdinalIgnoreCase))
            {
                known.AlternateMacAddresses = alternateMacs;
                changed = true;
            }

            if (!string.Equals(known.DiscoverySource, liveMatch.DiscoverySource, StringComparison.Ordinal))
            {
                known.DiscoverySource = liveMatch.DiscoverySource;
                changed = true;
            }

            if (known.NativeWidth != liveMatch.NativeWidth || known.NativeHeight != liveMatch.NativeHeight)
            {
                known.NativeWidth = liveMatch.NativeWidth;
                known.NativeHeight = liveMatch.NativeHeight;
                changed = true;
            }
        }

        if (changed)
        {
            await _knownDisplayStore.SaveAsync(knownDisplays, cancellationToken).ConfigureAwait(false);
        }
    }

    private static DisplayTarget? FindMatchingLiveTarget(DisplayTarget target, IEnumerable<DisplayTarget>? liveTargets)
    {
        if (liveTargets is null)
        {
            return null;
        }

        return liveTargets.FirstOrDefault(x => IsSameDevice(target, x));
    }

    private static DisplayTarget MergeTargets(DisplayTarget persisted, DisplayTarget live, string fallbackDiscoverySource)
    {
        var mergedMacs = MacAddressFormatter.NormalizeMany(GetTargetMacs(persisted).Concat(GetTargetMacs(live)));

        var primaryMac = MacAddressFormatter.Normalize(!string.IsNullOrWhiteSpace(live.MacAddress) ? live.MacAddress : persisted.MacAddress);

        return new DisplayTarget
        {
            Id = live.Id != Guid.Empty ? live.Id : persisted.Id,
            Name = string.IsNullOrWhiteSpace(live.Name) ? persisted.Name : live.Name,
            NetworkAddress = string.IsNullOrWhiteSpace(live.NetworkAddress) ? persisted.NetworkAddress : live.NetworkAddress,
            LastKnownNetworkAddress = string.IsNullOrWhiteSpace(persisted.NetworkAddress) ? persisted.LastKnownNetworkAddress : persisted.NetworkAddress,
            MacAddress = primaryMac,
            AlternateMacAddresses = mergedMacs
                .Where(x => !string.Equals(x, primaryMac, StringComparison.OrdinalIgnoreCase))
                .ToList(),
            DeviceUniqueId = string.IsNullOrWhiteSpace(live.DeviceUniqueId) ? persisted.DeviceUniqueId : live.DeviceUniqueId,
            DiscoverySource = string.IsNullOrWhiteSpace(live.DiscoverySource) ? fallbackDiscoverySource : live.DiscoverySource,
            TransportKind = live.TransportKind,
            IsOnline = live.IsOnline,
            WasPreviouslyKnown = true,
            IsStaticTarget = persisted.IsStaticTarget || live.IsStaticTarget,
            NativeWidth = live.NativeWidth > 0 ? live.NativeWidth : persisted.NativeWidth,
            NativeHeight = live.NativeHeight > 0 ? live.NativeHeight : persisted.NativeHeight
        };
    }

    private static DisplayTarget CloneTarget(DisplayTarget target)
    {
        return new DisplayTarget
        {
            Id = target.Id,
            Name = target.Name,
            NetworkAddress = target.NetworkAddress,
            LastKnownNetworkAddress = target.LastKnownNetworkAddress,
            MacAddress = MacAddressFormatter.Normalize(target.MacAddress),
            AlternateMacAddresses = MacAddressFormatter.NormalizeMany(target.AlternateMacAddresses),
            DeviceUniqueId = target.DeviceUniqueId,
            DiscoverySource = target.DiscoverySource,
            TransportKind = target.TransportKind,
            IsOnline = target.IsOnline,
            WasPreviouslyKnown = target.WasPreviouslyKnown,
            IsStaticTarget = target.IsStaticTarget,
            NativeWidth = target.NativeWidth,
            NativeHeight = target.NativeHeight
        };
    }

    private static bool IsSameDevice(KnownDisplayRecord known, DisplayTarget target)
    {
        if (!string.IsNullOrWhiteSpace(known.DeviceUniqueId) &&
            string.Equals(known.DeviceUniqueId, target.DeviceUniqueId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var knownMacs = GetKnownMacs(known);
        var targetMacs = GetTargetMacs(target);
        return knownMacs.Intersect(targetMacs, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static bool IsSameDevice(DisplayTarget left, DisplayTarget right)
    {
        if (!string.IsNullOrWhiteSpace(left.DeviceUniqueId) &&
            !string.IsNullOrWhiteSpace(right.DeviceUniqueId) &&
            string.Equals(left.DeviceUniqueId, right.DeviceUniqueId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftMacs = GetTargetMacs(left);
        var rightMacs = GetTargetMacs(right);
        return leftMacs.Intersect(rightMacs, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static IEnumerable<string> GetKnownMacs(KnownDisplayRecord record)
    {
        return new[] { record.MacAddress }
            .Concat(record.AlternateMacAddresses ?? Enumerable.Empty<string>())
            .Select(MacAddressFormatter.Normalize)
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }

    private static IEnumerable<string> GetTargetMacs(DisplayTarget target)
    {
        return new[] { target.MacAddress }
            .Concat(target.AlternateMacAddresses ?? Enumerable.Empty<string>())
            .Select(MacAddressFormatter.Normalize)
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }
}
