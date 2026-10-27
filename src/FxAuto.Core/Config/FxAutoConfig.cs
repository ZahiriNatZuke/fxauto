using FxAuto.Core.Arbitration;
using FxAuto.Core.Audio;
using FxAuto.Core.Classification;
using FxAuto.Core.Devices;

namespace FxAuto.Core.Config;

// Layer contract (plan.md "Layer contract", frozen in task 3): config.json model. Task 3a owns this file and may
// add members; signature changes go to the lead.

/// <summary>config.json (schemaVersion 2).</summary>
public sealed record FxAutoConfig(int SchemaVersion, GeneralSettings General,
    IReadOnlyDictionary<string, PresetMeta> Presets, IReadOnlyList<DeviceConfig> Devices,
    IReadOnlyList<SourceConfig> Sources, IReadOnlyList<Assignment> Assignments,
    IReadOnlyList<string> IgnoredApps, IReadOnlyList<string> Launchers, IReadOnlyList<string> GameFolderExclusions);

/// <summary>General settings. StartFxSoundIfClosed (REQ-112) and LogTitles (privacy) default to false;
/// UnknownDevice is "keepCurrent".</summary>
public sealed record GeneralSettings(string? FxSoundPath, bool StartWithWindows, bool StartFxSoundIfClosed,
    string LogLevel, bool LogTitles, int StartupGraceSeconds, int CheckIntervalSeconds,
    SoundingTimings Detection, int IdleGraceSeconds, int MinDwellMs, int GameLoadingMaxMs, CallPolicy CallPolicy,
    string UnknownDevice, bool ReadBrowserTabs, IReadOnlyList<string> AudibleTabMarkers)
{
    /// <summary>Language of the user interface: "system" (Windows display language), "en" or "es". Optional key
    /// <c>general.language</c>; absent = "system". The log, config keys and preset names stay in English.</summary>
    public string Language { get; init; } = UiChoices.System;

    /// <summary>Colours of the settings window: "system" (Windows app mode), "light" or "dark". Optional key
    /// <c>general.theme</c>; absent = "system".</summary>
    public string Theme { get; init; } = UiChoices.System;

    /// <summary>Update preferences, optional object <c>general.updates</c>. Null = the keys are absent: the defaults
    /// apply (<see cref="UpdatePreferences.Default"/>) and settings kept by an older build in update-state.json may be
    /// migrated here. Use <see cref="EffectiveUpdates"/> to read them.</summary>
    public UpdatePreferences? Updates { get; init; }

    /// <summary><see cref="Updates"/>, or the defaults when the keys are absent.</summary>
    public UpdatePreferences EffectiveUpdates => Updates ?? UpdatePreferences.Default;
}

/// <summary><c>general.updates</c>: <c>checkAutomatically</c> (daily check on GitHub Releases, on by default) and
/// <c>includePrereleases</c> (off by default). Both keys are optional.</summary>
public sealed record UpdatePreferences(bool CheckAutomatically, bool IncludePrereleases)
{
    public static readonly UpdatePreferences Default = new(CheckAutomatically: true, IncludePrereleases: false);
}

/// <summary>Accepted values of <see cref="GeneralSettings.Language"/> and <see cref="GeneralSettings.Theme"/>.</summary>
public static class UiChoices
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static readonly IReadOnlyList<string> Languages = [System, Localization.Loc.English, Localization.Loc.Spanish];
    public static readonly IReadOnlyList<string> Themes = [System, Light, Dark];

    /// <summary>True = dark colours. "system" follows <paramref name="windowsAppsDark"/> (Windows "app mode");
    /// unknown values behave like "system".</summary>
    public static bool ResolveDark(string? theme, bool windowsAppsDark) => theme?.Trim().ToLowerInvariant() switch
    {
        Light => false,
        Dark => true,
        _ => windowsAppsDark,
    };
}

