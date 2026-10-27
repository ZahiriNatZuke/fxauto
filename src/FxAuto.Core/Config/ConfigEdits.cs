namespace FxAuto.Core.Config;

/// <summary>Pure config edits used by the UI and by preset operations. Preset names are compared exactly (ordinal),
/// like FxAuto sends them to FxSound.</summary>
public static class ConfigEdits
{
    /// <summary>Renames a preset in presets (its settings move with it), device defaults and assignments. If
    /// <paramref name="to"/> already had settings, the settings of <paramref name="from"/> replace them.</summary>
    public static FxAutoConfig RenamePresetEverywhere(FxAutoConfig c, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        if (string.Equals(from, to, StringComparison.Ordinal)) return c;

        var presets = new Dictionary<string, PresetMeta>(StringComparer.Ordinal);
        foreach (var (name, meta) in c.Presets)
        {
            if (string.Equals(name, from, StringComparison.Ordinal)) presets[to] = meta;
            else if (!string.Equals(name, to, StringComparison.Ordinal) || !c.Presets.ContainsKey(from)) presets[name] = meta;
        }
        return c with
        {
            Presets = presets,
            Devices = [.. c.Devices.Select(d => Is(d.DefaultPreset, from) ? d with { DefaultPreset = to } : d)],
            Assignments = [.. c.Assignments.Select(a => Is(a.Preset, from) ? a with { Preset = to } : a)],
        };
    }

    /// <summary>Removes a preset: its settings go; device defaults and assignments that used it move to
    /// <paramref name="replacement"/>. With a null replacement those assignments are dropped; a device default
    /// cannot be dropped (it is mandatory), so InvalidOperationException is thrown when one uses the preset.</summary>
    public static FxAutoConfig RemovePreset(FxAutoConfig c, string name, string? replacement)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (replacement is not null && string.Equals(replacement, name, StringComparison.Ordinal))
            throw new ArgumentException("The replacement is the preset being removed.", nameof(replacement));

        if (replacement is null)
        {
            var user = c.Devices.FirstOrDefault(d => Is(d.DefaultPreset, name));
            if (user is not null)
                throw new InvalidOperationException(
                    $"Preset \"{name}\" is the default of device \"{user.Id}\"; choose a replacement.");
        }

        var presets = new Dictionary<string, PresetMeta>(StringComparer.Ordinal);
        foreach (var (n, meta) in c.Presets)
        {
            if (!string.Equals(n, name, StringComparison.Ordinal)) presets[n] = meta;
        }
        var assignments = replacement is null
            ? c.Assignments.Where(a => !Is(a.Preset, name)).ToList()
            : [.. c.Assignments.Select(a => Is(a.Preset, name) ? a with { Preset = replacement } : a)];
        return c with
        {
            Presets = presets,
            Devices = [.. c.Devices.Select(d => replacement is not null && Is(d.DefaultPreset, name) ? d with { DefaultPreset = replacement } : d)],
            Assignments = assignments,
        };
    }

    /// <summary>Every preset name the config refers to (device defaults, assignments, preset settings), in first-seen order.</summary>
    public static IReadOnlyList<string> ReferencedPresets(FxAutoConfig c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var n in c.Devices.Select(d => d.DefaultPreset).Concat(c.Assignments.Select(a => a.Preset)).Concat(c.Presets.Keys))
        {
            if (!string.IsNullOrEmpty(n) && seen.Add(n)) list.Add(n);
        }
        return list;
    }

    private static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.Ordinal);
}
