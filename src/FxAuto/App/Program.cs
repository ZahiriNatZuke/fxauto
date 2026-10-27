using FxAuto.Audio;
using FxAuto.Devices;
using FxAuto.Foreground;
using FxAuto.FxSound;
using FxAuto.Ui;
using Microsoft.Win32;

namespace FxAuto.App;

/// <summary>
/// FxAuto: detects which source is playing and on which device, and sets the matching FxSound preset.
/// Options: --autostart (started with Windows: grace period before FxSound may be started, only when
/// <c>general.startFxSoundIfClosed</c> is on), --exit (closes the running instance in an orderly way).
/// </summary>
internal static class Program
{
    // Kernel object names kept on purpose (they are an inter-process contract, not UI text): renaming them would
    // stop a new build from seeing an already running instance and from stopping it with --exit.
    private const string MutexName = @"Local\FxAuto-InstanciaUnica";
    private const string ExitEventName = @"Local\FxAuto-Salir";

    [STAThread]
    private static int Main(string[] args)
    {
        bool autostart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        if (args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase))) return SignalExit();

        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FxAuto");
        using var log = new JsonlLog(logDir);

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            log.Log("instance.rejected", new { pid = Environment.ProcessId });
            return 2;
        }

        try
        {
            return Run(autostart, log);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static int Run(bool autostart, JsonlLog log)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetColorMode(SystemColorMode.System);   // settings window follows Windows dark/light (task 6)
        Application.ThreadException += (_, e) => log.Log("ui.exception", new { error = e.Exception.ToString() });
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Log("exception.unhandled", new { error = e.ExceptionObject.ToString() });

        log.Log("start", new { version = typeof(Program).Assembly.GetName().Version?.ToString(), autostart,
                                pid = Environment.ProcessId });

        void Log(string ev, object data) => log.Log(ev, data);

        using var store = new ConfigStore(ConfigStore.DefaultDirectory, Log);
        ConfigLoad load;
        try
        {
            load = store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Log("config.load.error", new { error = ex.Message });
            load = new ConfigLoad(Core.Config.DefaultConfig.Create(),
                [Core.Config.ConfigIssue.Of(Core.Config.IssueLevel.Error, ConfigStore.ConfigFileName, "Issue.Program.01", ex.Message)],
                ConfigOrigin.DefaultAfterError);
        }
        log.Log("config.loaded", new { origin = load.Origin.ToString(), errors = load.Issues.Count(i => i.Level == Core.Config.IssueLevel.Error),
                                       warnings = load.Issues.Count(i => i.Level == Core.Config.IssueLevel.Warning) });
        load = ReconcileStartWithWindows(load, store, log);
        var updateStateFile = Path.Combine(ConfigStore.DefaultDirectory, Core.Updates.UpdateStateJson.FileName);
        load = MigrateUpdateSettings(load, store, updateStateFile, log, out bool updatesFromConfig);
        Ui.Settings.UiPreferences.Apply(load.Config.General);   // language and theme of tray and windows

        Coordinator? coordinatorRef = null;
        using var port = new FxSoundPort(() => coordinatorRef?.Config.Config.General.FxSoundPath ?? load.Config.General.FxSoundPath, Log);

        using var devices = new DeviceMonitor(Log);
        devices.Start();

        using var coordinator = new Coordinator(load, store, log, port, devices.Snapshot(), autostart);
        coordinatorRef = coordinator;
        using var tray = new TrayApp(coordinator, log);

        devices.Changed += snap => coordinator.Post(new EndpointSnapshotEvent(DateTimeOffset.Now, snap));
        using var audio = new AudioMonitorHost(load.Config.General.Detection.PollMs,
            snap => coordinator.Post(new AudioSnapshotEvent(DateTimeOffset.Now, snap)), Log);
        coordinator.RefreshAudio = audio.Refresh;
        // detection.pollMs applies without a restart (REQ-109): the monitor is recreated off the coordinator thread.
        coordinator.AudioPollChanged = ms => Task.Run(() => audio.SetPollMs(ms));

        coordinator.Start();
        audio.Start();
        if (load.Origin is ConfigOrigin.Backup or ConfigOrigin.DefaultAfterError)
            tray.PostBalloon(new Notification(ConfigStore.ConfigFileName + " has errors",
                (load.Origin == ConfigOrigin.Backup ? "The backup copy is used. " : "The built-in defaults are used and nothing is saved until it is fixed. ")
                + (load.Issues.FirstOrDefault()?.Message ?? ""), true, SettingsPage.Status)
            {
                TitleText = new Core.Localization.Msg("Note.P01.Title", ConfigStore.ConfigFileName),
                BodyText = new Core.Localization.Msg(load.Origin == ConfigOrigin.Backup ? "Note.P01.Backup" : "Note.P01.Defaults",
                    load.Issues.FirstOrDefault()?.Shown ?? ""),
            });

        using var watcher = new ForegroundWatcher(
            (kind, info, at) => coordinator.Post(kind == ForegroundWatcher.Kind.Foreground
                ? new ForegroundEvent(at, info)
                : new TitleChangedEvent(at, info)),
            Log);
        watcher.Start();

        // Updates from GitHub Releases: daily automatic check (general.updates in config.json; on by default), results
        // logged as update.check / update.download. The tray shows one balloon per new version and "Update to X…";
        // About checks, downloads, verifies and installs. update-state.json keeps only the run state.
        using var updates = new UpdateService(UpdateService.CurrentVersion(), updateStateFile,
            UpdateService.DefaultDownloadDirectory, Log, tray.PostExit);
        if (updatesFromConfig) updates.ApplySettings(load.Config.General.EffectiveUpdates);
        tray.AttachUpdates(updates);
        updates.Start();

        try { store.Watch(change => coordinator.Post(new ConfigFileChangedEvent(DateTimeOffset.Now, change))); }
        catch (Exception ex) when (ex is IOException or ArgumentException) { log.Log("config.watch.error", new { error = ex.Message }); }

        PowerModeChangedEventHandler onPower = (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) coordinator.Post(new ResumedEvent(DateTimeOffset.Now));
        };
        SessionEndingEventHandler onSessionEnd = (_, _) => tray.PostExit("session ending");
        SystemEvents.PowerModeChanged += onPower;
        SystemEvents.SessionEnding += onSessionEnd;

        // Orderly exit from outside: FxAuto.exe --exit.
        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => tray.PostExit("--exit"), null,
                                                              Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            Application.Run(tray);
        }
        finally
        {
            exitWait.Unregister(null);
            SystemEvents.PowerModeChanged -= onPower;
            SystemEvents.SessionEnding -= onSessionEnd;
            log.Log("end", new { });
        }
        return 0;
    }

    /// <summary>
    /// The HKCU Run value is the source of truth for "Start FxAuto with Windows" (the installer writes or removes it).
    /// When <c>general.startWithWindows</c> disagrees, the config follows the registry, so Settings shows the real
    /// state and applying other changes does not undo the installer's choice. Only for a config that can be saved.
    /// </summary>
    private static ConfigLoad ReconcileStartWithWindows(ConfigLoad load, ConfigStore store, JsonlLog log)
    {
        if (load.Origin is not (ConfigOrigin.File or ConfigOrigin.Default) || store.SaveBlockedReason is not null) return load;
        try
        {
            bool enabled = AutoStart.IsEnabled();
            if (enabled == load.Config.General.StartWithWindows) return load;
            var cfg = load.Config with { General = load.Config.General with { StartWithWindows = enabled } };
            store.Save(cfg);
            log.Log("autostart.reconciled", new { startWithWindows = enabled });
            return load with { Config = cfg };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or InvalidOperationException)
        {
            log.Log("autostart.reconcile.error", new { error = ex.Message });
            return load;
        }
    }

    /// <summary>
    /// Update settings live in config.json (<c>general.updates</c>). An older build kept them in update-state.json:
    /// when the config has no such keys, they are copied into it once (only for a config that can be saved).
    /// <paramref name="fromConfig"/> = false only when that copy could not be made, so the old values stay in force.
    /// </summary>
    private static ConfigLoad MigrateUpdateSettings(ConfigLoad load, ConfigStore store, string stateFile, JsonlLog log, out bool fromConfig)
    {
        fromConfig = true;
        var prefs = Core.Updates.UpdateNotice.SettingsToMigrate(load.Config.General.Updates, Core.Updates.UpdateStateJson.Load(stateFile));
        if (prefs is null) return load;
        if (load.Origin is not (ConfigOrigin.File or ConfigOrigin.Default) || store.SaveBlockedReason is not null)
        {
            fromConfig = false;
            return load;
        }
        try
        {
            var cfg = load.Config with { General = load.Config.General with { Updates = prefs } };
            store.Save(cfg);
            log.Log("update.settings.migrated", new { checkAutomatically = prefs.CheckAutomatically, includePrereleases = prefs.IncludePrereleases });
            return load with { Config = cfg };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Log("update.settings.migrate.error", new { error = ex.Message });
            fromConfig = false;
            return load;
        }
    }

    private static int SignalExit()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ExitEventName);
            ev.Set();
            return 0;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return 1;   // no instance is running
        }
    }
}

