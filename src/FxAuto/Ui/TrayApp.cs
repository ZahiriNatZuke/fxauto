using System.Diagnostics;
using System.Drawing;
using FxAuto.App;
using FxAuto.Core;
using FxAuto.Ui.Settings;
using FxAuto.Core.Localization;
using FxAuto.Core.Updates;

namespace FxAuto.Ui;

/// <summary>
/// Tray icon (UI thread). Receives the coordinator state through BeginInvoke and sends it the menu commands as
/// events. Never shows windows or steals focus in normal use; the settings window opens only on request.
/// Menu (REQ-108, REQ-113): Auto / Lock on current preset / Lock on ▸ / Pause ▸ / Output ▸ / Create source from
/// what's playing / Settings… / Start FxSound (only when closed) / Open log / Exit, plus "Update to X…" while an update
/// is waiting. A new version found by the automatic check shows one balloon per version.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private enum IconState { Verified, Pending, Locked, Paused, Failed }

    private readonly Coordinator _coordinator;
    private readonly JsonlLog _log;
    private readonly Control _invoker = new();
    private readonly NotifyIcon _icon;
    private readonly Dictionary<IconState, Icon> _icons = [];
    private readonly ContextMenuStrip _menu = new();
    private readonly SettingsHost _host;
    private readonly SettingsWindowOpener _opener;
    private TrayState? _state;
    private Notification? _lastBalloon;
    private bool _exiting;
    private UpdateService? _updates;

    private readonly ToolStripMenuItem _header = new() { Enabled = false };
    private readonly ToolStripMenuItem _auto = new();
    private readonly ToolStripMenuItem _lockCurrent = new();
    private readonly ToolStripMenuItem _lockOn = new();
    private readonly ToolStripMenuItem _pause = new();
    private readonly ToolStripMenuItem _pauseHour = new();
    private readonly ToolStripMenuItem _pauseRestart = new();
    private readonly ToolStripMenuItem _output = new();
    private readonly ToolStripMenuItem _createSource = new();
    private readonly ToolStripMenuItem _settings = new();
    private readonly ToolStripMenuItem _startFx = new();
    private readonly ToolStripMenuItem _openLog = new();
    private readonly ToolStripMenuItem _exit = new();
    private readonly ToolStripMenuItem _update = new() { Visible = false };
    private readonly ToolStripSeparator _updateSeparator = new() { Visible = false };

    public TrayApp(Coordinator coordinator, JsonlLog log)
    {
        _coordinator = coordinator;
        _log = log;
        _ = _invoker.Handle;   // creates the handle on the UI thread for BeginInvoke
        _host = new SettingsHost(coordinator, log, UiPost);
        _opener = new SettingsWindowOpener(_host);

        foreach (var s in Enum.GetValues<IconState>()) _icons[s] = MakeIcon(s);

        _auto.Click += (_, _) => Send(MenuCommand.Auto);
        _lockCurrent.Click += (_, _) => Send(MenuCommand.LockCurrent);
        _pauseHour.Click += (_, _) => Send(MenuCommand.Pause, pauseFor: TimeSpan.FromHours(1));
        _pauseRestart.Click += (_, _) => Send(MenuCommand.Pause);
        _pause.DropDownItems.AddRange([_pauseHour, _pauseRestart]);
        _createSource.Click += (_, _) => OpenSettings(SettingsPage.Sources, "detect-playing");
        _settings.Click += (_, _) => OpenSettings(SettingsPage.Status, null);
        _startFx.Click += (_, _) => Send(MenuCommand.StartFxSound);
        _openLog.Click += (_, _) => OpenLog();
        _exit.Click += (_, _) => RequestExit("menu");
        _update.Click += (_, _) => OpenSettings(SettingsPage.About, null);

        _menu.Items.AddRange([
            _header, new ToolStripSeparator(),
            _update, _updateSeparator,
            _auto, _lockCurrent, _lockOn, _pause, new ToolStripSeparator(),
            _output, new ToolStripSeparator(),
            _createSource, _settings, _startFx, _openLog, new ToolStripSeparator(),
            _exit,
        ]);
        _menu.Opening += (_, _) => RefreshMenu();
        SetMenuTexts();
        // language or theme changed in Settings (or in config.json): the menu and the tooltip follow at once
        _host.ConfigChanged += c =>
        {
            UiPreferences.Apply(c.Config.General);
            _updates?.ApplySettings(c.Config.General.EffectiveUpdates);   // config.json rules the update settings
            SetMenuTexts();
            if (_state is { } st) Apply(st);
        };

        _icon = new NotifyIcon
        {
            Icon = _icons[IconState.Pending],
            Text = "FxAuto",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.BalloonTipClicked += (_, _) => OnBalloonClicked();
        _icon.DoubleClick += (_, _) => OpenSettings(SettingsPage.Status, null);

        _coordinator.StatusChanged += s => UiPost(() => Apply(s));
        _coordinator.Notify += n => UiPost(() => Balloon(n));
        _coordinator.Stopped += () => UiPost(FinishExit);
        Apply(_coordinator.Current);
    }

    private void SetMenuTexts()
    {
        _auto.Text = Loc.T("Tray.Auto");
        _lockCurrent.Text = Loc.T("Tray.LockCurrent");
        _lockOn.Text = Loc.T("Tray.LockOn");
        _pause.Text = Loc.T("Tray.Pause");
        _pauseHour.Text = Loc.T("Tray.PauseHour");
        _pauseRestart.Text = Loc.T("Tray.PauseRestart");
        _output.Text = Loc.T("Tray.Output");
        _createSource.Text = Loc.T("Tray.CreateSource");
        _settings.Text = Loc.T("Tray.Settings");
        _startFx.Text = Loc.T("Tray.StartFx");
        _openLog.Text = Loc.T("Tray.OpenLog");
        _exit.Text = Loc.T("Tray.Exit");
    }

    private void UiPost(Action a)
    {
        try
        {
            if (!_invoker.IsDisposed && _invoker.IsHandleCreated) _invoker.BeginInvoke(a);
        }
        catch (InvalidOperationException) { }
    }

    private void Send(MenuCommand cmd, string? arg = null, TimeSpan? pauseFor = null)
        => _coordinator.Post(new MenuCommandEvent(DateTimeOffset.Now, cmd, arg, pauseFor));

    private void OpenSettings(SettingsPage page, string? focusId)
    {
        if (_exiting) return;
        try { _opener.Open(page, focusId); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _log.Log("ui.settings.error", new { error = ex.Message });
        }
    }

    // ---- state ----

    private void Apply(TrayState s)
    {
        _state = s;
        _icon.Icon = _icons[StateOf(s)];
        _icon.Text = Truncate(TooltipOf(s), 127);
    }

    private static IconState StateOf(TrayState t)
    {
        var s = t.Status;
        if (!s.FxRunning && !t.StartingFxSound) return IconState.Failed;
        if (s.LastFailure is not null && s.Mode is Mode.Auto or Mode.Locked) return IconState.Failed;
        return s.Mode switch
        {
            Mode.Paused or Mode.Editing => IconState.Paused,
            Mode.Locked => IconState.Locked,
            _ => s.ExpectedPreset is not null && s.ExpectedPreset == s.VerifiedPreset ? IconState.Verified : IconState.Pending,
        };
    }

    private static string ModeText(TrayState t) => t.Status.Mode switch
    {
        Mode.Paused => t.PausedUntil is { } u ? Loc.F("Tray.Mode.PausedUntil", u.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)) : Loc.T("Tray.Mode.Paused"),
        Mode.Locked => Loc.T("Tray.Mode.Locked"),
        Mode.Editing => Loc.T("Tray.Mode.Editing"),
        _ => Loc.T("Tray.Mode.Auto"),
    };

    private static string TooltipOf(TrayState t)
    {
        var s = t.Status;
        if (!s.FxRunning)
            return t.StartingFxSound ? Loc.T("Tray.Tip.WaitingFx") : Loc.T("Tray.Tip.FxNotRunning");
        string preset = s.VerifiedPreset ?? s.ExpectedPreset ?? "?";
        string text = Loc.F("Tray.Tip.Head", ModeText(t), preset);
        if (s.DeviceName is not null) text += $"\n{s.DeviceName}";
        if (s.LastFailure is not null) text += "\n" + Loc.F("Tray.Tip.Failure", Loc.Known(s.LastFailure));
        else if (s.ExpectedPreset is not null && s.ExpectedPreset != s.VerifiedPreset) text += "\n" + Loc.F("Tray.Tip.Applying", s.ExpectedPreset);
        text += $"\n{s.WhyShown}";
        return text;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void Balloon(Notification n)
    {
        if (_exiting) return;
        _lastBalloon = n;
        var title = Loc.Show(n.TitleText, n.Title);
        var body = Loc.Show(n.BodyText, n.Text);
        _icon.BalloonTipTitle = Truncate(title, 63);
        _icon.BalloonTipText = Truncate(body.Length == 0 ? " " : body, 255);
        _icon.BalloonTipIcon = n.Error ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    private void OnBalloonClicked()
    {
        var n = _lastBalloon;
        if (n?.Page is { } page) OpenSettings(page, n.FocusId);
    }

    // ---- menu ----

    private void RefreshMenu()
    {
        var t = _state;
        var s = t?.Status;
        _header.Text = t is null ? "FxAuto" : Truncate(TooltipOf(t).Replace('\n', ' '), 90);
        _auto.Checked = s?.Mode == Mode.Auto;
        _lockCurrent.Checked = s?.Mode == Mode.Locked;
        _pause.Checked = s?.Mode == Mode.Paused;
        bool editing = s?.Mode == Mode.Editing;
        _auto.Enabled = _lockCurrent.Enabled = _lockOn.Enabled = _pause.Enabled = !editing;
        _startFx.Visible = s is null || !s.FxRunning;
        _startFx.Enabled = t is null || !t.StartingFxSound;

        _lockOn.DropDownItems.Clear();
        var fx = t?.Fx;
        foreach (var p in fx?.UserPresets ?? [])
            _lockOn.DropDownItems.Add(LockItem(p, s));
        if (fx is { BuiltInPresets.Count: > 0 })
        {
            var builtIn = new ToolStripMenuItem(Loc.T("Tray.BuiltIn"));
            foreach (var p in fx.BuiltInPresets) builtIn.DropDownItems.Add(LockItem(p, s));
            if (_lockOn.DropDownItems.Count > 0) _lockOn.DropDownItems.Add(new ToolStripSeparator());
            _lockOn.DropDownItems.Add(builtIn);
        }
        _lockOn.Enabled = !editing && _lockOn.DropDownItems.Count > 0;

        _output.DropDownItems.Clear();
        foreach (var name in fx?.OutputDevices ?? [])
        {
            string n = name;
            var item = new ToolStripMenuItem(n) { Checked = string.Equals(fx!.SelectedOutput, n, StringComparison.Ordinal) };
            item.Click += (_, _) => Send(MenuCommand.SwitchOutput, n);
            item.Enabled = !(t?.PresetOpRunning ?? false);
            _output.DropDownItems.Add(item);
        }
        _output.Enabled = _output.DropDownItems.Count > 0 && s?.FxRunning == true;

        var pending = _updates?.PendingVersion;
        _update.Visible = _updateSeparator.Visible = pending is not null;
        if (pending is not null) _update.Text = Loc.F("Updates.TrayItem", pending);
    }

    // ---- updates ----

    /// <summary>Connects the update engine: About gets it through the settings host, and every check result comes
    /// back here on the UI thread (CheckCompleted is raised on a pool thread).</summary>
    public void AttachUpdates(UpdateService updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        _updates = updates;
        _host.Updates = updates;
        updates.CheckCompleted += r => UiPost(() => OnUpdateChecked(r));
    }

    private void OnUpdateChecked(UpdateCheckResult r)
    {
        if (_exiting || _updates is null || !_updates.TryMarkAnnounced(r)) return;
        var version = r.Latest!.ToString();
        _log.Log("update.notified", new { version });
        Balloon(new Notification("Update available", $"FxAuto {version} is available. Click to see what's new.", false, SettingsPage.About)
        {
            TitleText = new Msg("Updates.NoteTitle"),
            BodyText = new Msg("Updates.NoteBody", version),
        });
    }

    private ToolStripMenuItem LockItem(string preset, StatusSnapshot? s)
    {
        string name = preset;
        var item = new ToolStripMenuItem(name) { Checked = s?.Mode == Mode.Locked && s.ExpectedPreset == name };
        item.Click += (_, _) => Send(MenuCommand.LockPreset, name);
        return item;
    }

    private void OpenLog()
    {
        string f = _log.TodayFile;
        if (File.Exists(f)) StartSafe(new ProcessStartInfo("notepad.exe") { Arguments = Quote(f), UseShellExecute = false });
        else StartSafe(new ProcessStartInfo("explorer.exe") { Arguments = Quote(_log.Directory), UseShellExecute = false });
    }

    private static string Quote(string s) => "\"" + s + "\"";

    private void StartSafe(ProcessStartInfo psi)
    {
        try { Process.Start(psi)?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Balloon(new Notification("Could not open", ex.Message, true) { TitleText = new Msg("Tray.CouldNotOpen") });
        }
    }

    // ---- exit ----

    /// <summary>Requests exit from any thread.</summary>
    public void PostExit(string why) => UiPost(() => RequestExit(why));

    /// <summary>Balloon notification from any thread.</summary>
    public void PostBalloon(Notification n) => UiPost(() => Balloon(n));

    private void RequestExit(string why)
    {
        if (_exiting) return;
        _exiting = true;
        // The coordinator waits for the command in progress (5 s cap) off the UI thread and then signals Stopped.
        Task.Run(() => _coordinator.Stop(why));
        // In case the coordinator no longer responds: forced exit after 10 s.
        var fallback = new System.Windows.Forms.Timer { Interval = 10_000 };
        fallback.Tick += (_, _) => { fallback.Dispose(); FinishExit(); };
        fallback.Start();
    }

    private bool _finished;

    private void FinishExit()
    {
        if (_finished) return;
        _finished = true;
        _opener.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _opener.Dispose();
            _host.Dispose();
            _menu.Dispose();
            _invoker.Dispose();
            foreach (var i in _icons.Values) i.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- icons generated in code (shared drawing: AppIcon) ----

    private static Icon MakeIcon(IconState s)
    {
        Color fill = s switch
        {
            IconState.Verified => AppIcon.Verified,
            IconState.Pending => AppIcon.Pending,
            IconState.Locked => AppIcon.Locked,
            IconState.Paused => AppIcon.Paused,
            _ => AppIcon.Failed,
        };
        using var bmp = AppIcon.Render(32, fill, paused: s == IconState.Paused);
        nint h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();   // own copy: the original HICON can be released
        }
        finally { Native.DestroyIcon(h); }
    }
}
