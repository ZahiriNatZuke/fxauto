using System.Globalization;
using System.Text;

namespace FxAuto.Core.FxSound;

/// <summary>Builds FxSound.exe arguments (invariant culture, quoting per CommandLineToArgvW).</summary>
public static class FxCommand
{
    public const string Status = "--status";

    /// <summary>--power=1 --preset="X" --volume_leveling=N --run_minimized</summary>
    public static string Apply(string preset, double leveling)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return $"--power=1 --preset={Quote(preset)} --volume_leveling={FormatNumber(leveling)} --run_minimized";
    }

    // ---- preset operation builders ----
    // One preset/output operation per call (FxSound runs only the highest-priority preset command of a call, and
    // --preset is processed before --output). Names are always quoted; numbers use the invariant culture. Every
    // builder except EditInFxSound ends with --run_minimized, which also restores FxSound's hidden window state.

    public const string RunMinimized = "--run_minimized";

    /// <summary>--preset="X" --run_minimized (no leveling, no power: selects only).</summary>
    public static string SelectPreset(string name) => $"--preset={Quote(Name(name))} {RunMinimized}";

    /// <summary>--output="exact friendly name" --run_minimized, alone. Only names listed in output_devices[] may be
    /// sent (an unknown name stays saved in FxSound and the real output does not change).</summary>
    public static string Output(string friendlyName) => $"--output={Quote(Name(friendlyName))} {RunMinimized}";

    /// <summary>--set_effect=name:value --run_minimized. The value keeps 6 decimals so re-sending the current value
    /// is not audible. The effect name is a bare identifier (verified unquoted in the spike).</summary>
    public static string SetEffect(string name, double value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || !name.All(c => char.IsAsciiLetterLower(c) || c == '_'))
            throw new ArgumentException("effect name must be lower-case letters or '_'", nameof(name));
        return $"--set_effect={name}:{value.ToString("0.######", CultureInfo.InvariantCulture)} {RunMinimized}";
    }

    /// <summary>--save_preset="X" --run_minimized (acts only if the active preset is modified).</summary>
    public static string SavePreset(string name) => $"--save_preset={Quote(Name(name))} {RunMinimized}";

    /// <summary>--rename_preset="X" --run_minimized (renames the active preset).</summary>
    public static string RenamePreset(string name) => $"--rename_preset={Quote(Name(name))} {RunMinimized}";

    /// <summary>--delete_preset --run_minimized (deletes the active preset; FxSound then selects another one).</summary>
    public static string DeletePreset() => "--delete_preset " + RunMinimized;

    /// <summary>--overwrite_preset --run_minimized (saves the active preset's current settings over it).</summary>
    public static string OverwritePreset() => "--overwrite_preset " + RunMinimized;

    /// <summary>--undo_preset --run_minimized (drops unsaved changes of the active preset and its AutoSave copy).</summary>
    public static string UndoPreset() => "--undo_preset " + RunMinimized;

    /// <summary>--view=N alone (shows FxSound's window; 2 = full view with the equalizer).</summary>
    public static string View(int mode) => "--view=" + mode.ToString(CultureInfo.InvariantCulture);

    /// <summary>--preset="X" --view=2: selects the preset and shows FxSound's window (no --run_minimized).</summary>
    public static string EditInFxSound(string name) => $"--preset={Quote(Name(name))} {View(2)}";

    private static string Name(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0) throw new ArgumentException("empty name", nameof(name));
        return name;
    }

    /// <summary>Number formatted with CultureInfo.InvariantCulture (1.5, never 1,5).</summary>
    public static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Quotes a value for CommandLineToArgvW.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
