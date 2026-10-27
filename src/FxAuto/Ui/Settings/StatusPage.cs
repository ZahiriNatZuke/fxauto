using FxAuto.App;
using FxAuto.Core;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>Status: what is playing, the output device, the active preset and why, the mode, and the issues with
/// [Fix] (REQ-109).</summary>
internal sealed class StatusPage : SettingsPageBase
{
    private readonly ThemedListView _playing = new() { AccessibleName = Loc.T("Status.NowPlaying"), MinimumSize = new Size(100, 150), Height = 170 };
    private readonly Label _fx = Ui.Text("");
    private readonly Dot _fxDot = new();
    private readonly Label _device = Ui.Text("");
    private readonly Label _preset = Ui.Text("");
    private readonly Label _why = Ui.Text("", wrap: true, maxWidth: 560);
    private readonly Label _failure = Ui.Text("", wrap: true, maxWidth: 640);
    private readonly RadioButton _auto = new() { Text = Loc.T("Status.Auto"), AutoSize = true, Margin = new Padding(0, 4, Theme.S4, 0) };
    private readonly RadioButton _paused = new() { Text = Loc.T("Status.Paused"), AutoSize = true, Margin = new Padding(0, 4, Theme.S4, 0) };
    private readonly RadioButton _locked = new() { Text = Loc.T("Status.LockedOn"), AutoSize = true, Margin = new Padding(0, 4, Theme.S2, 0) };
    private readonly ComboBox _lockPreset = Ui.Combo([], 220, Loc.T("Status.LockPreset"));
    private readonly TableLayoutPanel _issues = Stack(1);
    private readonly Label _issuesEmpty = Ui.Text(Loc.T("Status.NoProblems"));
    private bool _binding;

    public StatusPage(SettingsForm window) : base(window)
    {
        _playing.SetColumns((Loc.T("Status.Col.Source"), 230), (Loc.T("Status.Col.Category"), 120), (Loc.T("Status.Col.How"), 190), (Loc.T("Status.Col.State"), 100));

        var now = CardOf(Ui.SectionTitle(Loc.T("Status.NowPlaying")), _playing,
            Ui.Row(Ui.Button(Loc.T("Status.CreateSource"), () => Window.ShowPage(SettingsPage.Sources, SourcesPage.DetectPlayingId))));

        var facts = Ui.Grid();
        var fxRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        fxRow.Controls.Add(_fxDot);
        fxRow.Controls.Add(_fx);
        Ui.AddField(facts, "FxSound", fxRow);
        Ui.AddField(facts, Loc.T("Status.OutputDevice"), _device);
        Ui.AddField(facts, Loc.T("Status.ActivePreset"), _preset);
        Ui.AddField(facts, Loc.T("Status.Why"), _why);
        _failure.Tag = "keep-color";
        var output = CardOf(Ui.SectionTitle(Loc.T("Status.OutputAndPreset")), facts, _failure);

        var modeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        modeRow.Controls.AddRange([_auto, _paused, _locked, _lockPreset]);
        var pauseHour = Ui.Button(Loc.T("Status.PauseHour"), () => _ = Host.SetModeAsync(Mode.Paused, null, TimeSpan.FromHours(1)));
        var mode = CardOf(Ui.SectionTitle(Loc.T("Status.Mode")), modeRow,
            Ui.Hint(Loc.T("Status.ModeHint")),
            Ui.Row(pauseHour));
        _auto.CheckedChanged += (_, _) => { if (_auto.Checked && !_binding) _ = Host.SetModeAsync(Mode.Auto); };
        _paused.CheckedChanged += (_, _) => { if (_paused.Checked && !_binding) _ = Host.SetModeAsync(Mode.Paused); };
        _locked.CheckedChanged += (_, _) =>
        {
            _lockPreset.Enabled = _locked.Checked && _locked.Enabled;
            if (_locked.Checked && !_binding) LockSelected();
        };
        _lockPreset.SelectionChangeCommitted += (_, _) => { if (_locked.Checked && !_binding) LockSelected(); };

        var issues = CardOf(Ui.SectionTitle(Loc.T("Status.Problems")), _issues);

        var stack = Stack(4);
        stack.Controls.Add(now, 0, 0);
        stack.Controls.Add(output, 0, 1);
        stack.Controls.Add(mode, 0, 2);
        stack.Controls.Add(issues, 0, 3);
        Controls.Add(Scrolling(stack));
    }

