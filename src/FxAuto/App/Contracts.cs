using FxAuto.Core;
using FxAuto.Core.Audio;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Devices;
using FxAuto.Core.FxSound;
using FxAuto.Core.Presets;

// Layer contract of the Windows layer (plan.md "Layer contract", frozen in task 3). Additions are fine; signature
// changes go to the lead. Implementations: Audio (task 4a), Devices (4b), Browser (4c), App/Ui (5, 6).

namespace FxAuto.Audio
{
    /// <summary>Audio session monitor (dedicated MTA thread). Posts snapshots at ≤ 4 Hz only while a session on the
    /// FxSound virtual endpoint is active, plus one final "all quiet" snapshot.</summary>
    internal interface IAudioSessionMonitor : IDisposable
    {
        event Action<AudioSnapshot>? Sampled;
        void Start();
        /// <summary>Forces a re-enumeration (e.g. after a device change).</summary>
        void Refresh();
    }
}

namespace FxAuto.Devices
{
    /// <summary>Endpoint monitor (IMMNotificationClient + 750 ms debounce, second snapshot ~3 s later).</summary>
    internal interface IDeviceMonitor : IDisposable
    {
        event Action<EndpointSnapshot>? Changed;
        EndpointSnapshot Snapshot();
        void Start();
    }
}

namespace FxAuto.Browser
{
    /// <summary>Read-only browser tab reader (UI Automation, own MTA thread, one pending request at a time).</summary>
    internal interface IBrowserTabReader
    {
        /// <summary>Tabs of the browser rooted at <paramref name="rootPid"/>; null = unavailable.</summary>
        IReadOnlyList<BrowserTab>? ReadTabs(int rootPid);
        /// <summary>Titles of the top-level windows of that process tree (GetWindowText, no UIA).</summary>
        IReadOnlyList<string> WindowTitles(int rootPid);
    }
}

namespace FxAuto.App
{
    /// <summary>What the settings window talks to (UI thread). Calls post events to the coordinator.</summary>
    internal interface ISettingsHost
    {
        VersionedConfig Config { get; }
        StatusSnapshot Status { get; }
        EndpointSnapshot Endpoints { get; }
        event Action<StatusSnapshot>? StatusChanged;
        /// <summary>The UI rebases its working copy.</summary>
        event Action<VersionedConfig>? ConfigChanged;

        /// <summary>Validate → atomic save → swap → re-decide now. Rejected with an Error issue "stale" if
        /// <paramref name="baseVersion"/> != current Version; the UI then rebases and asks again.</summary>
        Task<ApplyConfigResult> ApplyConfigAsync(FxAutoConfig next, long baseVersion);
        /// <summary>Preset ops save their own config edits and return the new VersionedConfig for the UI to rebase onto.</summary>
        Task<(PresetOpResult Result, VersionedConfig Config)> RunPresetOpAsync(PresetOpRequest req, CancellationToken ct);
        Task<FxStatus?> RefreshFxStatusAsync();
        /// <summary>"Create a source from what is playing".</summary>
        IReadOnlyList<DetectedSource> DetectPlayingNow();
        /// <summary>Auto / Locked / Paused.</summary>
        Task SetModeAsync(Mode mode, string? lockedPreset = null, TimeSpan? pauseFor = null);
        /// <summary>Enters Mode.Editing.</summary>
        Task<PresetOpResult> BeginEditAsync(string preset);
        Task<(PresetOpResult Result, VersionedConfig Config)> FinishEditAsync(EditOutcome outcome, string? saveAsName);
        /// <summary>HKCU Run (AutoStart).</summary>
        bool StartWithWindows { get; }
        void SetStartWithWindows(bool on);

        /// <summary>Like <see cref="ApplyConfigAsync"/>, but the user confirmed replacing a config.json FxAuto could not
        /// use (<see cref="StatusSnapshot.ConfigSaveBlocked"/>). Added in task 5 (default: plain apply).</summary>
        Task<ApplyConfigResult> ApplyConfigReplacingAsync(FxAutoConfig next, long baseVersion) => ApplyConfigAsync(next, baseVersion);

        /// <summary>Folder of FxAuto's log files (About → Open log folder). Same folder Program.cs logs to.</summary>
        string LogFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FxAuto");

        /// <summary>Update engine for About (check, download, install); null = not available (About hides the
        /// actions). Added with the updates UI.</summary>
        IUpdateController? Updates => null;
    }

    internal sealed record ApplyConfigResult(bool Applied, IReadOnlyList<ConfigIssue> Issues, VersionedConfig Current);

    internal enum SettingsPage { Status, Devices, Presets, Sources, Categories, General, About }

    /// <summary>Opens the settings window on a page (UI thread); used by the "configure" notification.</summary>
    internal interface ISettingsWindowOpener { void Open(SettingsPage page, string? focusId = null); }

