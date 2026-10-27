using FxAuto.Core;
using FxAuto.Core.FxSound;

namespace FxAuto.App;

/// <summary>Event for the coordinator. ArrivedAt is the arrival time.</summary>
internal abstract record AppEvent(DateTimeOffset ArrivedAt);

// Core events. The audio, endpoint, tab and settings-request events are in App/Contracts.cs.

internal sealed record ForegroundEvent(DateTimeOffset ArrivedAt, ForegroundInfo Info) : AppEvent(ArrivedAt);
internal sealed record TitleChangedEvent(DateTimeOffset ArrivedAt, ForegroundInfo Info) : AppEvent(ArrivedAt);
internal sealed record ProcessExitedEvent(DateTimeOffset ArrivedAt, int Pid) : AppEvent(ArrivedAt);
internal sealed record TickEvent(DateTimeOffset ArrivedAt) : AppEvent(ArrivedAt);
internal sealed record ResumedEvent(DateTimeOffset ArrivedAt) : AppEvent(ArrivedAt);
internal sealed record ShutdownEvent(DateTimeOffset ArrivedAt, string Why) : AppEvent(ArrivedAt);

/// <summary>Hand edit of config.json seen by <see cref="ConfigStore"/>.</summary>
internal sealed record ConfigFileChangedEvent(DateTimeOffset ArrivedAt, ConfigFileChange Change) : AppEvent(ArrivedAt);

/// <summary>Browser tab reading was disabled after consecutive timeouts.</summary>
internal sealed record TabReadingDisabledEvent(DateTimeOffset ArrivedAt) : AppEvent(ArrivedAt);

/// <summary>Tray menu command. Arg: preset (LockPreset) or output name (SwitchOutput); PauseFor null = until restart.</summary>
internal sealed record MenuCommandEvent(DateTimeOffset ArrivedAt, MenuCommand Command, string? Arg = null,
                                        TimeSpan? PauseFor = null) : AppEvent(ArrivedAt);

internal enum MenuCommand { Auto, LockCurrent, LockPreset, Pause, StartFxSound, SwitchOutput, ReenableTabReading }

/// <summary>Result of an ApplyAsync/EnsureAsync/QueryAsync/FxSound start launched without await.</summary>
internal sealed record ApplyCompletedEvent(DateTimeOffset ArrivedAt, string Kind, string? Preset, ApplyResult? Result,
                                           FxStatus? Status, bool? Started, string? Error) : AppEvent(ArrivedAt);

/// <summary>Tray notification. Page: the settings page a click on the balloon opens (null = none).</summary>
internal sealed record Notification(string Title, string Text, bool Error, SettingsPage? Page = null, string? FocusId = null)
{
    /// <summary>UI text of <see cref="Title"/> (translated in the tray); the log keeps <see cref="Title"/>.</summary>
    public Core.Localization.Msg? TitleText { get; init; }
    /// <summary>UI text of <see cref="Text"/>.</summary>
    public Core.Localization.Msg? BodyText { get; init; }
}

/// <summary>What the tray shows: the status plus FxSound's lists (presets, outputs) and transient flags.</summary>
internal sealed record TrayState(StatusSnapshot Status, FxStatus? Fx, bool StartingFxSound, bool PresetOpRunning,
                                 DateTimeOffset? PausedUntil);
