using System.Globalization;
using System.Threading.Channels;
using FxAuto.Audio;
using FxAuto.Browser;
using FxAuto.Core;
using FxAuto.Core.Arbitration;
using FxAuto.Core.Audio;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Devices;
using FxAuto.Core.FxSound;
using FxAuto.Core.Presets;
using FxAuto.Foreground;
using FxAuto.FxSound;
using FxAuto.Core.Localization;

namespace FxAuto.App;

/// <summary>
/// Coordinator: a single thread consumes the event queue and is the only one touching the Core objects
/// (classifier, tab tracker, sounding detector, arbiter, apply gate). Decision cycle: SoundingDetector → candidates
/// (classifier + tab tracker + process start times) → SourceArbiter → OutputResolver + DeviceMatcher (learned endpoint
/// ids into runtime-state.json) → PresetResolver → FxSoundController. It never waits for FxSound: commands, queries
/// and preset operations run on the thread pool and come back as events, so Pause/Lock/Exit are
/// handled at once and "the latest wins".
/// </summary>
internal sealed class Coordinator : IDisposable
{
    // CPU: the timer is not periodic; it is armed for the next pending instant (dues), never sooner than MinWake.
    private static readonly TimeSpan MinWake = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxWake = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FxAliveCheck = TimeSpan.FromSeconds(5);       // process handle: microseconds
    private static readonly TimeSpan FxClosedRescan = TimeSpan.FromSeconds(10);    // process snapshot while closed
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HangRetryMargin = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RequeryMinInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StateSaveDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EditCap = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TabRequestMinInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan QuietReobserve = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ExitedMemory = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ResumeGap = TimeSpan.FromSeconds(3);     // silence that marks browser tabs stale

    private readonly Channel<AppEvent> _queue = Channel.CreateUnbounded<AppEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly IClock _clock = SystemClock.Instance;
    private readonly JsonlLog _log;
    private readonly FxSoundPort _port;
    private readonly ConfigStore _store;
    private readonly FxSoundController _controller;
    private readonly PresetOperations _ops;
    private readonly PresetFileStore _presetFiles;
    private readonly ProcessLifetime _lifetime;
    private readonly ProcessTree _tree = new();      // coordinator-owned (the audio monitor has its own)
    private readonly CancellationTokenSource _stopping = new();
    private readonly bool _autostart;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly UnresponsiveBackoff _hang = new();
    private readonly OnceSet _once = new();
    private readonly ApplyGate _gate;

    // Config (coordinator thread writes; other threads read the volatile published copies).
    private FxAutoConfig _cfg;
    private volatile FxAutoConfig _cfgForOps;
    private long _version = 1;
    private volatile VersionedConfig _versioned;
    private IReadOnlyList<ConfigIssue> _configIssues;
    private IReadOnlyList<ConfigIssue> _fileIssues = [];
    private IReadOnlyList<ConfigIssue> _stateIssues;
    private LearnedState _learned;
    private DateTimeOffset? _stateSaveDue;
    private string? _stateSaveError;

    // Core objects.
    private SourceClassifier _classifier;
    private BrowserTabTracker _tracker;
    private SoundingDetector _detector;
    private SourceArbiter _arbiter;
    private ArbiterTimings _arbiterTimings;

    // Mode.
    private Mode _mode = Mode.Auto;
    private Mode _modeBeforeEdit = Mode.Auto;
    private string? _lockedPreset;
    private DateTimeOffset? _pausedUntil;
    private string? _editingPreset;
    private DateTimeOffset? _editSince;
    private bool _restoredLockUnconfirmed;        // lock restored from runtime-state.json, not yet checked against FxSound's lists

    // Sources.
    private ForegroundInfo? _fg;
    private int? _fgRoot;
    private readonly Dictionary<int, (string? Exe, string? Path, string? Aumid)> _roots = [];
    private readonly Dictionary<int, DateTimeOffset?> _starts = [];
    private readonly Dictionary<int, SourceClass> _classCache = [];
    private readonly Dictionary<int, SourceClass> _classMemo = [];     // every root, cleared when an input changes
    private readonly Dictionary<int, DateTimeOffset> _exited = [];
    private readonly Dictionary<int, string> _browserTitles = [];
    private readonly Dictionary<int, DateTimeOffset> _tabRequestedAt = [];
    private AudioSnapshot? _audio;
    private IReadOnlyList<PlayingState> _states = [];
    private Dictionary<int, bool> _playing = [];
    private string? _renderEndpointId;
    private volatile EndpointSnapshot _endpoints;
    private BrowserTabService? _tabs;
    private bool _tabsDisabled;

    // FxSound.
    private FxStatus? _status;
    private volatile FxStatus? _statusForUi;
    private bool _fxRunning;
    private int? _fxPid;                // main FxSound PID seen by the last liveness check
    private Dictionary<int, int> _browserSessions = [];   // playing browser root → signature of its audio sessions
    private bool? _fxElevated;
    private bool _startingFx;
    private bool _launchedByUs;
    private bool _hangWarned;
    private DateTimeOffset? _graceUntil;
    private DateTimeOffset _nextFxCheck;
    private DateTimeOffset _nextPeriodic;
    private DateTimeOffset? _resumeDue;
    private bool _queryInFlight;
    private bool _queryAgain;
    private readonly List<TaskCompletionSource<object?>> _queryReplies = [];
    private DateTimeOffset _lastRequery = DateTimeOffset.MinValue;
    private string _missingKey = "";

    // Preset operations.
    private bool _opRunning;
    private Action<PresetOpResult, VersionedConfig>? _opDone;

    // Last decision (for Status and logs).
    private ArbiterDecision? _decision;
    private EffectiveOutput? _output;
    private DeviceMatch? _match;
    private EndpointInfo? _matchedEndpoint;
    private string _why = "starting";
    private Msg? _whyText = new("Why.Starting");
    private string? _lastDecisionKey;
    private string? _lastOutputKey;
    private IReadOnlyList<PlayingSourceView> _views = [];
    private volatile IReadOnlyList<DetectedSource> _detected = [];
    private bool _shuttingDown;
    private bool _pendingConfigChanged;
    private Thread? _loop;
    private System.Threading.Timer? _tickTimer;
    private int _tickQueued;
    private DateTimeOffset? _scheduledWake;
    private bool _quietEvent;          // the event changed nothing visible: no publish (CPU)
    private volatile TrayState? _published;

    /// <summary>New state for the tray and the settings window (raised on the coordinator thread).</summary>
    public event Action<TrayState>? StatusChanged;
    /// <summary>The live config changed (UI apply, preset op, hand edit). Raised on the coordinator thread.</summary>
    public event Action<VersionedConfig>? ConfigChanged;
    /// <summary>Tray balloon notification (raised on the coordinator thread).</summary>
    public event Action<Notification>? Notify;
    /// <summary>The coordinator finished (after Stop).</summary>
    public event Action? Stopped;

    /// <summary>Forces the audio monitor to re-enumerate (set by Program: device change).</summary>
    public Action? RefreshAudio { get; set; }

    /// <summary>detection.pollMs changed (set by Program: restarts the audio monitor with the new rate).</summary>
    public Action<int>? AudioPollChanged { get; set; }

    public Coordinator(ConfigLoad load, ConfigStore store, JsonlLog log, FxSoundPort port, EndpointSnapshot endpoints,
                       bool autostart)
    {
        ArgumentNullException.ThrowIfNull(load);
        _store = store;
        _log = log;
        _port = port;
        _autostart = autostart;
        _endpoints = endpoints;
        _cfg = _cfgForOps = load.Config;
        _versioned = new VersionedConfig(_cfg, _version);
        _configIssues = load.Issues;
        var (state, stateIssues) = store.LoadState();
        _learned = state;
        _stateIssues = stateIssues;
        _gate = new ApplyGate(_clock);
        _controller = new FxSoundController(port, _clock, (ev, data) => log.Log(ev, data), new FxControllerTimings());
        _presetFiles = port.CreatePresetFileStore();
        _ops = new PresetOperations(_controller, _presetFiles, _clock, (ev, data) => log.Log(ev, data))
        {
            CurrentConfig = () => _cfgForOps,
        };
        _lifetime = new ProcessLifetime(pid => Post(new ProcessExitedEvent(DateTimeOffset.Now, pid)));
        _classifier = new SourceClassifier(_cfg);
        _tracker = new BrowserTabTracker(_classifier, _clock);
        _detector = new SoundingDetector(_cfg.General.Detection, _clock);
        _arbiterTimings = SourceArbiter.TimingsFrom(_cfg.General);
        _arbiter = new SourceArbiter(_arbiterTimings, _clock);
        _published = BuildTrayState();
    }

    // ---- what other threads read ----

    public VersionedConfig Config => _versioned;
    public EndpointSnapshot Endpoints => _endpoints;
    public TrayState Current => _published ?? BuildTrayState();
    public IReadOnlyList<DetectedSource> DetectedNow => _detected;
    public FxStatus? LastFxStatus => _statusForUi;

    /// <summary>Queues an event (any thread).</summary>
    public void Post(AppEvent e) => _queue.Writer.TryWrite(e);