    public override SettingsPage Page => SettingsPage.Status;

    public override string Heading => Loc.T("Nav.Status");

    public override string? Subheading => Loc.T("Status.Subheading");

    private void LockSelected()
    {
        if (_lockPreset.SelectedItem is string p) _ = Host.SetModeAsync(Mode.Locked, p);
    }

    public override void Bind() => OnStatusChanged();

    public override void OnStatusChanged()
    {
        _binding = true;
        try
        {
            var s = Host.Status;
            BindPlaying(s);
            _fxDot.Tone = !s.FxRunning ? Tone.Error : s.FxElevated == true ? Tone.Warning : Tone.Success;
            _fx.Text = !s.FxRunning ? Loc.T("Status.Fx.NotRunning")
                : s.FxElevated == true ? Loc.T("Status.Fx.Elevated")
                : s.ThroughFxSound ? Loc.T("Status.Fx.Through") : Loc.T("Status.Fx.Bypassed");
            _device.Text = s.DeviceName is null ? Loc.T("Status.UnknownDevice") : s.OutputName is null || s.OutputName == s.DeviceName ? s.DeviceName : $"{s.DeviceName}  ({s.OutputName})";
            _preset.Text = s.ExpectedPreset is null ? Loc.T("Status.Preset.None")
                : s.VerifiedPreset == s.ExpectedPreset ? Loc.F("Status.Preset.Verified", s.ExpectedPreset)
                : s.VerifiedPreset is null ? Loc.F("Status.Preset.NotVerified", s.ExpectedPreset)
                : Loc.F("Status.Preset.Shows", s.ExpectedPreset, s.VerifiedPreset);
            _why.Text = string.IsNullOrWhiteSpace(s.Why) ? Loc.T("Status.NothingPlaying") : Capitalize(s.WhyShown);
            _failure.Visible = s.LastFailure is not null;
            _failure.Text = s.LastFailure is null ? "" : Loc.F("Status.LastProblem", Loc.Known(s.LastFailure));
            _failure.ForeColor = P.Error;

            _auto.Checked = s.Mode == Mode.Auto;
            _paused.Checked = s.Mode == Mode.Paused;
            _locked.Checked = s.Mode == Mode.Locked;
            var enabled = s.Mode != Mode.Editing;
            _auto.Enabled = _paused.Enabled = _locked.Enabled = enabled;
            _lockPreset.Enabled = enabled && _locked.Checked;
            var choices = PresetChoices();
            if (!_lockPreset.Items.Cast<string>().SequenceEqual(choices))
            {
                _lockPreset.Items.Clear();
                foreach (var c in choices) _lockPreset.Items.Add(c);
            }
            var current = s.VerifiedPreset ?? s.ExpectedPreset;
            if (current is not null && _lockPreset.Items.Contains(current)) _lockPreset.SelectedItem = current;
            BindIssues(s);
        }
        finally
        {
            _binding = false;
        }
    }

    private void BindPlaying(StatusSnapshot s)
    {
        _playing.BeginUpdate();
        _playing.Items.Clear();
        foreach (var p in s.Playing.OrderByDescending(p => p.IsWinner).ThenByDescending(p => p.Playing))
        {
            var name = p.Site is not null ? $"{p.Exe ?? Loc.T("Status.Browser")} · {p.Site}" : p.Exe ?? Loc.F("Status.Process", p.RootPid);
            var source = p.SourceId is null ? null : Working.Sources.FirstOrDefault(x => x.Id == p.SourceId);
            if (source is not null) name = $"{source.Name}  ({name})";
            var state = p.IsWinner ? Loc.T("Status.State.Chosen") : !p.Playing ? Loc.T("Status.State.Quiet")
                : p.IsForeground ? Loc.T("Status.State.Foreground") : Loc.T("Status.State.Background");
            var item = new ListViewItem(name) { UseItemStyleForSubItems = false };
            item.SubItems.Add(Categories.DisplayName(p.Category));
            item.SubItems.Add(Loc.How(p.How));
            var st = item.SubItems.Add(state);
            if (p.IsWinner) { st.ForeColor = P.Success; st.Font = Theme.BodyStrong; }
            else if (!p.Playing) st.ForeColor = P.TextSecondary;
            _playing.Items.Add(item);
        }
        if (_playing.Items.Count == 0)
        {
            var empty = new ListViewItem(Loc.T("Status.NothingThroughFx")) { ForeColor = P.TextSecondary };
            _playing.Items.Add(empty);
        }
        _playing.EndUpdateAndFit();
    }

