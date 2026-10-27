namespace FxAuto.Core.Devices;

// Layer contract (plan.md "Layer contract", frozen in task 3).

/// <summary>Endpoint form factor (PKEY_AudioEndpoint_FormFactor).</summary>
public enum FormFactor { Unknown, Speakers, LineLevel, Headphones, Microphone, Headset, Handset,
    UnknownDigitalPassthrough, Spdif, Hdmi }

/// <summary>One audio render endpoint as read from MMDevice.</summary>
public sealed record EndpointInfo(string EndpointId, string FriendlyName, string? EndpointDesc, string? Adapter,
    FormFactor FormFactor, string? ContainerId, string? Bus, int State, bool IsFxSoundVirtual);

/// <summary>Immutable snapshot posted by the device monitor.</summary>
public sealed record EndpointSnapshot(DateTimeOffset At, IReadOnlyList<EndpointInfo> Endpoints,
    string? DefaultMultimediaId, string? DefaultConsoleId);

/// <summary>Where audio really goes (REQ-105).</summary>
public sealed record EffectiveOutput(bool ThroughFxSound, EndpointInfo? Endpoint, string Why)
{
    /// <summary>status.json and the real render endpoint disagree (or selected_output is not trustworthy): the
    /// coordinator should ask for a fresh --status and resolve again. Added in task 3c.</summary>
    public bool RequeryStatus { get; init; }

    /// <summary><see cref="Why"/> as data for the UI (translated there); the log keeps <see cref="Why"/>.</summary>
    public Localization.Msg? WhyText { get; init; }
}

/// <summary>Result of matching an endpoint to a configured device.</summary>
public sealed record DeviceMatch(string? DeviceId, string How, string? LearnedEndpointId, string SuggestedTag)
{
    /// <summary>The endpoint is a Bluetooth hands-free (HFP) profile: "call mode", no preset change. Added in task 3c.</summary>
    public bool CallMode { get; init; }
    /// <summary>Weak match (adapter only): the coordinator logs a warning. Added in task 3c.</summary>
    public bool Weak { get; init; }
}