    public void Start()
    {
        var now = _clock.Now;
        _fxPid = _port.AliveMainPid();
        _fxRunning = _fxPid is not null;
        _fxElevated = _fxRunning ? _port.IsElevated() : null;
        _log.Log("fxsound.state", new { open = _fxRunning, elevated = _fxElevated, autostart = _autostart });
        if (_fxRunning)
        {
            RequestQuery("startup");
        }
        else if (_cfg.General.StartFxSoundIfClosed)
        {
            if (_autostart && AutoStart.FxSoundHasAutostart())
            {
                // Started with Windows and FxSound also starts by itself: give it a margin before opening it.
                _graceUntil = now + TimeSpan.FromSeconds(Math.Max(0, _cfg.General.StartupGraceSeconds));
                _log.Log("fxsound.grace", new { until = _graceUntil });
            }
            else
            {
                StartFxSound("startup");
            }
        }
        else
        {
            // REQ-112: FxAuto does not start FxSound unless the user opted in.
            _log.Log("fxsound.not_started", new { reason = "startFxSoundIfClosed is off" });
        }

        RestoreUserMode(now);

        if (_learned.EditPendingPreset is { } pending)
        {
            // A crash or restart during "Edit in FxSound": resume in Editing, never discard on our own.
            _modeBeforeEdit = _mode;
            _mode = Mode.Editing;
            _editingPreset = pending;
            _editSince = now;
            _log.Log("edit.resumed", new { preset = pending });
            Raise(Note(new Msg("Note.C01.Title"),
                new Msg("Note.C01.Text", pending),
                false, SettingsPage.Presets, pending));
        }

        UpdateTabService();
        _nextFxCheck = now + (_fxRunning ? FxAliveCheck : FxClosedRescan);
        _nextPeriodic = now + PeriodicInterval;
        LogOutputDevices();

        // One-shot timer re-armed after every event for the next pending instant (Reschedule).
        _tickTimer = new System.Threading.Timer(_ => QueueTick(), null, Timeout.Infinite, Timeout.Infinite);
        _loop = new Thread(Loop) { IsBackground = true, Name = "FxAuto.Coordinator" };
        _loop.Start();
        Post(new TickEvent(now));
    }

    /// <summary>Restores the user's mode saved in runtime-state.json: Locked only if its preset still exists in
    /// FxSound's preset files (checked again against FxSound's lists on the first status), Paused only while its end
    /// is in the future. "Paused until restart" is never saved, so it ends with the restart as before.</summary>
    private void RestoreUserMode(DateTimeOffset now)
    {
        var saved = _learned.UserMode;
        if (saved is null) return;
        var (mode, locked, until, dropped) = CoordinatorLogic.RestoreMode(saved, now, p => _presetFiles.FindPresetFile(p) is not null);
        _mode = mode;
        _lockedPreset = locked;
        _pausedUntil = until;
        _restoredLockUnconfirmed = mode == Mode.Locked;
        if (dropped is not null)
        {
            _log.Log("mode.restore", new { mode = "Auto", reason = "locked preset not found", preset = dropped });
            Raise(Note(new Msg("Note.C02.Title"),
                new Msg("Note.C02.Text", dropped), false));
        }
        else
        {
            _log.Log("mode.restore", new { mode = mode.ToString(), preset = locked, until, saved = saved.Mode.ToString() });
        }
        PersistUserMode();       // drops an expired pause or a missing lock from the file
    }

    /// <summary>First FxSound status after restoring a lock: if FxSound's lists do not have the preset, the lock
    /// ends (back to Auto, with a notice), as when the locked preset is deleted.</summary>
    private void ConfirmRestoredLock()
    {
        if (!_restoredLockUnconfirmed || _status is null) return;
        if (_status.UserPresets.Count + _status.BuiltInPresets.Count == 0) return;   // lists not read: wait
        _restoredLockUnconfirmed = false;
        if (_lockedPreset is not { } locked || _status.UserPresets.Contains(locked) || _status.BuiltInPresets.Contains(locked)) return;
        var cur = new ModeNames(_mode, _modeBeforeEdit, _lockedPreset, _editingPreset, _learned.EditPendingPreset);
        var (next, _) = CoordinatorLogic.FollowPresetOp(cur, PresetOpKind.Delete, [(locked, "")]);
        _mode = next.Mode;
        _modeBeforeEdit = next.ModeBeforeEdit;
        _lockedPreset = next.Locked;
        _log.Log("mode", new { mode = _mode.ToString(), origin = "restored lock: preset not in FxSound", preset = locked });
        Raise(Note(new Msg("Note.C03.Title"),
            new Msg("Note.C03.Text", locked), false));
        Decide("restored lock dropped");
    }

    /// <summary>Saves the user's mode in runtime-state.json when it changed (atomic write; runs after every event,
    /// so mode changes, pause ends, renames and deletes are all covered).</summary>
    private void PersistUserMode()
    {
        var saved = CoordinatorLogic.ToSaved(_mode, _modeBeforeEdit, _lockedPreset, _pausedUntil);
        if (saved == _learned.UserMode) return;
        _learned = _learned with { UserMode = saved };
        SaveStateNow();
    }

    private TimeSpan PeriodicInterval => TimeSpan.FromSeconds(Math.Max(10, _cfg.General.CheckIntervalSeconds));

    private void QueueTick()
    {
        if (Interlocked.Exchange(ref _tickQueued, 1) == 0) Post(new TickEvent(DateTimeOffset.Now));
    }

