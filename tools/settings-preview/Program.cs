using System.Diagnostics;
using System.Drawing.Imaging;
using FxAuto.App;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Localization;
using FxAuto.Core.Presets;
using FxAuto.Ui.Settings;

namespace FxAuto.SettingsPreview;

/// <summary>settings-preview --theme dark|light [--lang en|es] --out &lt;dir&gt; [--mem] [--interactive] [--updates]</summary>
internal static class Program
{
    private static string _out = ".";
    private static string _theme = "light";
    private static readonly List<string> Log = [];

    [STAThread]
    private static int Main(string[] args)
    {
        _theme = Arg(args, "--theme") ?? "light";
        Loc.SetLanguage(Arg(args, "--lang") ?? Loc.English);
        _out = Arg(args, "--out") ?? Directory.GetCurrentDirectory();
        var mem = args.Contains("--mem");
        var interactive = args.Contains("--interactive");
        var updates = args.Contains("--updates");
        Directory.CreateDirectory(_out);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetColorMode(_theme == "dark" ? SystemColorMode.Dark : SystemColorMode.Classic);
        UiFlags.NoActivate = !interactive;

        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var ctx = new ApplicationContext();
        var exit = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                if (interactive) await Interactive();
                else if (mem) await Memory();
                else if (updates) await UpdateShots();
                else await Shots();
            }
            catch (Exception ex)
            {
                Log.Add("FAILED: " + ex);
                exit = 1;
            }
            File.WriteAllLines(Path.Combine(_out, mem ? $"memory-{_theme}.txt" : $"checklist-{_theme}.txt"), Log);
            ctx.ExitThread();
        };
        timer.Start();
        Application.Run(ctx);
        timer.Dispose();
        return exit;
    }

    private static string? Arg(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static void Check(bool ok, string what) => Log.Add($"{(ok ? "PASS" : "FAIL")}  {what}");

    private static SettingsForm NewForm(FakeSettingsHost host)
    {
        var f = new SettingsForm(host) { StartPosition = FormStartPosition.Manual, Location = new Point(80, 60) };
        f.Show();
        return f;
    }

    private static void Capture(Control c, string name)
    {
        Application.DoEvents();
        using var bmp = new Bitmap(c.Width, c.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try { UiNative.PrintWindow(c.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */); }
            finally { g.ReleaseHdc(hdc); }
        }
        var path = Path.Combine(_out, $"{name}-{_theme}.png");
        bmp.Save(path, ImageFormat.Png);
        Log.Add($"shot  {Path.GetFileName(path)}");
    }

    private static async Task Shots()
    {
        var host = new FakeSettingsHost(SynchronizationContext.Current!) { Latency = 60 };
        host.UsePreferences(Loc.Language, _theme);
        var form = NewForm(host);
        await Task.Delay(900);
        Log.Add($"theme {_theme}, dark mode enabled = {Application.IsDarkModeEnabled}, dpi = {form.DeviceDpi}");

        foreach (var page in Enum.GetValues<SettingsPage>())
        {
            form.ShowPage(page, null);
            await Task.Delay(350);
            Capture(form, "page-" + page.ToString().ToLowerInvariant());
        }
        Check(form.Fx is not null, "FxSound presets read through the host");

        // 1. invalid config cannot be applied
        var v0 = host.Config.Version;
        form.Edit(c => c with { Devices = c.Devices.Select((d, i) => i == 0 ? d with { Tag = "Headphones/bad!" } : d).ToList() });
        form.ShowPage(SettingsPage.Devices, "devices[0].tag");
        await Task.Delay(300);
        Check(!form.CanApply, "Apply is disabled while the working copy has errors");
        Capture(form, "invalid-devices");
        await form.ApplyNowAsync();
        await Task.Delay(300);
        Check(host.Config.Version == v0, "invalid config was not sent to the host");
        form.ShowPage(SettingsPage.Status, "issues");
        await Task.Delay(300);
        Capture(form, "invalid-status");
        form.Edit(c => c with { Devices = c.Devices.Select((d, i) => i == 0 ? d with { Tag = "Headphones" } : d).ToList() });

        // 2. warnings do not block
        form.Edit(c => c with { Assignments = [.. c.Assignments, new Assignment("realtek", "video", null, "Not In FxSound")] });
        await Task.Delay(100);
        Check(form.CanApply, "a warning (missing preset) does not block Apply");
        Check(form.WorkingIssues.Any(i => i.Level == IssueLevel.Warning && i.Message.Contains("Not In FxSound", StringComparison.Ordinal)), "the warning is shown");

        // 3. stale apply → rebase per block
        host.AnnounceSaves = false;
        host.SimulateExternalEdit(); // adds a source and bumps the version behind the window's back
        form.Edit(c => c with { General = c.General with { LogTitles = true } });
        form.ShowPage(SettingsPage.General, null);
        var v1 = host.Config.Version;
        await form.ApplyNowAsync();
        await Task.Delay(400);
        Check(host.Config.Version == v1, "stale apply was rejected by the host");
        Check(form.Working.Sources.Any(s => s.Id == "added-elsewhere"), "rebase took the newer Sources block");
        Check(form.Working.General.LogTitles, "rebase kept the unapplied General edit");
        Check(form.Working.Assignments.Any(a => a.Preset == "Not In FxSound"), "rebase kept the unapplied Category presets edit");
        Capture(form, "stale-rebase");
        await form.ApplyNowAsync();
        await Task.Delay(400);
        Check(host.Config.Version == v1 + 1 && host.Config.Config.General.LogTitles && host.Config.Config.Sources.Any(s => s.Id == "added-elsewhere"),
            "second Apply saved the merged config");
        Check(!form.IsDirty, "working copy is clean after Apply");
        Check(!form.MessageKeys.Contains("rebase"), "a successful Apply clears the stale/rebase bar");
        foreach (var i in form.WorkingIssues.Where(i => i.Level == IssueLevel.Error)) Log.Add($"      error {i.Path}: {i.Message}");
        Capture(form, "applied");
        host.AnnounceSaves = true;

        // 4. a save announced by the host while the window is open
        form.Edit(c => c with { General = c.General with { IdleGraceSeconds = 90 } });
        host.SimulateExternalEdit();
        await Task.Delay(400);
        Check(form.Working.General.IdleGraceSeconds == 90 && form.IsDirty, "ConfigChanged rebased and kept the local edit");
        Capture(form, "external-change");
        form.DismissMessage("rebase");
        var v2 = host.Config.Version;
        await form.ApplyNowAsync();
        await Task.Delay(500);
        Check(host.Config.Version == v2 + 1 && !form.IsDirty, "Apply with the host announcing its own save: saved, clean");
        Check(!form.MessageKeys.Contains("rebase"), "no false \"changed outside this window\" bar after our own Apply");
        Capture(form, "applied-announced");

        // 4b. saves blocked by the host (read-only, then protected)
        form.Edit(c => c with { General = c.General with { IdleGraceSeconds = 75 } });
        host.SimulateSaveBlocked("config.json was written by a newer FxAuto (schemaVersion 3); it is used read-only and never saved.");
        await Task.Delay(300);
        Check(!form.CanApply && form.MessageKeys.Contains("blocked"), "read-only config: Apply disabled and the reason shown");
        Capture(form, "blocked-readonly");
        host.SimulateSaveBlocked("config.json could not be used (invalid JSON at line 3); FxAuto runs on the backup and protects the file until it reads valid again.", replaceable: true);
        await Task.Delay(300);
        Check(!form.CanApply && form.MessageKeys.Contains("blocked"), "protected config: Apply disabled, replace offered in the bar");
        Capture(form, "blocked-protected");
        host.SimulateSaveBlocked(null);
        await Task.Delay(300);
        Check(form.CanApply && !form.MessageKeys.Contains("blocked"), "saves allowed again: bar gone, Apply enabled");
        form.Edit(c => c with { General = c.General with { IdleGraceSeconds = 90 } });

        // 5. Cancel discards the working copy
        var before = host.Config.Version;
        form.Edit(c => c with { General = c.General with { LogLevel = "debug" } });
        form.ShowPage(SettingsPage.Presets, null);
        await Task.Delay(300);
        Capture(form, "presets-page");

        // 6. edit in FxSound bar
        form.ShowMessage("extra1", FxAuto.Ui.Settings.Tone.Warning, "First test message.");
        form.ShowMessage("extra2", FxAuto.Ui.Settings.Tone.Warning, "Second test message.");
        await host.BeginEditAsync("Headphones - Music");
        await Task.Delay(300);
        Check(form.VisibleBars <= SettingsForm.MaxBars, $"bars above the page with the edit bar: {form.VisibleBars} (max {SettingsForm.MaxBars})");
        Check(form.EditingPreset == "Headphones - Music", "edit bar names the preset (StatusSnapshot.EditingPreset)");
        Capture(form, "edit-bar");
        await host.FinishEditAsync(EditOutcome.Discard, null);
        await Task.Delay(300);

        // 7. dialogs
        var fx = form.Fx!;
        using (var dup = new DuplicateDialog("Headphones - Music", host.Config.Config, fx)) await ShowDialog(form, dup, "dialog-duplicate");
        var proposal = SourceEditorDialog.Propose(host.DetectPlayingNow()[0]);
        using (var src = new SourceEditorDialog(host.Config.Config, fx.UserPresets.Concat(fx.BuiltInPresets).ToList(), proposal, "chrome.exe"))
            await ShowDialog(form, src, "dialog-source-detected");
        using (var pick = new PickPlayingDialog(host.DetectPlayingNow())) await ShowDialog(form, pick, "dialog-detect-playing");
        using (var del = new ConfirmDialog(Loc.T("Presets.Delete.Title"), Loc.F("Presets.Delete.ConfirmUsed", "Headphones - Video",
                       $"Headset: {Categories.DisplayName(Categories.Video)}, {Categories.DisplayName(Categories.LiveStream)}, {Categories.DisplayName(Categories.Podcast)}"),
                   Loc.T("Presets.Delete.Ok"), SettingsForm.GameRunningWarning, destructive: true))
        {
            Check(del.AcceptButton is null, "delete confirmation: Enter does not confirm (no accept button)");
            Check(del.ActiveControl is Button b && b.Text == Loc.T("Form.Cancel"), "delete confirmation: focus starts on Cancel");
            await ShowDialog(form, del, "dialog-delete-game-running");
        }
        using (var def = new ChoiceDialog(Loc.T("Presets.Delete.Title"),
                   Loc.F("Presets.Delete.Replace", "Headphones - General", "Headset", "Headset: " + Loc.T("Presets.UsedDefault")),
                   Loc.T("Presets.Delete.ReplaceWith"), fx.UserPresets.Where(p => p != "Headphones - General"), Loc.T("Presets.Delete.Ok"), null, destructive: true))
        {
            Check(def.AcceptButton is null, "delete-default dialog: no accept button");
            await ShowDialog(form, def, "dialog-delete-default");
        }

        // language and theme: Apply asks the opener to reopen the window (texts and palette are read when it is built)
        form.ShowPage(SettingsPage.General, null);
        await Task.Delay(300);
        Capture(form, "general-selectors");
        SettingsPage? reopenOn = null;
        form.ReopenRequested += p => reopenOn = p;
        var language = Loc.Language;
        var other = language == Loc.Spanish ? Loc.English : Loc.Spanish;
        var dark = Application.IsDarkModeEnabled;
        form.Edit(c => c with { General = c.General with { Language = other, Theme = dark ? UiChoices.Light : UiChoices.Dark } });
        await form.ApplyNowAsync();
        await Task.Delay(400);
        Check(reopenOn == SettingsPage.General, "Apply with another language and theme asks to reopen on the same page");
        Check(Loc.Language == other && Application.IsDarkModeEnabled != dark, "the new language and colour mode are in effect");
        Check(host.Config.Config.General.Language == other, "general.language saved through the host");
        form.Edit(c => c with { General = c.General with { Language = language, Theme = dark ? UiChoices.Dark : UiChoices.Light } });
        reopenOn = null;
        await form.ApplyNowAsync();
        await Task.Delay(400);
        Check(reopenOn is null && Loc.Language == language && Application.IsDarkModeEnabled == dark,
            "switching back to what the window was built with needs no reopen");
        before = host.Config.Version;

        // Cancel
        form.ShowPage(SettingsPage.General, null);
        var cancel = form.CancelButton as Button;
        cancel?.PerformClick();
        await Task.Delay(300);
        Check(form.IsDisposed || !form.Visible, "Cancel closed the window");
        Check(host.Config.Version == before, "Cancel discarded the working copy (nothing saved)");
        if (!form.IsDisposed) form.Dispose();
    }

    private static async Task ShowDialog(Form owner, Form dlg, string name)
    {
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new Point(owner.Left + 120, owner.Top + 90);
        dlg.Show(owner);
        await Task.Delay(350);
        Capture(dlg, name);
        dlg.Close();
    }

    private static async Task Memory()
    {
        var p = Process.GetCurrentProcess();
        Log.Add($"pid {Environment.ProcessId}");
        p.Refresh();
        Log.Add($"before open: private {p.PrivateMemorySize64 / 1048576.0:0.0} MB, working set {p.WorkingSet64 / 1048576.0:0.0} MB");
        var host = new FakeSettingsHost(SynchronizationContext.Current!) { Latency = 30 };
        host.UsePreferences(Loc.Language, _theme);
        using (var opener = new SettingsWindowOpener(host))
        {
            for (var round = 0; round < 3; round++)
            {
                foreach (var page in Enum.GetValues<SettingsPage>())
                {
                    opener.Open(page, null);
                    await Task.Delay(250);
                }
                p.Refresh();
                Log.Add($"open, round {round + 1}: private {p.PrivateMemorySize64 / 1048576.0:0.0} MB");
                var form = Application.OpenForms.OfType<SettingsForm>().Single();
                form.Close();
                await Task.Delay(300);
                Check(!opener.IsOpen && Application.OpenForms.OfType<SettingsForm>().Any() == false, $"round {round + 1}: window disposed on close");
            }
        }
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Log.Add("closed; waiting 30 s for GC to settle");
        File.WriteAllLines(Path.Combine(_out, $"memory-{_theme}.txt"), Log);
        await Task.Delay(30_000);
        GC.Collect();
        p.Refresh();
        Log.Add($"closed +30 s: private {p.PrivateMemorySize64 / 1048576.0:0.0} MB, working set {p.WorkingSet64 / 1048576.0:0.0} MB");
        Check(p.PrivateMemorySize64 < 80L * 1048576, "private bytes < 80 MB with the window closed");
        File.WriteAllLines(Path.Combine(_out, $"memory-{_theme}.txt"), Log);
        await Task.Delay(20_000); // window for the external Get-Process -Id measurement
    }

    private static async Task Interactive()
    {
        var host = new FakeSettingsHost(SynchronizationContext.Current!);
        host.UsePreferences(Loc.Language, _theme);
        using var opener = new SettingsWindowOpener(host);
        opener.Open(SettingsPage.Status);
        while (opener.IsOpen) await Task.Delay(200);
    }

    /// <summary>--updates: About and General with a fake update engine (no network): version available, download in
    /// progress, verification failed, up to date; files updates-*-&lt;lang&gt;-&lt;theme&gt;.png.</summary>
    private static async Task UpdateShots()
    {
        var lang = Loc.Language;
        var host = new FakeSettingsHost(SynchronizationContext.Current!) { Latency = 60 };
        host.UsePreferences(lang, _theme);
        var fake = new FakeUpdateController { Current = new FxAuto.Core.Updates.SemVersion(1, 1, 0), Latency = 250 };
        host.Updates = fake;
        fake.NextResult = FakeUpdateController.Available(fake.Current, DateTimeOffset.Now);
        await fake.CheckAsync();
        var form = NewForm(host);
        form.Size = new Size(980, 860);
        await Task.Delay(900);

        form.ShowPage(SettingsPage.About, null);
        await Task.Delay(500);
        Capture(form, $"updates-available-{lang}");
        Check(fake.PendingVersion?.ToString() == "1.2.0", "pending version 1.2.0");

        // Update now → progress (fake download, then a SHA-256 mismatch)
        var updateNow = Find<Button>(form, Loc.T("Updates.UpdateNow"));
        Check(updateNow is { Visible: true }, "Update now visible when a version is available");
        updateNow?.PerformClick();
        await Task.Delay(1400);
        Capture(form, $"updates-downloading-{lang}");
        await Task.Delay(2600);
        Capture(form, $"updates-error-{lang}");
        Check(Find<Button>(form, Loc.T("Updates.UpdateNow")) is { Visible: true }, "after a failed verification Update now is offered again");

        // Check for updates → up to date
        fake.NextResult = null;
        Find<Button>(form, Loc.T("Updates.Check"))?.PerformClick();
        await Task.Delay(900);
        Capture(form, $"updates-uptodate-{lang}");
        Check(Find<Button>(form, Loc.T("Updates.UpdateNow")) is not { Visible: true }, "up to date: no Update now");

        form.ShowPage(SettingsPage.General, null);
        await Task.Delay(400);
        var checkAuto = Find<CheckBox>(form, Loc.T("Updates.CheckAuto"));
        Check(checkAuto is { Checked: true } && Find<CheckBox>(form, Loc.T("Updates.Prereleases")) is { Checked: false },
              "General: automatic checks on, pre-releases off by default");
        for (Control? c = checkAuto?.Parent; c is not null; c = c.Parent)
        {
            if (c is ScrollableControl { AutoScroll: true } sc)
            {
                sc.ScrollControlIntoView(checkAuto!.Parent!.Parent!);
                break;
            }
        }
        await Task.Delay(300);
        Capture(form, $"updates-general-{lang}");
        foreach (var c in AllControls(form).Where(c => c.Visible && c is Label or CheckBox or ButtonBase))
        {
            if (c.Text.Length == 0 || !c.IsHandleCreated) continue;
            var need = TextRenderer.MeasureText(c.Text, c.Font).Width;
            if (c is CheckBox) need += 20;
            if (c.MaximumSize.Width == 0 && c.Width + 1 < need) Log.Add($"CLIPPED  {c.GetType().Name} \"{c.Text}\" {c.Width} < {need}");
        }
        form.Close();
        form.Dispose();
    }

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in AllControls(c)) yield return d;
        }
    }

    private static T? Find<T>(Control root, string text) where T : Control
        => AllControls(root).OfType<T>().FirstOrDefault(c => c.Text == text);
}
