using FxAuto.Core.Devices;

namespace FxAuto.Devices;

/// <summary>Raw property values of one render endpoint, as read from its IPropertyStore (research 02 §1).</summary>
/// <param name="FormFactor">PKEY_AudioEndpoint_FormFactor (EndpointFormFactor), null if absent.</param>
/// <param name="ContainerId">PKEY_Device_ContainerId, null if absent.</param>
/// <param name="Bus">PKEY_Device_EnumeratorName ("HDAUDIO", "USB", "BTHENUM", "BTHHFENUM", "ROOT"...).</param>
internal sealed record RawEndpoint(string EndpointId, string? FriendlyName, string? DeviceDesc, string? Adapter,
    uint? FormFactor, Guid? ContainerId, string? Bus, int State);

/// <summary>Pure mapping from raw MMDevice properties to the Core contract (no COM; unit-tested).</summary>
internal static class EndpointMapping
{
    public const int StateActive = 1;
    public const int StateDisabled = 2;
    public const int StateNotPresent = 4;
    public const int StateUnplugged = 8;

    /// <summary>ContainerId of devices that have no physical container (FxSound's virtual device, software
    /// endpoints): shared by unrelated devices, so it must not group anything.</summary>
    public static readonly Guid NoContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    public static EndpointInfo ToInfo(RawEndpoint raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var friendly = Clean(raw.FriendlyName) ?? Clean(raw.DeviceDesc) ?? raw.EndpointId;
        var info = new EndpointInfo(raw.EndpointId, friendly, Clean(raw.DeviceDesc), Clean(raw.Adapter),
            MapFormFactor(raw.FormFactor), NormalizeContainerId(raw.ContainerId), Clean(raw.Bus), raw.State, false);
        return info with { IsFxSoundVirtual = DeviceMatcher.IsFxSoundVirtual(info) };
    }

    /// <summary>EndpointFormFactor → FormFactor. Windows values 1–9 have the same order as the contract enum;
    /// 0 (RemoteNetworkDevice), 10 (UnknownFormFactor) and anything else are Unknown.</summary>
    public static FormFactor MapFormFactor(uint? raw) => raw switch
    {
        1 => FormFactor.Speakers,
        2 => FormFactor.LineLevel,
        3 => FormFactor.Headphones,
        4 => FormFactor.Microphone,
        5 => FormFactor.Headset,
        6 => FormFactor.Handset,
        7 => FormFactor.UnknownDigitalPassthrough,
        8 => FormFactor.Spdif,
        9 => FormFactor.Hdmi,
        _ => FormFactor.Unknown,
    };

    /// <summary>"{xxxxxxxx-...}" in lower case, or null for missing / empty / "no container" ids.</summary>
    public static string? NormalizeContainerId(Guid? id)
        => id is not Guid g || g == Guid.Empty || g == NoContainer ? null : g.ToString("B");

    /// <summary>Stable order: active first, then by friendly name, then by id.</summary>
    public static List<EndpointInfo> Sort(IEnumerable<EndpointInfo> endpoints)
        => endpoints.OrderBy(e => e.State == StateActive ? 0 : 1)
            .ThenBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.EndpointId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Same endpoints (all fields) and same defaults; the timestamp is ignored.</summary>
    public static bool SameContent(EndpointSnapshot? a, EndpointSnapshot? b)
    {
        if (a is null || b is null) return ReferenceEquals(a, b);
        return string.Equals(a.DefaultMultimediaId, b.DefaultMultimediaId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.DefaultConsoleId, b.DefaultConsoleId, StringComparison.OrdinalIgnoreCase)
            && a.Endpoints.SequenceEqual(b.Endpoints);
    }

    // Values are kept exactly (FriendlyName must equal FxSound's output_devices[] entries byte for byte).
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
