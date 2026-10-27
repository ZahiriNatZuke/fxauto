using FxAuto.Core.Config;
using FxAuto.Core.Devices;
using FxAuto.Core.FxSound;

namespace FxAuto.Tests;

/// <summary>Device keys, matcher and effective output, with the real endpoint table of research 02 §1.1.</summary>
internal static class DeviceTests
{
    private static string Id(string guid) => "{0.0.0.00000000}.{" + guid + "}";

    // ---- research 02 §1.1 (sample render endpoints (IDs are made up)) ----
    internal static readonly EndpointInfo G435 = new(Id("b1a2c3d4-1111-4a2b-9c3d-0e1f2a3b4c5d"),
        "Audífono de los auriculares con micrófono (G435 Wireless Gaming Headset)", "Audífono de los auriculares con micrófono",
        "G435 Wireless Gaming Headset", FormFactor.Headset, "{0c4e12ee-0000-0000-0000-000000000000}", "USB", 1, false);
    internal static readonly EndpointInfo Realtek = new(Id("d2b3c4e5-2222-4b3c-8d4e-1f2a3b4c5d6e"),
        "Altavoces (Realtek(R) Audio)", "Altavoces", "Realtek(R) Audio", FormFactor.Speakers,
        "{7a1b2c3d-4444-5d5e-8f60-3b4c5d6e7f80}", "HDAUDIO", 1, false);
    internal static readonly EndpointInfo FxVirtual = new(Id("f3c4d5e6-3333-4c4d-9e5f-2a3b4c5d6e7f"),
        "FxSound Speakers (FxSound Audio Enhancer)", "FxSound Speakers", "FxSound Audio Enhancer", FormFactor.Speakers,
        "{00000000-0000-0000-ffff-ffffffffffff}", "ROOT", 1, true);
    internal static readonly EndpointInfo RedmiA2dp = new(Id("0129dc3f-0000-0000-0000-000000000000"),
        "Auriculares (Redmi Buds 6 Pro)", "Auriculares", "Redmi Buds 6 Pro", FormFactor.Headphones,
        "{c3482619-0000-0000-0000-000000000000}", "BTHENUM", 1, false);
    internal static readonly EndpointInfo RedmiHfp = new(Id("6e4db349-0000-0000-0000-000000000000"),
        "Auriculares con micrófono (Redmi Buds 6 Pro Hands-Free)", "Auriculares con micrófono", "Redmi Buds 6 Pro Hands-Free",
        FormFactor.Headset, "{c3482619-0000-0000-0000-000000000000}", "BTHHFENUM", 1, false);
    internal static readonly EndpointInfo RealtekGeneric = new(Id("770acb0f-0000-0000-0000-000000000000"),
        "Altavoces (High Definition Audio Device)", "Altavoces", "High Definition Audio Device", FormFactor.Speakers,
        "{11111111-0000-0000-0000-000000000000}", "HDAUDIO", 1, false);
    internal static readonly EndpointInfo NvidiaP27v1 = new(Id("1404b2f1-0000-0000-0000-000000000000"),
        "P27FBB-RGGL (NVIDIA High Definition Audio)", "P27FBB-RGGL", "NVIDIA High Definition Audio", FormFactor.Hdmi,
        "{ca18f804-0000-0000-0000-000000000000}", "HDAUDIO", 1, false);
    internal static readonly EndpointInfo NvidiaP27v2 = NvidiaP27v1 with { EndpointId = Id("a8a62ca8-0000-0000-0000-000000000000") };
    internal static readonly EndpointInfo NvidiaP27Generic = NvidiaP27v1 with
    {
        EndpointId = Id("42ff8f87-0000-0000-0000-000000000000"), FriendlyName = "P27FBB-RGGL (High Definition Audio Device)",
        Adapter = "High Definition Audio Device",
    };
    internal static readonly EndpointInfo NvidiaMag = new(Id("a9572777-0000-0000-0000-000000000000"),
        "MAG 275Q E20 (NVIDIA High Definition Audio)", "MAG 275Q E20", "NVIDIA High Definition Audio", FormFactor.Hdmi,
        "{dddddddd-0000-0000-0000-000000000000}", "HDAUDIO", 1, false);
    internal static readonly EndpointInfo DualSense2 = new(Id("5179476f-0000-0000-0000-000000000000"),
        "Altavoces (2- DualSense Wireless Controller)", "Altavoces", "2- DualSense Wireless Controller", FormFactor.Speakers,
        "{eeeeeeee-0000-0000-0000-000000000000}", "USB", 1, false);
    internal static readonly EndpointInfo G435Port2 = G435 with
    {
        EndpointId = Id("99999999-0000-0000-0000-000000000000"),
        FriendlyName = "Audífono de los auriculares con micrófono (2- G435 Wireless Gaming Headset)",
        Adapter = "2- G435 Wireless Gaming Headset",
    };