    private void BindIssues(StatusSnapshot s)
    {
        var rows = s.Issues.Select(i => (Issue: i, Draft: false)).ToList();
        if (Window.IsDirty)
            foreach (var i in Window.WorkingIssues)
                if (!rows.Any(r => r.Issue.Path == i.Path && r.Issue.Level == i.Level))
                    rows.Add((i, true));
        rows = rows.OrderBy(r => r.Issue.Level).ToList();

        _issues.SuspendLayout();
        foreach (Control c in _issues.Controls.Cast<Control>().ToArray()) { _issues.Controls.Remove(c); if (c != _issuesEmpty) c.Dispose(); }
        _issues.RowStyles.Clear();
        _issues.RowCount = Math.Max(1, rows.Count);
        if (rows.Count == 0)
        {
            _issues.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _issues.Controls.Add(_issuesEmpty, 0, 0);
        }
        for (var i = 0; i < rows.Count; i++)
        {
            _issues.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _issues.Controls.Add(IssueRow(rows[i].Issue, rows[i].Draft), 0, i);
        }
        _issues.ResumeLayout(true);
        Theme.Apply(_issues, P, P.Card);
    }

    private Control IssueRow(ConfigIssue issue, bool draft)
    {
        var row = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Theme.S2) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var tone = issue.Level switch { IssueLevel.Error => Tone.Error, IssueLevel.Warning => Tone.Warning, _ => Tone.Info };
        var dot = new Dot { Tone = tone, Palette = P, AccessibleName = issue.Level.ToString() };
        var level = issue.Level switch { IssueLevel.Error => "Error", IssueLevel.Warning => "Warning", _ => "Note" };
        var text = Ui.Text(Loc.F((draft ? "Status.IssueDraft." : "Status.Issue.") + level, issue.Shown), wrap: true, maxWidth: 560);
        dot.AccessibleName = Loc.T("Status.Level." + level);
        row.Controls.Add(dot, 0, 0);
        row.Controls.Add(text, 1, 0);
        var page = PageFor(issue.Path);
        if (page is { } target)
        {
            var fix = Ui.Button(Loc.T("Status.Fix"), () => Window.ShowPage(target, issue.Path), Loc.F("Status.FixAccessible", issue.Shown));
            fix.MinimumSize = new Size(64, 28);
            fix.Margin = new Padding(Theme.S2, 0, 0, 0);
            row.Controls.Add(fix, 2, 0);
        }
        return row;
    }

    private static SettingsPage? PageFor(string path)
    {
        if (path.StartsWith("devices", StringComparison.Ordinal)) return SettingsPage.Devices;
        if (path.StartsWith("assignments", StringComparison.Ordinal)) return SettingsPage.Categories;
        if (path.StartsWith("sources", StringComparison.Ordinal) || path.StartsWith("ignoredApps", StringComparison.Ordinal)
            || path.StartsWith("launchers", StringComparison.Ordinal) || path.StartsWith("gameFolderExclusions", StringComparison.Ordinal))
            return SettingsPage.Sources;
        if (path.StartsWith("presets", StringComparison.Ordinal) || path.StartsWith("fxsound", StringComparison.OrdinalIgnoreCase))
            return SettingsPage.Presets;
        if (path.StartsWith("general", StringComparison.Ordinal)) return SettingsPage.General;
        return null;
    }

    /// <summary>Reasons are built lower-case to read inside a sentence; as a value of their own they start upper-case.</summary>
    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], System.Globalization.CultureInfo.CurrentCulture) + s[1..];

    public override void FocusItem(string id)
    {
        if (id == "issues") _issues.Parent?.Parent?.Focus();
    }
}
