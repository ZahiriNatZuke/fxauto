using FxAuto.Core.FxSound;
using FxAuto.Core.Localization;

namespace FxAuto.Core.Devices;

/// <summary>Effective output (research 02 §5.1): through FxSound only if FxSound runs, the default multimedia endpoint
/// is the FxSound virtual one and power is on; then the real endpoint is <c>selected_output</c> only if that name is
/// listed in <c>output_devices</c> (the <c>--output</c> trap: a fake name stays saved). The endpoint where FxSound.exe
/// really renders, when known, wins over <c>selected_output</c> and asks for a fresh <c>--status</c>.</summary>
public static class OutputResolver
{
    private const int StateActive = 1;

    public static EffectiveOutput Resolve(FxStatus? st, bool fxRunning, EndpointSnapshot eps)
        => Resolve(st, fxRunning, eps, null);

    /// <param name="fxSoundRenderEndpointId">AudioSnapshot.FxSoundRenderEndpointId: physical endpoint where FxSound.exe
    /// has its render session (REQ-105 cross-check). null = unknown.</param>
    public static EffectiveOutput Resolve(FxStatus? st, bool fxRunning, EndpointSnapshot eps, string? fxSoundRenderEndpointId)
    {
        ArgumentNullException.ThrowIfNull(eps);
        var def = ById(eps, eps.DefaultMultimediaId);
        var defIsFx = def is not null && DeviceMatcher.IsFxSoundVirtual(def);

        if (!fxRunning || !defIsFx || st is null || !st.Power)
        {
            var why = !defIsFx ? "Windows default output (FxSound is not the default)"
                : !fxRunning ? "FxSound is the Windows default but is not running"
                : st is null ? "FxSound is the Windows default but its status is unknown"
                : "FxSound is the Windows default but processing is off";
            var whyText = new Msg(!defIsFx ? "Out.NotDefault" : !fxRunning ? "Out.NotRunning" : st is null ? "Out.StatusUnknown" : "Out.PowerOff");
            if (defIsFx)
            {
                // audio goes to the virtual endpoint and FxSound does not process it: no real device to resolve
                return new EffectiveOutput(false, null, why) { WhyText = whyText };
            }
            return new EffectiveOutput(false, def, def is null ? why + "; no default endpoint" : why)
            { WhyText = def is null ? new Msg("Out.NoDefault", whyText) : whyText };
        }

        var name = st.SelectedOutput;
        var listed = name is not null && st.OutputDevices.Contains(name, StringComparer.Ordinal);
        var selected = listed ? ByFriendlyName(eps, name!) : null;
        var render = ById(eps, fxSoundRenderEndpointId);
        if (render is not null && DeviceMatcher.IsFxSoundVirtual(render)) render = null;

        if (render is not null)
        {
            if (selected is not null && string.Equals(selected.EndpointId, render.EndpointId, StringComparison.OrdinalIgnoreCase))
                return new EffectiveOutput(true, selected, "FxSound selected output (confirmed by its render session)") { WhyText = new Msg("Out.SelectedConfirmed") };
            return new EffectiveOutput(true, render,
                $"FxSound renders on \"{render.FriendlyName}\" but selected_output is \"{name ?? "(none)"}\"")
            { RequeryStatus = true, WhyText = new Msg("Out.RendersElsewhere", render.FriendlyName, name ?? "—") };
        }

        if (selected is not null)
            return new EffectiveOutput(true, selected, "FxSound selected output") { WhyText = new Msg("Out.Selected") };

        // selected_output absent from output_devices (fake name) or not found among the endpoints
        var why2 = name is null ? "selected_output missing"
            : !listed ? $"selected_output \"{name}\" is not in output_devices"
            : $"selected_output \"{name}\" not found among the endpoints";
        var why2Text = name is null ? new Msg("Out.SelectedMissing") : !listed ? new Msg("Out.SelectedNotListed", name)
            : new Msg("Out.SelectedNotFound", name);

        // only one real output listed: it is the only place FxSound can render
        var listedEndpoints = st.OutputDevices.Select(n => ByFriendlyName(eps, n)).OfType<EndpointInfo>()
            .Where(e => !DeviceMatcher.IsFxSoundVirtual(e)).ToList();
        if (listedEndpoints.Count == 1)
            return new EffectiveOutput(true, listedEndpoints[0], why2 + "; the only output FxSound lists")
            { RequeryStatus = true, WhyText = new Msg("Out.OnlyListed", why2Text) };

        // otherwise the Windows default that is not the FxSound virtual endpoint (console role), if any
        var console = ById(eps, eps.DefaultConsoleId);
        if (console is not null && !DeviceMatcher.IsFxSoundVirtual(console))
            return new EffectiveOutput(true, console, why2 + "; Windows default console output")
            { RequeryStatus = true, WhyText = new Msg("Out.Console", why2Text) };
        return new EffectiveOutput(true, null, why2 + "; real output unknown") { RequeryStatus = true, WhyText = new Msg("Out.Unknown", why2Text) };
    }

    private static EndpointInfo? ById(EndpointSnapshot eps, string? id)
        => id is null ? null
            : eps.Endpoints.FirstOrDefault(e => string.Equals(e.EndpointId, id, StringComparison.OrdinalIgnoreCase));

    private static EndpointInfo? ByFriendlyName(EndpointSnapshot eps, string name)
    {
        // FxSound compares FriendlyName exactly; prefer an active endpoint (Windows keeps old ones with the same name)
        var matches = eps.Endpoints.Where(e => string.Equals(e.FriendlyName, name, StringComparison.Ordinal)).ToList();
        return matches.FirstOrDefault(e => e.State == StateActive) ?? matches.FirstOrDefault();
    }
}