    private static DeviceConfig Dev(string id, string tag, FormFactor? ff, string? desc, string[] pinned, params string[] adapters)
        => new(id, tag, tag, new DeviceMatchSpec(adapters, ff, desc, pinned, null), tag + " - General", true, null);

    private static readonly DeviceConfig[] Devices =
    [
        Dev("realtek", "Realtek", FormFactor.Speakers, null, [Realtek.EndpointId], "Realtek(R) Audio"),
        Dev("g435", "G435", FormFactor.Headset, null, [G435.EndpointId], "G435 Wireless Gaming Headset"),
        Dev("redmi", "Redmi", FormFactor.Headphones, null, [], "Redmi Buds 6 Pro"),
        Dev("p27", "P27", FormFactor.Hdmi, "P27FBB-RGGL", [], "NVIDIA High Definition Audio"),
        Dev("mag", "MAG", FormFactor.Hdmi, "MAG 275Q E20", [], "NVIDIA High Definition Audio"),
        Dev("dualsense", "DualSense", FormFactor.Speakers, null, [], "DualSense Wireless Controller"),
    ];

    private static readonly LearnedState Empty = new(1, new Dictionary<string, IReadOnlyList<string>>(), [], null);

    private static readonly EndpointInfo[] All = [G435, Realtek, FxVirtual];

    private static FxStatus Status(string? selectedOutput, bool power = true, params string[] outputs)
        => new("1.2.16.0", "General", power, false, 0, selectedOutput, [], ["General"], DateTimeOffset.Now)
        {
            OutputDevices = outputs.Length > 0 ? outputs : [G435.FriendlyName, Realtek.FriendlyName],
        };

    private static EndpointSnapshot Snap(string? defMultimedia, string? defConsole = null, params EndpointInfo[] eps)
        => new(DateTimeOffset.Now, eps.Length > 0 ? eps : All, defMultimedia, defConsole ?? defMultimedia);

