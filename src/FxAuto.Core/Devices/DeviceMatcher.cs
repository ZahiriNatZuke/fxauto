using FxAuto.Core.Config;

namespace FxAuto.Core.Devices;

/// <summary>Endpoint → configured device (research 02 §2.2): known endpoint id (pinned or learned) → adapters +
/// form factor (+ endpointDesc) with learning → single profile with that adapter (weak, warning) → unknown.
/// Bluetooth hands-free (HFP) endpoints are grouped with their stereo (A2DP) sibling by ContainerId and normalised
/// adapter, and flagged as call mode.</summary>
public static class DeviceMatcher
{
    public const string HowFxSoundVirtual = "fxsound virtual endpoint";
    public const string HowEndpointId = "known endpoint id";
    public const string HowAdapterFormFactor = "adapter + form factor";
    public const string HowContainer = "container id + form factor";
    public const string HowAdapterOnly = "adapter only (weak)";
    public const string HowUnknown = "unknown device";

    public static DeviceMatch Match(EndpointInfo ep, IReadOnlyList<DeviceConfig> devices,
        LearnedState learned, IReadOnlyList<EndpointInfo> all)
    {
        ArgumentNullException.ThrowIfNull(ep);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(learned);
        ArgumentNullException.ThrowIfNull(all);

        var adapter = ep.Adapter ?? DeviceKeys.AdapterFromFriendlyName(ep.FriendlyName) ?? ep.FriendlyName;
        var suggested = DeviceKeys.SuggestTag(adapter);
        if (ep.IsFxSoundVirtual || IsFxSoundVirtual(ep))
            return new DeviceMatch(null, HowFxSoundVirtual, null, suggested);

        var hfp = IsHandsFree(ep);
        var m = MatchCore(ep, adapter, hfp, devices, learned);
        if (m is null && hfp)
        {
            // HFP endpoint not recognised by itself: use its A2DP sibling (same container, same normalised adapter)
            var sibling = all.FirstOrDefault(o => !ReferenceEquals(o, ep) && !IsHandsFree(o) && !o.IsFxSoundVirtual
                && o.ContainerId is not null && ep.ContainerId is not null
                && string.Equals(o.ContainerId, ep.ContainerId, StringComparison.OrdinalIgnoreCase)
                && DeviceKeys.SameAdapter(o.Adapter ?? DeviceKeys.AdapterFromFriendlyName(o.FriendlyName), adapter));
            if (sibling is not null)
            {
                var sm = MatchCore(sibling, sibling.Adapter ?? adapter, false, devices, learned);
                // the learned id would be the sibling's: never learn the HFP id through its sibling
                if (sm is not null) m = sm with { LearnedEndpointId = null, How = sm.How + " (bluetooth sibling)" };
            }
        }
        if (m is null) return new DeviceMatch(null, HowUnknown, null, suggested) { CallMode = hfp };
        return m with { CallMode = hfp };
    }

    /// <summary>Bluetooth hands-free endpoint: bus BTHHFENUM or adapter ending in " Hands-Free".</summary>
    public static bool IsHandsFree(EndpointInfo ep)
    {
        ArgumentNullException.ThrowIfNull(ep);
        if (string.Equals(ep.Bus, "BTHHFENUM", StringComparison.OrdinalIgnoreCase)) return true;
        var a = ep.Adapter ?? DeviceKeys.AdapterFromFriendlyName(ep.FriendlyName);
        return a is not null && a.TrimEnd().EndsWith(" Hands-Free", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>FxSound virtual endpoint: bus ROOT and adapter "FxSound Audio Enhancer".</summary>
    public static bool IsFxSoundVirtual(EndpointInfo ep)
    {
        ArgumentNullException.ThrowIfNull(ep);
        return ep.IsFxSoundVirtual
            || (string.Equals(ep.Bus, "ROOT", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ep.Adapter?.Trim(), DeviceKeys.FxSoundAdapter, StringComparison.OrdinalIgnoreCase));
    }

    private static DeviceMatch? MatchCore(EndpointInfo ep, string adapter, bool hfp,
        IReadOnlyList<DeviceConfig> devices, LearnedState learned)
    {
        // 1. known endpoint id (user-pinned or learned)
        foreach (var d in devices)
        {
            if (d.Match.PinnedEndpointIds.Contains(ep.EndpointId, StringComparer.OrdinalIgnoreCase)
                || (learned.EndpointIdsByDevice.TryGetValue(d.Id, out var ids)
                    && ids.Contains(ep.EndpointId, StringComparer.OrdinalIgnoreCase)))
                return new DeviceMatch(d.Id, HowEndpointId, null, d.Tag);
        }

        // 2. adapter + form factor (+ endpointDesc when the profile has one); learns the new endpoint id.
        //    A hands-free endpoint ignores the form factor (Headset instead of Headphones).
        var byAdapter = devices.Where(d => d.Match.Adapters.Any(a => DeviceKeys.SameAdapter(a, adapter))).ToList();
        var strong = byAdapter
            .Where(d => hfp || d.Match.FormFactor is null || d.Match.FormFactor == ep.FormFactor)
            .Where(d => d.Match.EndpointDesc is null
                        || string.Equals(d.Match.EndpointDesc.Trim(), ep.EndpointDesc?.Trim(), StringComparison.OrdinalIgnoreCase))
            // most specific profile first (endpointDesc, then form factor); ties keep the list order
            .OrderByDescending(d => (d.Match.EndpointDesc is not null ? 2 : 0) + (d.Match.FormFactor is not null ? 1 : 0))
            .ToList();
        if (strong.Count > 0)
            return new DeviceMatch(strong[0].Id, HowAdapterFormFactor, ep.EndpointId, strong[0].Tag);

        // 2b. same physical container (stable for Bluetooth and monitors across drivers) + form factor
        if (ep.ContainerId is not null)
        {
            var byContainer = devices.Where(d => d.Match.ContainerId is not null
                    && string.Equals(d.Match.ContainerId, ep.ContainerId, StringComparison.OrdinalIgnoreCase)
                    && (hfp || d.Match.FormFactor is null || d.Match.FormFactor == ep.FormFactor)
                    && (d.Match.EndpointDesc is null
                        || string.Equals(d.Match.EndpointDesc.Trim(), ep.EndpointDesc?.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (byContainer.Count == 1)
                return new DeviceMatch(byContainer[0].Id, HowContainer, ep.EndpointId, byContainer[0].Tag);
        }

        // 3. a single profile with that adapter: match without learning, flagged weak (warning in the log)
        if (byAdapter.Count == 1)
            return new DeviceMatch(byAdapter[0].Id, HowAdapterOnly, null, byAdapter[0].Tag) { Weak = true };

        return null;
    }
}