    private void Loop()
    {
        var reader = _queue.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var e))
            {
                _quietEvent = false;
                try
                {
                    if (Handle(e)) return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log.Log("coordinator.exception", new { @event = e.GetType().Name, error = ex.ToString() });
                }
                try { AfterEvent(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log.Log("coordinator.exception", new { @event = "after", error = ex.ToString() });
                }
            }
        }
    }

    /// <summary>Handles an event. true = stop.</summary>
    private bool Handle(AppEvent e)
    {
        switch (e)
        {
            case AudioSnapshotEvent a: OnAudio(a.Snapshot); break;
            case EndpointSnapshotEvent d: OnEndpoints(d.Snapshot); break;
            case ForegroundEvent f: OnForeground(f.Info, "focus"); break;
            case TitleChangedEvent t: OnForeground(t.Info, "title"); break;
            case BrowserTabsEvent b: OnTabs(b); break;
            case TabReadingDisabledEvent:
                _tabsDisabled = true;
                _log.Log("tabs.disabled", new { after_timeouts = BrowserTabService.MaxConsecutiveTimeouts });
                break;
            case ProcessExitedEvent p: OnProcessExited(p.Pid); break;
            case TickEvent:
                Interlocked.Exchange(ref _tickQueued, 0);
                _scheduledWake = null;          // fired (or a manual tick): re-armed after this event
                OnTick();
                break;
            case ResumedEvent:
                _log.Log("power.resume", new { });
                _resumeDue = _clock.Now + ResumeDelay;
                break;
            case ConfigAppliedEvent c: OnConfigApplied(c); break;
            case ConfigFileChangedEvent f: OnConfigFile(f.Change); break;
            case PresetOpEvent op:
                StartPresetOp(op.Request, op.Ct, (r, v) => op.Reply.TrySetResult((r, v)));
                break;
            case PresetOpCompletedEvent done: OnPresetOpCompleted(done); break;
            case SettingsRequestEvent s: OnSettingsRequest(s); break;
            case MenuCommandEvent m: OnMenu(m); break;
            case ApplyCompletedEvent a: OnCompleted(a); break;
            case ShutdownEvent s:
                OnShutdown(s.Why);
                return true;
        }
        return false;
    }

    // ───────────── inputs ─────────────

    private void OnAudio(AudioSnapshot raw)
    {
        var snap = CoordinatorLogic.WithoutIgnored(raw, _cfg.IgnoredApps);
        _audio = snap;
        bool changed = false;
        foreach (var s in snap.Sessions)
        {
            if (s.RootExe is null && _roots.ContainsKey(s.RootPid)) continue;
            var root = (s.RootExe, s.RootPath, s.RootAumid);
            if (_roots.TryGetValue(s.RootPid, out var known) && known == root) continue;
            _roots[s.RootPid] = root;
            changed = true;
        }
        if (changed) _classMemo.Clear();
        if (!string.Equals(raw.FxSoundRenderEndpointId, _renderEndpointId, StringComparison.OrdinalIgnoreCase))
        {
            changed = true;
            _log.Log("fxsound.render_endpoint", new { from = _renderEndpointId, to = raw.FxSoundRenderEndpointId,
                                                      name = EndpointName(raw.FxSoundRenderEndpointId) });
            _renderEndpointId = raw.FxSoundRenderEndpointId;
            if (_fxRunning) RequestQuery("render endpoint");   // REQ-105 cross-check with selected_output
        }
        if (Observe(snap)) changed = true;
        MarkBrowsersWithNewSessions(snap);
        // CPU: a sample that changed nothing the decision uses (the usual case while something plays) does not
        // run a decision; timed changes (release, grace, dwell, retry) come from the timer.
        if (changed) Decide("audio");
        else _quietEvent = true;
    }

    /// <summary>Feeds the detector. true when the arbiter's inputs changed.</summary>
    private bool Observe(AudioSnapshot snap)
    {
        var states = _detector.Observe(snap, pid => Classify(pid).IsGame, pid => pid == _fgRoot);
        var changes = CoordinatorLogic.PlayingChanges(_playing, states);
        bool changed = changes.Count > 0 || !CoordinatorLogic.SameDecisionStates(_states, states);
        foreach (var (pid, playing) in changes)
        {
            var exe = ExeOf(pid);
            if (_classifier.IsBrowser(exe))
            {
                if (playing && pid != _fgRoot)
                {
                    // Background browser: latch on the title of its most recently focused window (no UIA needed).
                    var title = FirstWindowTitle(pid);
                    if (title is not null) _browserTitles[pid] = title;
                    _tracker.OnBrowserTitle(pid, exe!, title);
                }
                _tracker.OnPlayingChanged(pid, playing);
            }
            _classMemo.Clear();
            var c = Classify(pid);
            _log.Log("source.playing", new
            {
                pid, exe, playing, category = c.Category, sourceId = c.SourceId, site = c.Site, how = c.How,
                foreground = pid == _fgRoot,
                title = _cfg.General.LogTitles && _browserTitles.TryGetValue(pid, out var t) ? Trim(t) : null,
            });
        }
        foreach (var pid in CoordinatorLogic.ResumedAfterSilence(_states, states, ResumeGap))
            if (_classifier.IsBrowser(ExeOf(pid))) _tracker.MarkStale(pid);   // another tab may be the one playing now
        _states = states;
        if (changed) _playing = states.ToDictionary(s => s.RootPid, s => s.Playing);
        RequestTabsIfNeeded();
        return changed;
    }

    private void OnEndpoints(EndpointSnapshot snap)
    {
        _endpoints = snap;
        _log.Log("devices.changed", new
        {
            endpoints = snap.Endpoints.Count, @default = EndpointName(snap.DefaultMultimediaId),
        });
        RefreshAudio?.Invoke();
        if (_fxRunning) RequestQuery("device");
        Decide("device");
        Ensure("device");
    }

    private void OnForeground(ForegroundInfo info, string kind)
    {
        bool neutral = CoordinatorLogic.IsNeutralForeground(info, _ownPid, _cfg.IgnoredApps, _cfg.Launchers);
        if (kind == "title" && !_cfg.General.LogTitles && (neutral || TitleIrrelevant(info)))
        {
            // CPU: a title change that cannot change anything (terminals animate their title): no log line, no
            // decision. The foreground info keeps the new title for the Settings "detected" list.
            if (!neutral) KeepTitle(info);
            _quietEvent = true;
            return;
        }
        _log.Log(kind, new
        {
            exe = info.Exe, pid = info.Pid, aumid = info.Aumid, unreadable = info.Unreadable, neutral,
            title = _cfg.General.LogTitles ? Trim(info.Title) : null,
        });
        if (neutral) return;          // ignored apps, launchers, FxAuto's own windows: keep the previous foreground
        _fg = info;
        int? previousRoot = _fgRoot;
        var root = _tree.ResolveInfo(info.Pid);
        _fgRoot = root.RootPid;
        _roots[root.RootPid] = (root.RootExe ?? info.Exe, root.RootPath ?? info.FullPath, root.RootAumid ?? info.Aumid);
        if (root.RootPid == info.Pid) _starts[info.Pid] = info.ProcessStart;
        var exe = ExeOf(root.RootPid);
        if (_classifier.IsBrowser(exe))
        {
            _tracker.OnBrowserTitle(root.RootPid, exe!, info.Title);
            if (!string.IsNullOrWhiteSpace(info.Title)) _browserTitles[root.RootPid] = info.Title;
            _tracker.MarkStale(root.RootPid);            // focus or title changed: the audible tab may have moved
        }
        if (previousRoot is int prev && prev != root.RootPid && _classifier.IsBrowser(ExeOf(prev)))
            _tracker.MarkStale(prev);                    // focus left a browser: it now plays in the background
        _classMemo.Clear();
        Decide(kind);
        RequestTabsIfNeeded();
    }

    /// <summary>A playing browser whose audio sessions (session PIDs or their active state) changed: the audible tab may
    /// have moved, so its tabs are read again.</summary>
    private void MarkBrowsersWithNewSessions(AudioSnapshot snap)
    {
        if (_tabs is null || _tabsDisabled || !_playing.ContainsValue(true))
        {
            if (_browserSessions.Count > 0) _browserSessions = [];
            return;
        }
        var now = CoordinatorLogic.SessionSignatures(snap,
            root => _playing.TryGetValue(root, out var p) && p && _classifier.IsBrowser(ExeOf(root)));
        foreach (var root in CoordinatorLogic.ChangedSignatures(_browserSessions, now))
            _tracker.MarkStale(root);
        _browserSessions = now;
        RequestTabsIfNeeded();
    }

    /// <summary>Title change of the current foreground process whose title no rule uses (not a browser, no
    /// <c>titleRegex</c> source applies to it).</summary>
    private bool TitleIrrelevant(ForegroundInfo info)
    {
        if (_fg is not { } fg || fg.Pid != info.Pid || fg.Hwnd != info.Hwnd || _fgRoot is not int root) return false;
        var (exe, path, aumid) = _roots.TryGetValue(root, out var r) ? r : (null, null, null);
        return !_classifier.TitleMatters(new SourceContext(root, exe, path, aumid, null, null));
    }

    private void KeepTitle(ForegroundInfo info)
    {
        _fg = info;
        if (_fgRoot is not int root) return;
        var detected = _detected;
        if (detected.Any(d => d.RootPid == root))
            _detected = detected.Select(d => d.RootPid == root ? d with { Title = info.Title } : d).ToList();
    }

    private void OnTabs(BrowserTabsEvent b)
    {
        if (_tabs is null) return;                      // tab reading switched off meanwhile
        if (!_tracker.OnTabs(b.RootPid, b.Tabs, b.RequestId))
        {
            _quietEvent = true;                         // answer to an older request or another playback: dropped
            return;
        }
        if (b.RootPid != _fgRoot && b.WindowTitles.Count > 0)
        {
            _tracker.OnBrowserTitle(b.RootPid, ExeOf(b.RootPid) ?? "", b.WindowTitles[0]);
            _browserTitles[b.RootPid] = b.WindowTitles[0];
        }
        if (_cfg.General.LogLevel == "debug")
            _log.Log("tabs.read", new { pid = b.RootPid, ok = b.Tabs is not null, tabs = b.Tabs?.Count, audible = b.Tabs?.Count(t => t.Audible) });
        _classMemo.Clear();
        Decide("tabs");
    }

    private void OnProcessExited(int pid)
    {
        _log.Log("process.exit", new { pid });
        _exited[pid] = _clock.Now;
        _tracker.Forget(pid);
        _classCache.Remove(pid);
        _starts.Remove(pid);
        _browserTitles.Remove(pid);
        _tabRequestedAt.Remove(pid);
        if (_fgRoot == pid) _fgRoot = null;
        _classMemo.Clear();
        Decide("process exit");
    }

    private void OnTick()
    {
        var now = _clock.Now;
        _quietEvent = true;           // set back to false by every branch that does something

        if (now >= _nextFxCheck)
        {
            // CPU: the main FxSound's process handle answers while it runs; a snapshot only while it is closed.
            int? pid = _port.AliveMainPid();
            bool running = pid is not null;
            var change = CoordinatorLogic.FxChange(_fxRunning, _fxPid, pid);
            _fxPid = pid;
            _nextFxCheck = now + (running ? FxAliveCheck : FxClosedRescan);
            _quietEvent = false;      // publish at this slow pace anyway (safety net for state read by the tray)
            if (change == FxProcessChange.Restarted)
            {
                // Another main FxSound PID: it was closed and opened again between two checks.
                _fxElevated = _port.IsElevated();
                _log.Log("fxsound.state", new { open = true, elevated = _fxElevated, restarted = true });
                _graceUntil = null;
                _status = null;
                _statusForUi = null;
                _gate.ResetApplied();
                if (!_startingFx) RequestQuery("fxsound restarted");
                Decide("fxsound state");
            }
            else if (running != _fxRunning)
            {
                _fxRunning = running;
                _fxElevated = running ? _port.IsElevated() : null;
                _log.Log("fxsound.state", new { open = running, elevated = _fxElevated });
                if (running)
                {
                    _graceUntil = null;
                    _gate.ResetApplied();              // FxSound just opened: re-apply
                    if (!_startingFx) RequestQuery("fxsound opened");
                }
                else
                {
                    _status = null;
                    _statusForUi = null;
                    if (!_startingFx)
                        Raise(Note(new Msg("Note.C04.Title"),
                            new Msg("Note.C04.Text"), true));
                }
                Decide("fxsound state");
            }
        }

        if (_graceUntil is { } g && now >= g)
        {
            _quietEvent = false;
            _graceUntil = null;
            if (!_fxRunning && _cfg.General.StartFxSoundIfClosed) StartFxSound("grace ended");
        }

        if (CoordinatorLogic.PauseExpired(_mode, _pausedUntil, now))
        {
            _log.Log("mode", new { mode = "Auto", origin = "pause ended" });
            _mode = Mode.Auto;
            _pausedUntil = null;
            Decide("pause ended");
        }

        if (_mode == Mode.Editing && _editSince is { } since && now - since >= EditCap && _once.First("edit-cap:" + _editingPreset))
        {
            _quietEvent = false;
            _log.Log("edit.cap", new { preset = _editingPreset, minutes = (int)EditCap.TotalMinutes });
            Raise(Note(new Msg("Note.C05.Title"),
                new Msg("Note.C05.Text", _editingPreset),
                false, SettingsPage.Presets, _editingPreset));
        }

        // Releases, grace end, dwell end, retry after failure, output re-check.
        var due = CoordinatorLogic.Earliest(_detector.NextDueAt, _arbiter.NextDueAt, GateDue(), RequeryDue());
        if (due is { } d && now >= d)
        {
            if (_detector.NextDueAt is { } r && now >= r && _audio is not null && now - _audio.At >= QuietReobserve)
                Observe(_audio with { At = now });      // the monitor is quiet: re-observe the last (silent) snapshot
            Decide("tick");
        }
        RequestTabsIfNeeded();

        foreach (var pid in _exited.Where(kv => now - kv.Value > ExitedMemory).Select(kv => kv.Key).ToList())
            _exited.Remove(pid);

        if (_stateSaveDue is { } sd && now >= sd)
        {
            _quietEvent = false;
            _stateSaveDue = null;
            SaveStateNow();
        }

        if (_resumeDue is { } rd && now >= rd)
        {
            _quietEvent = false;
            _resumeDue = null;
            Ensure("resume");
        }
        if (now >= _nextPeriodic)
        {
            _quietEvent = false;
            _nextPeriodic = now + PeriodicInterval;
            Ensure("periodic");
        }
    }

    private void AfterEvent()
    {
        PersistUserMode();
        if (!_quietEvent)
        {
            // Lifetime of the processes that matter: the foreground root and the winner.
            var pids = new HashSet<int>();
            if (_fgRoot is int f) pids.Add(f);
            if (_decision?.Winner?.RootPid is int w) pids.Add(w);
            _lifetime.Track(pids);
            FlushConfigChanged();
            Publish();
        }
        Reschedule();
    }

    /// <summary>Arms the one-shot timer for the next pending instant (CPU: no fixed 250 ms tick).</summary>
    private void Reschedule()
    {
        var timer = _tickTimer;
        if (timer is null || _shuttingDown) return;
        var now = _clock.Now;
        var wake = CoordinatorLogic.NextWake(now, MinWake, MaxWake,
            _nextFxCheck,
            _graceUntil,
            _mode == Mode.Paused ? _pausedUntil : null,
            _mode == Mode.Editing && _editSince is { } since && !_once.Seen("edit-cap:" + _editingPreset) ? since + EditCap : null,
            _detector.NextDueAt, _arbiter.NextDueAt, GateDue(), RequeryDue(),
            NextTabRequestAt(),
            _exited.Count > 0 ? _exited.Values.Min() + ExitedMemory : null,
            _stateSaveDue, _resumeDue, _nextPeriodic);
        if (wake == _scheduledWake) return;
        _scheduledWake = wake;
        long ms = wake is { } w ? Math.Max(1, (long)Math.Ceiling((w - now).TotalMilliseconds)) : Timeout.Infinite;
        try { timer.Change(ms, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Retry/in-flight expiry of the apply gate, only while a command could be sent (otherwise the event that
    /// makes it possible again — FxSound opened or started, preset operation done — runs a decision itself).</summary>
    private DateTimeOffset? GateDue()
        => _fxRunning && !_startingFx && !_opRunning ? _gate.NextDueAt : null;

    /// <summary>While something plays and the effective output is uncertain, status.json is re-read every
    /// <see cref="RequeryMinInterval"/> (the old 4 Hz decisions did it on every audio sample).</summary>
    private DateTimeOffset? RequeryDue()
        => _output is { RequeryStatus: true } && _fxRunning && _playing.Values.Any(p => p)
            ? _lastRequery + RequeryMinInterval
            : null;

    // ───────────── decision cycle ─────────────

    /// <summary>Classification of a root, memoised until one of its inputs changes (CPU: the detector asks for every
    /// root on every audio sample). Cleared on new roots, focus/title, tab reads, playing changes, exits and config.</summary>
    private SourceClass Classify(int pid)
    {
        if (_classMemo.TryGetValue(pid, out var memo)) return memo;
        var c = ClassifyNow(pid);
        _classMemo[pid] = c;
        return c;
    }

    private SourceClass ClassifyNow(int pid)
    {
        var (exe, path, aumid) = _roots.TryGetValue(pid, out var r) ? r : (null, null, null);
        if (_classifier.IsBrowser(exe)) return _tracker.Current(pid, exe!);
        if (pid == _fgRoot && _fg is not null)
            return _classifier.Classify(new SourceContext(pid, exe, path, aumid, _fg.Title, null));
        if (_classCache.TryGetValue(pid, out var c)) return c;
        c = _classifier.Classify(new SourceContext(pid, exe, path, aumid, null, null));
        if (exe is not null) _classCache[pid] = c;
        return c;
    }

    private DateTimeOffset? StartOf(int pid)
    {
        if (_starts.TryGetValue(pid, out var s)) return s;
        s = WindowIdentifier.ReadProcess(pid).Start;
        _starts[pid] = s;
        return s;
    }

    private string? ExeOf(int pid) => _roots.TryGetValue(pid, out var r) ? r.Exe : null;

    private void Decide(string origin)
    {
        if (_shuttingDown) return;
        _quietEvent = false;
        var cands = CoordinatorLogic.BuildCandidates(_states, _fgRoot, Classify, StartOf, _exited.Keys.ToHashSet());
        _decision = _arbiter.Decide(cands, _fg, _mode);
        _output = OutputResolver.Resolve(_status, _fxRunning, _endpoints, _renderEndpointId);
        if (_output.RequeryStatus && _fxRunning && _clock.Now - _lastRequery >= RequeryMinInterval)
        {
            _lastRequery = _clock.Now;
            RequestQuery("output check");
        }
        MatchDevice();

        string why;
        Msg whyText;
        PresetTarget? target = _gate.Expected;      // default: keep what is expected now
        switch (_mode)
        {
            case Mode.Paused:
                target = null;
                why = _pausedUntil is { } until
                    ? "paused until " + until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                    : "paused until FxAuto restarts";
                whyText = _pausedUntil is { } until2
                    ? new Msg("Why.PausedUntil", until2.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture))
                    : new Msg("Why.PausedUntilRestart");
                break;
            case Mode.Editing:
                target = null;
                why = $"editing \"{_editingPreset}\" in FxSound: no changes";
                whyText = new Msg("Why.EditingNamed", _editingPreset);
                break;
            case Mode.Locked when _lockedPreset is not null:
                target = new PresetTarget(_lockedPreset, Leveling(_lockedPreset), "locked by the user");
                why = $"locked on \"{_lockedPreset}\"";
                whyText = new Msg("Why.LockedOn", _lockedPreset);
                break;
            default:
                if (!_fxRunning)
                {
                    why = _startingFx ? "waiting for FxSound to start"
                        : "FxSound is not running — turn on 'Launch on startup' in FxSound";
                    whyText = new Msg(_startingFx ? "Why.WaitingFx" : "Why.FxNotRunning");
                    break;
                }
                if (!_output.ThroughFxSound)
                {
                    // REQ-105: FxSound bypassed or off: no presets are sent at all.
                    why = _output.Why + ": no preset is sent";
                    whyText = new Msg("Why.NoPresetSent", OutputText());
                    target = null;
                    if (_status is not null && _once.First("bypass"))
                        Raise(Note(new Msg("Note.Bypass.Title"), new Msg("Note.Bypass.Text", OutputText()), false));
                    break;
                }
                if (_match is null || _match.DeviceId is null)
                {
                    why = _match?.CallMode == true ? "hands-free (call) mode: no change"
                        : _matchedEndpoint is null ? _output.Why + ": real output unknown, no change"
                        : $"unknown output device \"{_matchedEndpoint.FriendlyName}\": no change";
                    whyText = _match?.CallMode == true ? new Msg("Why.CallMode")
                        : _matchedEndpoint is null ? new Msg("Why.RealOutputUnknown", OutputText())
                        : new Msg("Why.UnknownDevice", _matchedEndpoint.FriendlyName);
                    break;
                }
                if (_match.CallMode)
                {
                    why = "hands-free (call) mode: no change";
                    whyText = new Msg("Why.CallMode");
                    break;
                }
                if (_decision.Level == WinLevel.Hold)
                {
                    why = _decision.Reason;
                    whyText = DecisionText();
                    break;
                }
                var res = PresetResolver.Resolve(_cfg, _match.DeviceId, _decision.Winner?.Class, _status);
                why = _decision.Reason + " → " + res.Why;
                whyText = new Msg("Why.Chain", DecisionText(), res.WhyText ?? new Msg("Why.Raw", res.Why));
                if (res.Preset is not null) target = new PresetTarget(res.Preset, res.Leveling, res.Why);
                break;
        }
        _why = why;
        _whyText = whyText;
        _gate.SetExpected(target);
        BuildViews(cands);
        LogDecision(origin, target);
        Emit();
    }

    private void MatchDevice()
    {
        var ep = _output?.Endpoint;
        _matchedEndpoint = ep;
        if (ep is null)
        {
            _match = null;
            return;
        }
        _match = DeviceMatcher.Match(ep, _cfg.Devices, _learned, _endpoints.Endpoints);
        if (_match.DeviceId is { } id && _match.LearnedEndpointId is { } learnId
            && !(_learned.EndpointIdsByDevice.TryGetValue(id, out var ids) && ids.Contains(learnId, StringComparer.OrdinalIgnoreCase)))
        {
            _learned = LearnedStateJson.Learn(_learned, id, learnId);
            _stateSaveDue = _clock.Now + StateSaveDelay;
            _log.Log("device.learned", new { device = id, endpoint = learnId, name = ep.FriendlyName });
        }
        if (_match.Weak && _once.First("weak:" + ep.EndpointId))
            _log.Log("device.weak_match", new { device = _match.DeviceId, name = ep.FriendlyName, how = _match.How });
        if (_match.DeviceId is null && !_match.CallMode && _match.How == DeviceMatcher.HowUnknown)
        {
            var key = CoordinatorLogic.UnknownDeviceKey(ep.Adapter, ep.FriendlyName, ep.FormFactor);
            if (_once.First("unknown:" + key))
            {
                _log.Log("device.unknown", new { name = ep.FriendlyName, adapter = ep.Adapter, formFactor = ep.FormFactor.ToString(),
                                                 suggestedTag = _match.SuggestedTag });
                Raise(Note(new Msg("Note.C06.Title"),
                    new Msg("Note.C06.Text", ep.FriendlyName),
                    false, SettingsPage.Devices, ep.EndpointId));
            }
        }
    }

    private double Leveling(string preset) => _cfg.Presets.TryGetValue(preset, out var m) ? m.VolumeLeveling : 0;

    private void LogDecision(string origin, PresetTarget? target)
    {
        var w = _decision?.Winner;
        string key = $"{target?.Preset}|{target?.Leveling}|{_mode}|{_decision?.Level}|{w?.RootPid}|{w?.Class.Category}|{_match?.DeviceId}|{_why}";
        if (key == _lastDecisionKey) return;
        _lastDecisionKey = key;
        _log.Log("decision", new
        {
            preset = target?.Preset, leveling = target?.Leveling, mode = _mode.ToString(), level = _decision?.Level.ToString(),
            reason = _decision?.Reason, why = _why, device = _match?.DeviceId, pid = w?.RootPid, exe = w is null ? null : ExeOf(w.RootPid),
            category = w?.Class.Category, sourceId = w?.Class.SourceId, site = w?.Class.Site, how = w?.Class.How, origin,
        });
        string outKey = $"{_output?.ThroughFxSound}|{_output?.Endpoint?.EndpointId}|{_output?.Why}";
        if (outKey != _lastOutputKey)
        {
            _lastOutputKey = outKey;
            _log.Log("output", new
            {
                throughFxSound = _output?.ThroughFxSound, name = _output?.Endpoint?.FriendlyName, why = _output?.Why,
                device = _match?.DeviceId, how = _match?.How, callMode = _match?.CallMode,
                selectedOutput = _status?.SelectedOutput,
            });
        }
    }

    private void BuildViews(IReadOnlyList<CandidateSource> cands)
    {
        var winner = _decision?.Winner?.RootPid;
        _views = cands.Select(c => new PlayingSourceView(c.RootPid, ExeOf(c.RootPid), c.Class.Category, c.Class.SourceId,
            c.Class.Site, c.Class.How, c.IsForeground, c.Playing, winner == c.RootPid, c.AudibleSince)).ToList();
        _detected = cands.Where(c => c.Playing || c.IsForeground).Select(c =>
        {
            var (exe, path, aumid) = _roots.TryGetValue(c.RootPid, out var r) ? r : (null, null, null);
            string? title = c.RootPid == _fgRoot ? _fg?.Title : _browserTitles.GetValueOrDefault(c.RootPid);
            return new DetectedSource(c.RootPid, exe, path, aumid, title, c.Class);
        }).ToList();
    }

    // ───────────── commands to FxSound (without await) ─────────────

    private void Emit()
    {
        if (_shuttingDown || _startingFx || _opRunning || !_fxRunning) return;
        var t = _gate.TryEmit();
        if (t is null) return;
        _log.Log("apply", new { preset = t.Preset, leveling = t.Leveling, why = t.Why });
        string preset = t.Preset;
        _controller.ApplyAsync(t.Preset, t.Leveling, _stopping.Token).ContinueWith(
            task => Post(Completed(KindApply, preset, task)), TaskScheduler.Default);
    }

    private void Ensure(string origin)
    {
        if (_shuttingDown || _startingFx || _opRunning || !_fxRunning || _controller.IsBusy) return;
        if (_mode is Mode.Paused or Mode.Editing) return;
        var exp = _gate.Expected;
        if (exp is null) return;
        _log.Log("watchdog", new { origin, expected = exp.Preset });
        string preset = exp.Preset;
        _controller.EnsureAsync(exp.Preset, exp.Leveling, _stopping.Token).ContinueWith(
            t => Post(Completed(KindWatchdog, preset, t)), TaskScheduler.Default);
    }

    private const string KindApply = "apply";
    private const string KindWatchdog = "watchdog";
    private const string KindQuery = "query";
    private const string KindStart = "start";

    private static ApplyCompletedEvent Completed(string kind, string preset, Task<ApplyResult> t)
        => t.IsCompletedSuccessfully
            ? new ApplyCompletedEvent(DateTimeOffset.Now, kind, preset, t.Result, null, null, null)
            : new ApplyCompletedEvent(DateTimeOffset.Now, kind, preset, null, null, null,
                                      t.IsCanceled ? "canceled" : t.Exception?.GetBaseException().Message);

    /// <summary>One --status at a time; requests while one runs are coalesced into one more.</summary>
    private void RequestQuery(string origin, TaskCompletionSource<object?>? reply = null)
    {
        if (reply is not null) _queryReplies.Add(reply);
        if (_shuttingDown) { CompleteQueryReplies(null); return; }
        if (_queryInFlight)
        {
            _queryAgain = true;
            return;
        }
        _queryInFlight = true;
        _controller.QueryAsync(_stopping.Token).ContinueWith(t => Post(new ApplyCompletedEvent(
            DateTimeOffset.Now, KindQuery + ":" + origin, null, null,
            t.IsCompletedSuccessfully ? t.Result : null, null,
            t.IsCompletedSuccessfully ? null : t.Exception?.GetBaseException().Message ?? "canceled")),
            TaskScheduler.Default);
    }

    private void CompleteQueryReplies(FxStatus? st)
    {
        foreach (var r in _queryReplies) r.TrySetResult(st);
        _queryReplies.Clear();
    }

    private void StartFxSound(string origin)
    {
        if (_startingFx) return;
        _startingFx = true;
        _log.Log("fxsound.launch", new { origin });
        _port.StartMainMinimizedAsync(_stopping.Token).ContinueWith(t => Post(new ApplyCompletedEvent(
            DateTimeOffset.Now, KindStart, null, null, null,
            t.IsCompletedSuccessfully && t.Result,
            t.IsCompletedSuccessfully ? null : t.Exception?.GetBaseException().Message ?? "canceled")),
            TaskScheduler.Default);
    }

    private void OnCompleted(ApplyCompletedEvent a)
    {
        if (a.Kind == KindStart)
        {
            _startingFx = false;
            _fxRunning = _port.IsMainRunning();
            _fxElevated = _fxRunning ? _port.IsElevated() : null;
            _log.Log("fxsound.launch.done", new { ok = a.Started, open = _fxRunning, error = a.Error });
            _gate.ResetApplied();
            if (_fxRunning)
            {
                _launchedByUs = true;
                _hangWarned = false;
                RequestQuery("startup");
            }
            else
            {
                Raise(new Notification("FxSound did not start", a.Error ?? "It did not answer within 10 s.", true) { TitleText = new Msg("Note.C07.Title"), BodyText = a.Error is null ? new Msg("Note.C07.Text") : null });
            }
            Decide("fxsound started");
            return;
        }

        if (a.Kind.StartsWith(KindQuery, StringComparison.Ordinal))
        {
            _queryInFlight = false;
            _log.Log("fxsound.query", new { origin = a.Kind, ok = a.Status is not null, preset = a.Status?.SelectedPreset,
                                            power = a.Status?.Power, output = a.Status?.SelectedOutput, error = a.Error });
            CompleteQueryReplies(a.Status);
            if (a.Status is not null)
            {
                _status = a.Status;
                _statusForUi = a.Status;
                FxResponded();
                if (!_shuttingDown && !_opRunning && _gate.ObserveStatus(a.Status))
                    _log.Log("fxsound.already_applied", new { preset = a.Status.SelectedPreset, origin = a.Kind });
                ValidatePresets();
                ConfirmRestoredLock();
            }
            else if (_launchedByUs && _port.IsMainRunning() && _port.IsElevated() != true)
            {
                WarnHung();
            }
            if (_queryAgain)
            {
                _queryAgain = false;
                RequestQuery("coalesced");
            }
            Decide("status");
            return;
        }

        // apply / watchdog
        if (a.Result is null)
        {
            _log.Log("command.error", new { kind = a.Kind, preset = a.Preset, error = a.Error });
            if (a.Preset is not null && !_shuttingDown) _gate.MarkFailed(a.Preset, a.Error ?? "error");
            return;
        }
        var r = a.Result;
        _log.Log("verification", new { kind = a.Kind, ok = r.Ok, expected = r.Expected, actual = r.Actual,
                                       reason = r.FailureReason, attempts = r.Attempts, ms = r.Ms });
        if (r.Ok)
        {
            FxResponded();
            _gate.MarkApplied(r.Expected);
            return;
        }
        string reason = r.FailureReason ?? "unknown";
        if (reason.StartsWith("superseded", StringComparison.OrdinalIgnoreCase)) return;
        if (FxSoundController.IsUnresponsive(reason) && !_shuttingDown)
        {
            var wait = _hang.OnFailure();
            _nextPeriodic = _clock.Now + wait;
            _gate.MarkFailed(r.Expected, reason, wait + HangRetryMargin);
            _log.Log("fxsound.not_responding", new { failures = _hang.Failures, wait_s = (int)wait.TotalSeconds, reason });
            if (_launchedByUs) WarnHung();
            return;
        }
        _gate.MarkFailed(r.Expected, reason);
        if (reason.Contains("closed", StringComparison.OrdinalIgnoreCase)) _fxRunning = _port.IsMainRunning();
        if (reason.Contains("elevated", StringComparison.OrdinalIgnoreCase))
        {
            _fxElevated = true;
            if (_once.First("elevated"))
                Raise(Note(new Msg("Note.C08.Title"),
                    new Msg("Note.C08.Text"), true));
        }
    }

    private void FxResponded()
    {
        if (_hang.Failures > 0) _log.Log("fxsound.responding", new { after_failures = _hang.Failures });
        _hang.OnSuccess();
        _launchedByUs = false;
        _hangWarned = false;
    }

    private void WarnHung()
    {
        if (_hangWarned) return;
        _hangWarned = true;
        _log.Log("fxsound.unresponsive.warning", new { });
        Raise(Note(new Msg("Note.C09.Title"), new Msg("Note.C09.Text"), true));
    }

    /// <summary>Config re-validated against FxSound's lists; one notification per set of missing presets.</summary>
    private void ValidatePresets()
    {
        if (_status is null) return;
        _configIssues = ConfigValidator.Validate(_cfg, _status);
        var available = new HashSet<string>(_status.UserPresets.Concat(_status.BuiltInPresets), StringComparer.Ordinal);
        var missing = ConfigEdits.ReferencedPresets(_cfg).Where(p => !available.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        string key = string.Join("|", missing);
        if (missing.Count > 0 && key != _missingKey)
        {
            _log.Log("presets.missing", new { presets = missing });
            Raise(new Notification("Presets FxSound does not have", string.Join(", ", missing.Take(6)), true, SettingsPage.Presets) { TitleText = new Msg("Note.C10.Title") });
        }
        _missingKey = key;
    }

    // ───────────── config ─────────────

    private void OnConfigApplied(ConfigAppliedEvent e)
    {
        if (e.BaseVersion != _version)
        {
            e.Reply.TrySetResult(new ApplyConfigResult(false,
                [ConfigIssue.Of(IssueLevel.Error, "stale", "Issue.Coord.01")], _versioned));
            return;
        }
        e.Reply.TrySetResult(ApplyConfig(e.Next, e.ConfirmReplace, "settings"));
        Decide("config applied");
    }

    /// <summary>Validate → save → swap. The result is completed by the caller BEFORE ConfigChanged is raised (so the
    /// window gets its answer first and does not mistake its own apply for an outside change).</summary>
    private ApplyConfigResult ApplyConfig(FxAutoConfig next, bool confirmReplace, string origin)
    {
        var issues = ConfigValidator.Validate(next, _status);
        if (issues.Any(i => i.Level == IssueLevel.Error))
            return new ApplyConfigResult(false, issues, _versioned);
        // Read-only (newer FxAuto) never saves; protected (unusable config.json on disk) only after the user confirms.
        var blocked = _store.ReadOnlyReason ?? (confirmReplace ? null : _store.ProtectedReason);
        if (blocked is not null)
            return new ApplyConfigResult(false, [.. issues, new(IssueLevel.Error, ConfigStore.ConfigFileName, blocked)
                { Text = _store.ReadOnlyReason is not null ? _store.ReadOnlyReasonText : _store.ProtectedReasonText }], _versioned);
        var error = SwapConfig(next, save: true, origin, confirmReplace);
        if (error is not null)
            return new ApplyConfigResult(false, [.. issues, new(IssueLevel.Error, ConfigStore.ConfigFileName, error)], _versioned);
        _fileIssues = [];
        _pendingConfigChanged = true;
        return new ApplyConfigResult(true, issues, _versioned);
    }

    /// <summary>Raises ConfigChanged if a swap is pending (after the caller answered its request).</summary>
    private void FlushConfigChanged()
    {
        if (!_pendingConfigChanged) return;
        _pendingConfigChanged = false;
        ConfigChanged?.Invoke(_versioned);
    }

    private void OnConfigFile(ConfigFileChange change)
    {
        if (change.Config is null)
        {
            _fileIssues = change.Issues.Where(i => i.Level != IssueLevel.Info).ToList();
            _log.Log("config.invalid", new { errors = _fileIssues.Take(5).Select(i => $"{i.Path}: {i.Message}").ToList() });
            if (_fileIssues.Any(i => i.Level == IssueLevel.Error))
                Raise(new Notification("config.json has errors (the previous config is still in use)",
                    string.Join("\n", _fileIssues.Where(i => i.Level == IssueLevel.Error).Take(3).Select(i => i.Shown)), true, SettingsPage.Status) { TitleText = new Msg("Note.C11.Title") });
            return;
        }
        _fileIssues = [];
        SwapConfig(change.Config, save: false, "hand edit");
        _pendingConfigChanged = true;
        Decide("config file");
    }

    /// <summary>Saves (optionally) and swaps the live config; bumps the version. The caller raises ConfigChanged
    /// (<see cref="FlushConfigChanged"/>) after answering its request. Returns the save error, or null.</summary>
    private string? SwapConfig(FxAutoConfig next, bool save, string origin, bool confirmReplace = false)
    {
        if (save)
        {
            try { _store.Save(next, confirmReplace); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _log.Log("config.save.error", new { origin, error = ex.Message });
                return "Could not save the configuration: " + ex.Message;
            }
        }
        var old = _cfg;
        _cfg = next;
        _cfgForOps = next;
        _version++;
        _versioned = new VersionedConfig(next, _version);
        _configIssues = ConfigValidator.Validate(next, _status);
        _classCache.Clear();
        _classMemo.Clear();
        _classifier = new SourceClassifier(next);

        if (next.General.Detection.PollMs != old.General.Detection.PollMs)
            AudioPollChanged?.Invoke(next.General.Detection.PollMs);    // REQ-109: applied without a restart
        if (next.General.Detection != old.General.Detection)
        {
            _detector = new SoundingDetector(next.General.Detection, _clock);
            _playing = [];
            _states = [];
            _tracker = new BrowserTabTracker(_classifier, _clock);
        }
        else
        {
            // New tracker over the new classifier; browsers that are playing are latched again.
            _tracker = new BrowserTabTracker(_classifier, _clock);
            foreach (var (pid, playing) in _playing)
            {
                var exe = ExeOf(pid);
                if (!playing || !_classifier.IsBrowser(exe)) continue;
                _tracker.OnBrowserTitle(pid, exe!, pid == _fgRoot ? _fg?.Title : _browserTitles.GetValueOrDefault(pid));
                _tracker.OnPlayingChanged(pid, true);
            }
        }
        var timings = SourceArbiter.TimingsFrom(next.General);
        if (timings != _arbiterTimings)
        {
            _arbiterTimings = timings;
            _arbiter = new SourceArbiter(timings, _clock);
        }
        UpdateTabService();
        _log.Log("config.reloaded", new { origin, version = _version, sources = next.Sources.Count, devices = next.Devices.Count });
        return null;
    }

    private void UpdateTabService()
    {
        bool want = _cfg.General.ReadBrowserTabs;
        if (!want && _tabs is not null)
        {
            _tabs.Dispose();
            _tabs = null;
            _tabsDisabled = false;
            _log.Log("tabs.service", new { enabled = false });
        }
        else if (want && _tabs is null)
        {
            try
            {
                _tabs = new BrowserTabService(_cfg.General.AudibleTabMarkers, Post);
                _tabs.Disabled += () => Post(new TabReadingDisabledEvent(DateTimeOffset.Now));
                _tabsDisabled = false;
                _log.Log("tabs.service", new { enabled = true });
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                _log.Log("tabs.service.error", new { error = ex.Message });
            }
        }
        else if (want && _tabs is not null)
        {
            _tabs.AudibleMarkers = _cfg.General.AudibleTabMarkers;
            if (_tabsDisabled)
            {
                // Applying the settings again re-enables tab reading after it was disabled by timeouts.
                _tabs.Reenable();
                _tabsDisabled = false;
                _log.Log("tabs.reenabled", new { });
            }
        }
    }

    /// <summary>Reads the tabs of the playing browsers that need it (BrowserTabTracker.NextTabReadAt: start of
    /// playback, focus/title changes, failed reads and a slow safety re-read; no fixed 5 s polling).</summary>
    private void RequestTabsIfNeeded()
    {
        if (_tabs is null || _tabsDisabled) return;
        var now = _clock.Now;
        foreach (var (pid, playing) in _playing)
        {
            if (!playing || !_classifier.IsBrowser(ExeOf(pid))) continue;
            if (TabRequestDue(pid) is not { } due || due > now) continue;
            _tabRequestedAt[pid] = now;
            _tracker.OnTabsRequested(pid, _tabs.Request(pid));
        }
    }

    private DateTimeOffset? TabRequestDue(int pid)
        => CoordinatorLogic.TabRequestDue(_tracker.NextTabReadAt(pid),
            _tabRequestedAt.TryGetValue(pid, out var at) ? at : null, TabRequestMinInterval);

    private DateTimeOffset? NextTabRequestAt()
    {
        if (_tabs is null || _tabsDisabled) return null;
        DateTimeOffset? next = null;
        foreach (var (pid, playing) in _playing)
        {
            if (!playing || !_classifier.IsBrowser(ExeOf(pid))) continue;
            next = CoordinatorLogic.Earliest(next, TabRequestDue(pid));
        }
        return next;
    }

    private static string? FirstWindowTitle(int rootPid)
    {
        try
        {
            var windows = UiaBrowserTabReader.BrowserWindows.Find(rootPid);
            return windows.Count > 0 ? windows[0].Title : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private void SaveStateNow()
    {
        var err = _store.SaveState(_learned);
        if (err != _stateSaveError)
        {
            _stateSaveError = err;
            if (err is not null) _log.Log("state.save.error", new { error = err });
        }
    }

    // ───────────── preset operations ─────────────

    private void StartPresetOp(PresetOpRequest req, CancellationToken ct, Action<PresetOpResult, VersionedConfig> done)
    {
        string? refuse = _shuttingDown ? "FxAuto is closing."
            : _opRunning ? "Another preset operation is running."
            : !_fxRunning ? "FxSound is not running."
            : _fxElevated == true ? "FxSound runs as administrator; FxAuto sends it no commands."
            : PresetOperations.ChangesConfig(req) && _store.SaveBlockedReason is { } blocked
                ? "The configuration can't be saved right now, so presets can't be changed. " + blocked
            : null;
        if (refuse is not null)
        {
            done(new PresetOpResult(false, refuse, [], null, []), _versioned);
            return;
        }
        _opRunning = true;
        _opDone = done;
        _log.Log("preset-op.start", new { kind = req.Kind.ToString(), source = req.Source, target = req.Target, output = req.OutputName });
        Publish();
        _ops.RunAsync(req, ct).ContinueWith(t => Post(new PresetOpCompletedEvent(DateTimeOffset.Now, req,
            t.IsCompletedSuccessfully ? t.Result
                : new PresetOpResult(false, t.IsCanceled ? "canceled" : t.Exception?.GetBaseException().Message ?? "error", [], null, [])
                  { ReapplyRequested = true })), TaskScheduler.Default);
    }

    private void OnPresetOpCompleted(PresetOpCompletedEvent e)
    {
        _opRunning = false;
        var req = e.Request;
        var res = e.Result;
        if (res.Final is not null)
        {
            _status = res.Final;
            _statusForUi = res.Final;
        }

        // A rename or delete that failed half-way still did its first steps in FxSound: keep the config in step.
        var forConfig = !res.Ok && res.Done.Count > 0 && req.Kind is PresetOpKind.Rename or PresetOpKind.Delete
            ? res with { Ok = true } : res;
        var next = PresetOperations.ApplyToConfig(_cfg, req, forConfig);
        if (!ReferenceEquals(next, _cfg))
        {
            var err = SwapConfig(next, save: true, "preset op " + req.Kind);
            if (err is null) _pendingConfigChanged = true;
            if (err is not null) Raise(new Notification("Preset operation", err, true, SettingsPage.Presets) { TitleText = new Msg("Note.C12.Title") });
        }
        if (forConfig.Ok) FollowPresetChanges(req.Kind, forConfig.Done);

        switch (req.Kind)
        {
            case PresetOpKind.EditBegin when res.Ok:
                if (_mode != Mode.Editing) _modeBeforeEdit = _mode;
                _mode = Mode.Editing;
                _editingPreset = req.Source;
                _editSince = _clock.Now;
                _once.Forget("edit-cap:" + req.Source);
                _learned = _learned with { EditPendingPreset = req.Source };
                SaveStateNow();
                _log.Log("mode", new { mode = "Editing", preset = req.Source });
                break;
            case PresetOpKind.EditFinish when res.Ok:
                _mode = _modeBeforeEdit == Mode.Editing ? Mode.Auto : _modeBeforeEdit;
                _editingPreset = null;
                _editSince = null;
                _learned = _learned with { EditPendingPreset = null };
                SaveStateNow();
                _log.Log("mode", new { mode = _mode.ToString(), origin = "edit finished", outcome = req.Outcome?.ToString() });
                break;
        }

        if (res.ReapplyRequested || req.Kind is PresetOpKind.EditFinish or PresetOpKind.SwitchOutput) _gate.ResetApplied();
        else if (res.Final is not null) _gate.ObserveStatus(res.Final);
        if (res.Final is not null) ValidatePresets();

        var done = _opDone;
        _opDone = null;
        done?.Invoke(res, _versioned);
        FlushConfigChanged();          // after the answer: the window rebases on its own result first
        Decide("preset op");
    }

    /// <summary>Carries every preset name the coordinator holds (lock, gate, edit) through renames done in FxSound;
    /// a deleted locked preset ends the lock (back to Auto, with a notice).</summary>
    private void FollowPresetChanges(PresetOpKind kind, IReadOnlyList<(string From, string To)> done)
    {
        if (done.Count == 0) return;
        var cur = new ModeNames(_mode, _modeBeforeEdit, _lockedPreset, _editingPreset, _learned.EditPendingPreset);
        var (next, lockEnded) = CoordinatorLogic.FollowPresetOp(cur, kind, done);
        if (kind is PresetOpKind.Rename)
            foreach (var (from, to) in done)
                if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to)) _gate.RenamePreset(from, to);
        if (next == cur) return;
        _mode = next.Mode;
        _modeBeforeEdit = next.ModeBeforeEdit;
        _lockedPreset = next.Locked;
        _editingPreset = next.Editing;
        if (next.EditPending != cur.EditPending)
        {
            _learned = _learned with { EditPendingPreset = next.EditPending };
            SaveStateNow();
        }
        if (lockEnded is not null)
        {
            _log.Log("mode", new { mode = _mode.ToString(), origin = "locked preset deleted", preset = lockEnded });
            Raise(Note(new Msg("Note.C13.Title"), new Msg("Note.C13.Text", lockEnded), false));
        }
        else if (next.Locked != cur.Locked)
            _log.Log("mode", new { mode = _mode.ToString(), preset = next.Locked, origin = "preset renamed", from = cur.Locked });
    }

    // ───────────── UI requests ─────────────

    private void OnSettingsRequest(SettingsRequestEvent s)
    {
        switch (s.Kind)
        {
            case SettingsRequestKind.RefreshFxStatus:
                if (!_fxRunning) s.Reply?.TrySetResult(null);
                else RequestQuery("settings", s.Reply);
                break;
            case SettingsRequestKind.DetectPlayingNow:
                s.Reply?.TrySetResult(_detected);
                break;
            case SettingsRequestKind.SetMode:
                SetMode(s.Mode ?? Mode.Auto, s.Preset, s.PauseFor, "settings");
                s.Reply?.TrySetResult(null);
                break;
            case SettingsRequestKind.BeginEdit:
                if (_mode == Mode.Editing)
                {
                    s.Reply?.TrySetResult(new PresetOpResult(false, $"\"{_editingPreset}\" is already being edited.", [], null, []));
                    break;
                }
                StartPresetOp(new PresetOpRequest(PresetOpKind.EditBegin, Source: s.Preset), CancellationToken.None,
                    (r, _) => s.Reply?.TrySetResult(r));
                break;
            case SettingsRequestKind.FinishEdit:
                if (_mode != Mode.Editing || _editingPreset is null)
                {
                    s.Reply?.TrySetResult((new PresetOpResult(false, "No preset is being edited.", [], null, []), _versioned));
                    break;
                }
                StartPresetOp(new PresetOpRequest(PresetOpKind.EditFinish, Source: _editingPreset, Target: s.Preset, Outcome: s.Outcome),
                    CancellationToken.None, (r, v) => s.Reply?.TrySetResult((r, v)));
                break;
        }
    }

    private void SetMode(Mode mode, string? preset, TimeSpan? pauseFor, string origin)
    {
        if (_mode == Mode.Editing)
        {
            _log.Log("mode.refused", new { mode = mode.ToString(), reason = "editing a preset", origin });
            return;
        }
        _restoredLockUnconfirmed = false;      // the user chose a mode: nothing restored left to confirm
        switch (mode)
        {
            case Mode.Auto:
                _mode = Mode.Auto;
                _lockedPreset = null;
                _pausedUntil = null;
                break;
            case Mode.Locked:
                var p = preset ?? _gate.Verified ?? _gate.Expected?.Preset ?? _status?.SelectedPreset;
                if (p is null)
                {
                    _log.Log("mode.refused", new { mode = "Locked", reason = "no current preset", origin });
                    return;
                }
                _lockedPreset = p;
                _pausedUntil = null;
                _mode = Mode.Locked;
                break;
            case Mode.Paused:
                _mode = Mode.Paused;
                _pausedUntil = pauseFor is { } d ? _clock.Now + d : null;
                break;
            default:
                return;            // Editing only through BeginEdit
        }
        _log.Log("mode", new { mode = _mode.ToString(), preset = _lockedPreset, until = _pausedUntil, origin });
        Decide("mode");
    }

    private void OnMenu(MenuCommandEvent m)
    {
        _log.Log("menu", new { command = m.Command.ToString(), arg = m.Arg, pauseMinutes = m.PauseFor?.TotalMinutes });
        switch (m.Command)
        {
            case MenuCommand.Auto: SetMode(Mode.Auto, null, null, "menu"); break;
            case MenuCommand.LockCurrent: SetMode(Mode.Locked, null, null, "menu"); break;
            case MenuCommand.LockPreset when m.Arg is not null: SetMode(Mode.Locked, m.Arg, null, "menu"); break;
            case MenuCommand.Pause: SetMode(Mode.Paused, null, m.PauseFor, "menu"); break;
            case MenuCommand.StartFxSound:
                if (!_port.IsMainRunning()) StartFxSound("menu");
                break;
            case MenuCommand.SwitchOutput when m.Arg is not null:
                StartPresetOp(new PresetOpRequest(PresetOpKind.SwitchOutput, OutputName: m.Arg), CancellationToken.None, (r, _) =>
                {
                    if (!r.Ok) Raise(new Notification("Could not switch the output", r.Error ?? "unknown error", true)
                        { TitleText = new Msg("Note.C14.Title"), BodyText = r.ErrorText ?? (r.Error is null ? new Msg("Note.C14.Text") : null) });
                });
                break;
            case MenuCommand.ReenableTabReading:
                if (_tabs is not null && _tabsDisabled)
                {
                    _tabs.Reenable();
                    _tabsDisabled = false;
                    _log.Log("tabs.reenabled", new { });
                }
                break;
        }
    }

    private void OnShutdown(string why)
    {
        _shuttingDown = true;
        _log.Log("exit", new { reason = why });
        _tickTimer?.Dispose();
        var until = DateTimeOffset.Now + ShutdownWait;
        while (_controller.IsBusy && DateTimeOffset.Now < until) Thread.Sleep(100);
        if (_controller.IsBusy) _log.Log("exit.command_in_progress", new { });
        _stopping.Cancel();
        if (_stateSaveDue is not null) SaveStateNow();
        CompleteQueryReplies(null);
        _opDone?.Invoke(new PresetOpResult(false, "FxAuto is closing.", [], null, []), _versioned);
        _opDone = null;
        _tabs?.Dispose();
        _tabs = null;
        _lifetime.Dispose();
        Stopped?.Invoke();
    }

    // ───────────── publishing ─────────────

    /// <summary>UI text of the effective output's reason (falls back to the English one).</summary>
    private Msg OutputText() => _output?.WhyText ?? new Msg("Why.Raw", _output?.Why ?? "");

    /// <summary>UI text of the arbiter's reason (falls back to the English one).</summary>
    private Msg DecisionText() => _decision?.ReasonText ?? new Msg("Why.Raw", _decision?.Reason ?? "");

    /// <summary>Notification whose English text (log) and UI text come from the catalog.</summary>
    private static Notification Note(Msg title, Msg text, bool error, SettingsPage? page = null, string? focusId = null)
        => new(Loc.RenderEnglish(title), Loc.RenderEnglish(text), error, page, focusId) { TitleText = title, BodyText = text };

    private void Raise(Notification n)
    {
        _log.Log("notify", new { title = n.Title, error = n.Error, page = n.Page?.ToString() });
        Notify?.Invoke(n);
    }

    private IReadOnlyList<ConfigIssue> RuntimeIssues()
    {
        var list = new List<ConfigIssue>(_fileIssues);
        list.AddRange(_configIssues);
        list.AddRange(_stateIssues);
        if (!_fxRunning && !_startingFx)
            list.Add(_cfg.General.StartFxSoundIfClosed ? ConfigIssue.Of(IssueLevel.Warning, "fxsound", "Issue.Coord.FxNotRunning")
                : ConfigIssue.Of(IssueLevel.Warning, "fxsound", "Issue.Coord.FxNotRunningHint"));
        if (_fxElevated == true)
            list.Add(ConfigIssue.Of(IssueLevel.Error, "fxsound", "Issue.Coord.02"));
        if (_store.ReadOnlyReason is { } ro) list.Add(new(IssueLevel.Warning, ConfigStore.ConfigFileName, ro) { Text = _store.ReadOnlyReasonText });
        else if (_store.ProtectedReason is { } pr) list.Add(new(IssueLevel.Error, ConfigStore.ConfigFileName, pr) { Text = _store.ProtectedReasonText });
        if (_stateSaveError is { } se) list.Add(ConfigIssue.Of(IssueLevel.Warning, LearnedStateJson.FileName, "Issue.Coord.03", se));
        if (_tabsDisabled)
            list.Add(ConfigIssue.Of(IssueLevel.Warning, "general.readBrowserTabs",
                "Issue.Coord.04"));
        if (_matchedEndpoint is not null && _match is { DeviceId: null, CallMode: false } && _output?.ThroughFxSound == true)
            list.Add(ConfigIssue.Of(IssueLevel.Warning, "devices", "Issue.Coord.05", _matchedEndpoint.FriendlyName));
        return list;
    }

    private TrayState BuildTrayState()
    {
        var exp = _gate.Expected;
        var device = _match?.DeviceId is { } id ? _cfg.Devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) : null;
        var status = new StatusSnapshot(_mode, _fxRunning, _fxElevated, _output?.ThroughFxSound ?? false,
            _match?.DeviceId, device?.DisplayName, _output?.Endpoint?.FriendlyName, exp?.Preset, _gate.Verified,
            _why, _views, RuntimeIssues(), _gate.LastFailure)
        {
            WhyText = _whyText,
            ConfigSaveBlockedText = _store.SaveBlockedReasonText,
            EditingPreset = _mode == Mode.Editing ? _editingPreset ?? _learned.EditPendingPreset : null,
            ConfigSaveBlocked = _store.SaveBlockedReason,
            ConfigSaveReplaceable = _store.ReadOnlyReason is null && _store.ProtectedReason is not null,
        };
        return new TrayState(status, _status, _startingFx || _graceUntil is not null, _opRunning, _pausedUntil);
    }

    private void Publish()
    {
        var next = BuildTrayState();
        var last = _published;
        if (last is not null && Same(last, next)) return;
        _published = next;
        StatusChanged?.Invoke(next);
    }

    private static bool Same(TrayState a, TrayState b)
    {
        var x = a.Status;
        var y = b.Status;
        return ReferenceEquals(a.Fx, b.Fx) && a.StartingFxSound == b.StartingFxSound && a.PresetOpRunning == b.PresetOpRunning
            && a.PausedUntil == b.PausedUntil
            && x.Mode == y.Mode && x.FxRunning == y.FxRunning && x.FxElevated == y.FxElevated && x.ThroughFxSound == y.ThroughFxSound
            && x.DeviceId == y.DeviceId && x.DeviceName == y.DeviceName && x.OutputName == y.OutputName
            && x.ExpectedPreset == y.ExpectedPreset && x.VerifiedPreset == y.VerifiedPreset && x.Why == y.Why && Equals(x.WhyText, y.WhyText)
            && x.LastFailure == y.LastFailure && x.EditingPreset == y.EditingPreset && x.ConfigSaveBlocked == y.ConfigSaveBlocked
            && x.ConfigSaveReplaceable == y.ConfigSaveReplaceable
            && x.Playing.SequenceEqual(y.Playing) && x.Issues.SequenceEqual(y.Issues);
    }

    private void LogOutputDevices()
    {
        var e = _endpoints;
        _log.Log("devices.start", new { endpoints = e.Endpoints.Count, @default = EndpointName(e.DefaultMultimediaId) });
    }

    private string? EndpointName(string? id)
        => id is null ? null : _endpoints.Endpoints.FirstOrDefault(x => string.Equals(x.EndpointId, id, StringComparison.OrdinalIgnoreCase))?.FriendlyName;

    private static string? Trim(string? s) => s is { Length: > 120 } t ? t[..120] + "…" : s;

    /// <summary>Requests exit and waits for the coordinator to finish (8 s cap).</summary>
    public void Stop(string why)
    {
        Post(new ShutdownEvent(DateTimeOffset.Now, why));
        _loop?.Join(TimeSpan.FromSeconds(8));
    }

    public void Dispose()
    {
        _tickTimer?.Dispose();
        _stopping.Dispose();
    }
}
