using FxAuto.Core.FxSound;

namespace FxAuto.Core.Presets;

// Layer contract (plan.md "Layer contract", frozen in task 3).

/// <summary>Preset to apply and why ("source override", "category", "device default"...). Preset null = send nothing.</summary>
public sealed record PresetResolution(string? Preset, double Leveling, string Why)
{
    /// <summary><see cref="Why"/> as data for the UI (translated there); the log keeps <see cref="Why"/>.</summary>
    public Localization.Msg? WhyText { get; init; }
}

/// <summary>Result of validating a preset name (REQ-107).</summary>
public sealed record NameCheck(bool Ok, string? Error, string Sanitized)
{
    /// <summary><see cref="Error"/> as data for the UI (translated there).</summary>
    public Localization.Msg? ErrorText { get; init; }

    /// <summary>Error in the UI language (falls back to <see cref="Error"/>).</summary>
    public string? ShownError => Error is null ? null : Localization.Loc.Show(ErrorText, Error);
}

/// <summary>Preset management operations, each a verified step list.</summary>
public enum PresetOpKind { Duplicate, Rename, Delete, SwitchOutput, EditBegin, EditFinish }

/// <summary>How an "Edit in FxSound" session ends.</summary>
public enum EditOutcome { Save, SaveAsNew, Discard }

/// <summary>Request for <c>PresetOperations.RunAsync</c>.</summary>
public sealed record PresetOpRequest(PresetOpKind Kind, string? Source = null, string? Target = null,
    string? OutputName = null, EditOutcome? Outcome = null);

/// <summary>Result of a preset operation: the steps run, the final FxSound state and the renames done.</summary>
public sealed record PresetOpResult(bool Ok, string? Error, IReadOnlyList<string> Steps,
    FxStatus? Final, IReadOnlyList<(string From, string To)> Done)
{
    /// <summary>FxSound's active preset changed (always after a delete: FxSound selects General): the coordinator
    /// must re-apply the expected preset right away. Added in task 3c.</summary>
    public bool ReapplyRequested { get; init; }

    /// <summary><see cref="Error"/> as data for the UI (translated there); the log keeps <see cref="Error"/>.</summary>
    public Localization.Msg? ErrorText { get; init; }

    /// <summary>Error in the UI language (a known English text is translated too; otherwise <see cref="Error"/>).</summary>
    public string? ShownError => Error is null ? null : ErrorText is not null ? Localization.Loc.Render(ErrorText) : Localization.Loc.Known(Error);
}

/// <summary>FxSound preset files (.fac). Implemented in FxAuto (Windows); tests use a fake one.</summary>
public interface IPresetFileStore
{
    /// <summary>Finds a preset file by its line 3 (name), in Presets\ then Factsoft\. null if not found.</summary>
    string? FindPresetFile(string presetName);
    /// <summary>Writes an exact copy of <paramref name="sourceFile"/> named <paramref name="newName"/>. Returns the error or null.</summary>
    string? WriteExactCopy(string sourceFile, string newName);
    /// <summary>FxSound's AutoSave copy of that preset exists.</summary>
    bool AutoSaveExists(string presetName);
    /// <summary>Maximum number of user presets (FxSound.settings, or 120).</summary>
    int MaxUserPresets();
}