    public static void Register()
    {
        Program.Add("devices: NormalizeAdapter strips port prefix, Bluetooth suffixes and extra spaces", () =>
        {
            Assert.Equal("G435 Wireless Gaming Headset", DeviceKeys.NormalizeAdapter("2- G435 Wireless Gaming Headset"), "2- prefix");
            Assert.Equal("DualSense Wireless Controller", DeviceKeys.NormalizeAdapter(" 4-  DualSense  Wireless Controller "), "4- prefix");
            Assert.Equal("Redmi Buds 6 Pro", DeviceKeys.NormalizeAdapter("Redmi Buds 6 Pro Hands-Free"), "hands-free");
            Assert.Equal("Some Buds", DeviceKeys.NormalizeAdapter("Some Buds Stereo"), "stereo");
            Assert.Equal("Realtek(R) Audio", DeviceKeys.NormalizeAdapter("Realtek(R) Audio"), "unchanged");
            Assert.True(DeviceKeys.SameAdapter("2- G435 wireless gaming headset", "G435 Wireless Gaming Headset"), "same ignoring case");
        });

        Program.Add("devices: SuggestTag / SuggestId / AdapterFromFriendlyName", () =>
        {
            Assert.Equal("Realtek", DeviceKeys.SuggestTag("Realtek(R) Audio"), "realtek tag");
            Assert.Equal("G435", DeviceKeys.SuggestTag("G435 Wireless Gaming Headset"), "g435 tag");
            Assert.Equal("DualSense", DeviceKeys.SuggestTag("2- DualSense Wireless Controller"), "dualsense tag");
            Assert.Equal("NVIDIA", DeviceKeys.SuggestTag("NVIDIA High Definition Audio"), "nvidia tag");
            Assert.Equal("HD Audio", DeviceKeys.SuggestTag("High Definition Audio Device"), "generic driver tag");
            Assert.Equal("Redmi Buds 6 Pro", DeviceKeys.SuggestTag("Redmi Buds 6 Pro Hands-Free"), "redmi tag");
            Assert.Equal("realtek", DeviceKeys.SuggestId("Realtek(R) Audio"), "realtek id");
            Assert.Equal("g435", DeviceKeys.SuggestId("2- G435 Wireless Gaming Headset"), "g435 id");
            Assert.Equal("redmi-buds-6-pro", DeviceKeys.SuggestId("Redmi Buds 6 Pro"), "redmi id");
            Assert.Equal("Realtek(R) Audio", DeviceKeys.AdapterFromFriendlyName("Altavoces (Realtek(R) Audio)"), "nested parentheses");
            Assert.Null(DeviceKeys.AdapterFromFriendlyName("Speakers"), "no adapter");
        });

        Program.Add("devices: known endpoint id (pinned) → that device, nothing learned", () =>
        {
            var m = DeviceMatcher.Match(G435, Devices, Empty, All);
            Assert.Equal("g435", m.DeviceId, "device");
            Assert.Equal(DeviceMatcher.HowEndpointId, m.How, "how");
            Assert.Null(m.LearnedEndpointId, "nothing to learn");
            Assert.Equal("G435", m.SuggestedTag, "tag");
        });

        Program.Add("devices: dongle in another USB port (2- prefix, new id) → same device, id learned", () =>
        {
            var m = DeviceMatcher.Match(G435Port2, Devices, Empty, [G435Port2, Realtek, FxVirtual]);
            Assert.Equal("g435", m.DeviceId, "device");
            Assert.Equal(DeviceMatcher.HowAdapterFormFactor, m.How, "how");
            Assert.Equal(G435Port2.EndpointId, m.LearnedEndpointId, "learned id");
            var learned = new LearnedState(1, new Dictionary<string, IReadOnlyList<string>> { ["g435"] = [G435Port2.EndpointId] }, [], null);
            var again = DeviceMatcher.Match(G435Port2, Devices, learned, All);
            Assert.Equal(DeviceMatcher.HowEndpointId, again.How, "next time by the learned id");
            Assert.Null(again.LearnedEndpointId, "nothing more to learn");
        });

        Program.Add("devices: driver update (NVIDIA, new id, same adapter) → learned; endpointDesc separates two monitors", () =>
        {
            var m = DeviceMatcher.Match(NvidiaP27v2, Devices, Empty, [NvidiaP27v2, NvidiaMag]);
            Assert.Equal("p27", m.DeviceId, "P27 monitor");
            Assert.Equal(NvidiaP27v2.EndpointId, m.LearnedEndpointId, "new id learned");
            Assert.Equal("mag", DeviceMatcher.Match(NvidiaMag, Devices, Empty, [NvidiaP27v2, NvidiaMag]).DeviceId, "MAG monitor");
        });

        Program.Add("devices: generic driver renames the adapter → matched by container id (stable for monitors)", () =>
        {
            var devs = Devices.Select(d => d.Id == "p27" ? d with { Match = d.Match with { ContainerId = NvidiaP27v1.ContainerId } } : d).ToList();
            var m = DeviceMatcher.Match(NvidiaP27Generic, devs, Empty, [NvidiaP27Generic]);
            Assert.Equal("p27", m.DeviceId, "same container");
            Assert.Equal(DeviceMatcher.HowContainer, m.How, "how");
        });

        Program.Add("devices: Realtek generic driver (other adapter, other container) → unknown with suggested tag", () =>
        {
            var m = DeviceMatcher.Match(RealtekGeneric, Devices, Empty, [RealtekGeneric]);
            Assert.Null(m.DeviceId, "unknown");
            Assert.Equal(DeviceMatcher.HowUnknown, m.How, "how");
            Assert.Equal("HD Audio", m.SuggestedTag, "suggested tag");
        });

        Program.Add("devices: Bluetooth A2DP + HFP grouped into one device; HFP = call mode", () =>
        {
            var both = new[] { RedmiA2dp, RedmiHfp };
            var a = DeviceMatcher.Match(RedmiA2dp, Devices, Empty, both);
            Assert.Equal("redmi", a.DeviceId, "A2DP");
            Assert.True(!a.CallMode, "A2DP is not call mode");
            var h = DeviceMatcher.Match(RedmiHfp, Devices, Empty, both);
            Assert.Equal("redmi", h.DeviceId, "HFP grouped");
            Assert.True(h.CallMode, "HFP = call mode");

            // profile recognised only by the A2DP pinned id: the HFP endpoint goes through its sibling
            var pinnedOnly = new[] { Dev("redmi", "Redmi", FormFactor.Headphones, null, [RedmiA2dp.EndpointId], "Other Name") };
            var h2 = DeviceMatcher.Match(RedmiHfp, pinnedOnly, Empty, both);
            Assert.Equal("redmi", h2.DeviceId, "via sibling");
            Assert.True(h2.CallMode, "call mode");
            Assert.Null(h2.LearnedEndpointId, "the HFP id is not learned through the sibling");
        });

        Program.Add("devices: DualSense with 2- prefix; FxSound virtual; weak adapter-only match", () =>
        {
            Assert.Equal("dualsense", DeviceMatcher.Match(DualSense2, Devices, Empty, [DualSense2]).DeviceId, "dualsense");
            var v = DeviceMatcher.Match(FxVirtual, Devices, Empty, All);
            Assert.Null(v.DeviceId, "virtual is not a device");
            Assert.Equal(DeviceMatcher.HowFxSoundVirtual, v.How, "how");
            var lineOut = Realtek with { EndpointId = Id("12345678-0000-0000-0000-000000000000"), FormFactor = FormFactor.LineLevel };
            var w = DeviceMatcher.Match(lineOut, Devices, Empty, [lineOut]);
            Assert.Equal("realtek", w.DeviceId, "single profile with that adapter");
            Assert.True(w.Weak, "weak");
            Assert.Null(w.LearnedEndpointId, "weak matches are not learned");
        });

        Program.Add("output: through FxSound → endpoint named by selected_output (listed)", () =>
        {
            var e = OutputResolver.Resolve(Status(Realtek.FriendlyName), true, Snap(FxVirtual.EndpointId));
            Assert.True(e.ThroughFxSound, "through FxSound");
            Assert.Equal(Realtek.EndpointId, e.Endpoint?.EndpointId, "Realtek");
            Assert.True(!e.RequeryStatus, "no re-query");
        });

        Program.Add("output: fake selected_output not in output_devices → Windows default, re-query", () =>
        {
            var e = OutputResolver.Resolve(Status("Altavoces (Realtek(R) Audio) X"), true, Snap(FxVirtual.EndpointId, Realtek.EndpointId));
            Assert.True(e.ThroughFxSound, "still through FxSound");
            Assert.Equal(Realtek.EndpointId, e.Endpoint?.EndpointId, "Windows default (console role)");
            Assert.True(e.RequeryStatus, "re-query");
            Assert.True(e.Why.Contains("not in output_devices", StringComparison.Ordinal), "why: " + e.Why);

            var one = OutputResolver.Resolve(Status("bogus", true, G435.FriendlyName), true, Snap(FxVirtual.EndpointId));
            Assert.Equal(G435.EndpointId, one.Endpoint?.EndpointId, "the only listed output");
            var none = OutputResolver.Resolve(Status("bogus"), true, Snap(FxVirtual.EndpointId));
            Assert.Null(none.Endpoint, "unknown real output");
        });

        Program.Add("output: FxSound not the default, closed or off → Windows default / nothing", () =>
        {
            var bypass = OutputResolver.Resolve(Status(Realtek.FriendlyName), true, Snap(G435.EndpointId));
            Assert.True(!bypass.ThroughFxSound, "bypassed");
            Assert.Equal(G435.EndpointId, bypass.Endpoint?.EndpointId, "Windows default");
            var closed = OutputResolver.Resolve(null, false, Snap(FxVirtual.EndpointId));
            Assert.True(!closed.ThroughFxSound && closed.Endpoint is null, "virtual default without FxSound → no device");
            var off = OutputResolver.Resolve(Status(Realtek.FriendlyName, power: false), true, Snap(FxVirtual.EndpointId));
            Assert.True(!off.ThroughFxSound, "power off");
        });

        Program.Add("output: FxSoundRenderEndpointId differs from selected_output → render endpoint wins + re-query", () =>
        {
            var e = OutputResolver.Resolve(Status(Realtek.FriendlyName), true, Snap(FxVirtual.EndpointId), G435.EndpointId);
            Assert.Equal(G435.EndpointId, e.Endpoint?.EndpointId, "render endpoint");
            Assert.True(e.RequeryStatus, "re-query requested");
            var agree = OutputResolver.Resolve(Status(Realtek.FriendlyName), true, Snap(FxVirtual.EndpointId), Realtek.EndpointId);
            Assert.True(!agree.RequeryStatus && agree.Endpoint == Realtek, "consistent → no re-query");
        });

        Program.Add("output: real status.json with two outputs (accented names) resolves the G435", () =>
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "status.two-outputs.json"));
            var st = Assert.NotNull(FxStatusParser.TryParse(json, DateTimeOffset.Now), "parse");
            var e = OutputResolver.Resolve(st, true, Snap(FxVirtual.EndpointId));
            Assert.Equal(G435.EndpointId, e.Endpoint?.EndpointId, "G435 by its exact Spanish name");
            Assert.Equal("g435", DeviceMatcher.Match(e.Endpoint!, Devices, Empty, All).DeviceId, "device");
        });
    }
}