    /// <summary>Status published by the coordinator for the tray and the Status page.</summary>
    internal sealed record StatusSnapshot(Mode Mode, bool FxRunning, bool? FxElevated, bool ThroughFxSound,
        string? DeviceId, string? DeviceName, string? OutputName, string? ExpectedPreset, string? VerifiedPreset,
        string Why, IReadOnlyList<PlayingSourceView> Playing, IReadOnlyList<ConfigIssue> Issues, string? LastFailure)
    {
        /// <summary>Preset being edited in FxSound (Mode.Editing, from the edit-pending marker). Added in task 5.</summary>
        public string? EditingPreset { get; init; }
        /// <summary>Why the configuration cannot be saved now (config.json read-only, or unusable on disk and
        /// protected until it reads valid again or the user confirms replacing it). null = saves allowed. Added in task 5.</summary>
        public string? ConfigSaveBlocked { get; init; }
        /// <summary>True when <see cref="ConfigSaveBlocked"/> is a protected (unusable on disk) config that the user may
        /// replace after confirming; false when it is read-only and can never be saved. Added in task 5.</summary>
        public bool ConfigSaveReplaceable { get; init; }
        /// <summary><see cref="Why"/> as data for the UI (translated there); the log keeps <see cref="Why"/>.</summary>
        public Core.Localization.Msg? WhyText { get; init; }
        /// <summary><see cref="ConfigSaveBlocked"/> as data for the UI.</summary>
        public Core.Localization.Msg? ConfigSaveBlockedText { get; init; }
        /// <summary>Why in the UI language.</summary>
        public string WhyShown => Core.Localization.Loc.Show(WhyText, Why);
    }

    /// <summary>One playing (or recently playing) source as shown in Status. Added in task 3: the plan names the type only.</summary>
    internal sealed record PlayingSourceView(int RootPid, string? Exe, string Category, string? SourceId, string? Site,
        string How, bool IsForeground, bool Playing, bool IsWinner, DateTimeOffset? AudibleSince);

    /// <summary>A source detected as playing now, offered to "create a source from what is playing". Added in task 3.</summary>
    internal sealed record DetectedSource(int RootPid, string? Exe, string? ExePath, string? Aumid, string? Title,
        SourceClass Class);

    // ---- coordinator events (AppEvent is in AppEvents.cs) ----

    internal sealed record AudioSnapshotEvent(DateTimeOffset ArrivedAt, AudioSnapshot Snapshot) : AppEvent(ArrivedAt);

    internal sealed record EndpointSnapshotEvent(DateTimeOffset ArrivedAt, EndpointSnapshot Snapshot) : AppEvent(ArrivedAt);

    /// <summary>Result of a tab read. RequestId lets the coordinator ignore late results of abandoned requests.
    /// Tabs null = UIA unavailable or failed.</summary>
    internal sealed record BrowserTabsEvent(DateTimeOffset ArrivedAt, long RequestId, int RootPid,
        IReadOnlyList<BrowserTab>? Tabs, IReadOnlyList<string> WindowTitles) : AppEvent(ArrivedAt);

    /// <summary>UI asks to apply a config (ISettingsHost.ApplyConfigAsync).</summary>
    internal sealed record ConfigAppliedEvent(DateTimeOffset ArrivedAt, FxAutoConfig Next, long BaseVersion,
        TaskCompletionSource<ApplyConfigResult> Reply) : AppEvent(ArrivedAt)
    {
        /// <summary>The user confirmed replacing an unusable config.json (protected mode). Added in task 5.</summary>
        public bool ConfirmReplace { get; init; }
    }

    /// <summary>UI asks to run a preset operation; the coordinator pauses the engine and runs it exclusively.</summary>
    internal sealed record PresetOpEvent(DateTimeOffset ArrivedAt, PresetOpRequest Request, CancellationToken Ct,
        TaskCompletionSource<(PresetOpResult Result, VersionedConfig Config)> Reply) : AppEvent(ArrivedAt);

    /// <summary>A preset operation finished on the thread pool (same pattern as ApplyCompletedEvent).</summary>
    internal sealed record PresetOpCompletedEvent(DateTimeOffset ArrivedAt, PresetOpRequest Request, PresetOpResult Result)
        : AppEvent(ArrivedAt);

    /// <summary>Other ISettingsHost requests answered by the coordinator thread.</summary>
    internal enum SettingsRequestKind { RefreshFxStatus, DetectPlayingNow, SetMode, BeginEdit, FinishEdit }

    /// <summary>Generic UI → coordinator request; the coordinator completes <see cref="Reply"/> with the
    /// kind-specific result (FxStatus?, IReadOnlyList&lt;DetectedSource&gt;, PresetOpResult…).</summary>
    internal sealed record SettingsRequestEvent(DateTimeOffset ArrivedAt, SettingsRequestKind Kind,
        Mode? Mode = null, string? Preset = null, TimeSpan? PauseFor = null, EditOutcome? Outcome = null,
        TaskCompletionSource<object?>? Reply = null) : AppEvent(ArrivedAt);
}