/// <summary>Owns the audio session monitor so it can be recreated with a new poll rate (detection.pollMs).
/// Thread-safe; <see cref="SetPollMs"/> may block up to the monitor's stop timeout, so it is called off the
/// coordinator thread.</summary>
internal sealed class AudioMonitorHost : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<Core.Audio.AudioSnapshot> _onSample;
    private readonly Action<string, object> _log;
    private AudioSessionMonitor? _current;
    private int _pollMs;
    private bool _started;
    private bool _disposed;

    public AudioMonitorHost(int pollMs, Action<Core.Audio.AudioSnapshot> onSample, Action<string, object> log)
    {
        _pollMs = pollMs;
        _onSample = onSample;
        _log = log;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
            _current = Create(_pollMs);
            _current.Start();
        }
    }

    public void Refresh()
    {
        lock (_gate) _current?.Refresh();
    }

    public void SetPollMs(int pollMs)
    {
        AudioSessionMonitor? old;
        lock (_gate)
        {
            if (_disposed || pollMs == _pollMs) return;
            _pollMs = pollMs;
            if (!_started) return;
            old = _current;
            _current = null;
        }
        old?.Dispose();                 // stop the old thread first: no duplicate snapshots
        lock (_gate)
        {
            if (_disposed || _current is not null) return;
            _current = Create(_pollMs);
            _current.Start();
        }
        _log("audio.monitor.restarted", new { pollMs });
    }

    private AudioSessionMonitor Create(int pollMs)
    {
        var m = new AudioSessionMonitor(new ProcessTree(), pollMs, log: _log);
        m.Sampled += _onSample;
        return m;
    }

    public void Dispose()
    {
        AudioSessionMonitor? m;
        lock (_gate)
        {
            _disposed = true;
            m = _current;
            _current = null;
        }
        m?.Dispose();
    }
}
