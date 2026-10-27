using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.FxSound;
using FxAuto.Core.Localization;

namespace FxAuto.Core.Presets;

/// <summary>REQ-106: assignment(device, source) ?? assignment(device, category) ?? device.defaultPreset.
/// The same preset may be assigned to several devices. Volume leveling comes from presets[name].volumeLeveling.</summary>
public static class PresetResolver
{
    public const string WhySource = "source override";
    public const string WhyCategory = "category";
    public const string WhyDefault = "device default";

    public static PresetResolution Resolve(FxAutoConfig cfg, string? deviceId, SourceClass? winner)
        => Resolve(cfg, deviceId, winner, null);

    /// <param name="fx">Current FxSound status: a preset FxSound does not have is never returned; resolution falls
    /// back to the device default if FxSound has it (warning text in Why). null = not checked.</param>
    public static PresetResolution Resolve(FxAutoConfig cfg, string? deviceId, SourceClass? winner, FxStatus? fx)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (deviceId is null) return new PresetResolution(null, 0, "unknown device: no change") { WhyText = new Msg("Res.UnknownDevice") };
        var dev = cfg.Devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (dev is null) return new PresetResolution(null, 0, $"device \"{deviceId}\" is not configured: no change") { WhyText = new Msg("Res.NotConfigured", deviceId) };
        if (!dev.Enabled) return new PresetResolution(null, 0, $"device \"{dev.Id}\" is disabled: no change") { WhyText = new Msg("Res.Disabled", dev.DisplayName) };
        if (winner is not null && winner.Category == Categories.Hold)
            return new PresetResolution(null, 0, "hold source playing: no change") { WhyText = new Msg("Res.Hold") };

        string? preset = null;
        var why = WhyDefault;
        Msg? whyText = null;
        if (winner is not null)
        {
            var mine = cfg.Assignments.Where(a => string.Equals(a.Device, dev.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var bySource = winner.SourceId is null ? null
                : mine.FirstOrDefault(a => a.Source is not null
                    && string.Equals(a.Source, winner.SourceId, StringComparison.OrdinalIgnoreCase));
            if (bySource is not null && !string.IsNullOrWhiteSpace(bySource.Preset))
            {
                preset = bySource.Preset;
                why = $"{WhySource} ({winner.SourceId})";
                whyText = new Msg("Res.Source", cfg.Sources.FirstOrDefault(s => string.Equals(s.Id, winner.SourceId, StringComparison.OrdinalIgnoreCase))?.Name ?? winner.SourceId);
            }
            else
            {
                var byCategory = mine.FirstOrDefault(a => a.Source is null && a.Category is not null
                    && string.Equals(a.Category, winner.Category, StringComparison.OrdinalIgnoreCase));
                if (byCategory is not null && !string.IsNullOrWhiteSpace(byCategory.Preset))
                {
                    preset = byCategory.Preset;
                    why = $"{WhyCategory} ({winner.Category})";
                    whyText = new Msg("Res.Category", new Msg("Category." + winner.Category));
                }
            }
        }

        var def = string.IsNullOrWhiteSpace(dev.DefaultPreset) ? null : dev.DefaultPreset;
        if (preset is null)
        {
            preset = def;
            why = winner is null ? WhyDefault + " (nothing playing)" : WhyDefault;
            whyText = new Msg(winner is null ? "Res.DefaultIdle" : "Res.Default");
            if (preset is null) return new PresetResolution(null, 0, $"device \"{dev.Id}\" has no default preset: no change") { WhyText = new Msg("Res.NoDefault", dev.DisplayName) };
        }

        if (fx is not null && !Exists(fx, preset))
        {
            var missing = preset;
            if (def is not null && !string.Equals(def, preset, StringComparison.Ordinal) && Exists(fx, def))
                return new PresetResolution(def, Leveling(cfg, def), $"{WhyDefault}; warning: \"{missing}\" is missing in FxSound") { WhyText = new Msg("Res.MissingFallback", missing) };
            return new PresetResolution(null, 0, $"warning: \"{missing}\" is missing in FxSound: no change") { WhyText = new Msg("Res.Missing", missing) };
        }
        return new PresetResolution(preset, Leveling(cfg, preset), why) { WhyText = whyText };
    }

    private static bool Exists(FxStatus fx, string preset)
        => fx.UserPresets.Contains(preset, StringComparer.Ordinal) || fx.BuiltInPresets.Contains(preset, StringComparer.Ordinal);

    private static double Leveling(FxAutoConfig cfg, string preset)
        => cfg.Presets.TryGetValue(preset, out var meta) ? meta.VolumeLeveling : 0;
}