/// <summary>Per-preset metadata kept by FxAuto (FxSound keeps volume leveling global).</summary>
public sealed record PresetMeta(double VolumeLeveling, string? Note);

/// <summary>How an endpoint is recognised as a device. PinnedEndpointIds are user-pinned only; learned ids live in
/// runtime-state.json (<see cref="LearnedState"/>).</summary>
public sealed record DeviceMatchSpec(IReadOnlyList<string> Adapters, FormFactor? FormFactor, string? EndpointDesc,
    IReadOnlyList<string> PinnedEndpointIds, string? ContainerId);

/// <summary>A configured output device (REQ-105).</summary>
public sealed record DeviceConfig(string Id, string DisplayName, string Tag, DeviceMatchSpec Match,
    string DefaultPreset, bool Enabled, string? LastSeenOutputName);

/// <summary>How a source is matched. Site = exact title segment, or "*" only together with TitleRegex.</summary>
public sealed record SourceMatch(IReadOnlyList<string> Exe, IReadOnlyList<string> Aumid, IReadOnlyList<string> Folder,
    IReadOnlyList<string> Site, string? TitleRegex);

/// <summary>A configured source. Origin: "builtIn" | "user" | "migrated" (older configs; read and kept as is).</summary>
public sealed record SourceConfig(string Id, string Name, SourceKind Kind, SourceMatch Match, string Category,
    bool Fallback, string Origin);

/// <summary>Preset for a device and a category or a source (REQ-106).</summary>
public sealed record Assignment(string Device, string? Category, string? Source, string Preset);

/// <summary>Severity of a config issue: errors block saving, warnings are allowed.</summary>
public enum IssueLevel { Error, Warning, Info }

/// <summary>One validation/parse issue. Path is a JSON-like path ("devices[0].tag").</summary>
public sealed record ConfigIssue(IssueLevel Level, string Path, string Message)
{
    /// <summary><see cref="Message"/> as data for the UI (translated there); logs keep <see cref="Message"/>.</summary>
    public Localization.Msg? Text { get; init; }

    /// <summary>Message in the UI language (falls back to <see cref="Message"/>).</summary>
    public string Shown => Text is not null ? Localization.Loc.Render(Text) : Localization.Loc.Known(Message);

    /// <summary>Issue whose English <see cref="Message"/> and UI <see cref="Text"/> come from the catalog key.</summary>
    public static ConfigIssue Of(IssueLevel level, string path, string key, params object?[] args)
    {
        var text = new Localization.Msg(key, args);
        return new ConfigIssue(level, path, Localization.Loc.RenderEnglish(text)) { Text = text };
    }
}

/// <summary>Runtime state, NOT config: %APPDATA%\FxAuto\runtime-state.json, written only by the coordinator
/// (atomic .tmp + File.Replace; if corrupt, start empty and show a warning in Status).</summary>
public sealed record LearnedState(int SchemaVersion, IReadOnlyDictionary<string, IReadOnlyList<string>> EndpointIdsByDevice,
    IReadOnlyList<string> NotifiedUnknownDeviceKeys, string? EditPendingPreset)
{
    /// <summary>The user's mode to restore after a restart (Locked with its preset, or Paused with an end); null =
    /// Auto. Absent in files written before it existed.</summary>
    public SavedMode? UserMode { get; init; }
}

/// <summary>Persisted user mode: <see cref="Mode.Locked"/> with <paramref name="LockedPreset"/>, or
/// <see cref="Mode.Paused"/> with <paramref name="PausedUntil"/>. "Paused until restart" is never persisted.</summary>
public sealed record SavedMode(Mode Mode, string? LockedPreset, DateTimeOffset? PausedUntil);

/// <summary>Config with its version for optimistic concurrency between the UI working copy and the live config.
/// Version increments on every save (UI, preset op, hand edit).</summary>
public sealed record VersionedConfig(FxAutoConfig Config, long Version);
