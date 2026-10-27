using FxAuto.App;
using FxAuto.Core.Arbitration;
using FxAuto.Core.Config;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>General: language and theme, start with Windows, FxSound start policy (REQ-112), log, calls and browser
/// tabs, updates (general.updates) and timings.</summary>
internal sealed class GeneralPage : SettingsPageBase
{
    private readonly CheckBox _startWithWindows = new() { Text = Loc.T("General.StartWithWindows"), AutoSize = true };
    private readonly CheckBox _startFx = new() { Text = Loc.T("General.StartFx"), AutoSize = true };
    private readonly TextBox _fxPath = new() { Width = 420 };
    private readonly ComboBox _logLevel = Ui.Combo([new Ui.Choice("info", Loc.T("General.LogNormal")), new Ui.Choice("debug", Loc.T("General.LogDebug"))], 200);
    private readonly CheckBox _logTitles = new() { Text = Loc.T("General.LogTitles"), AutoSize = true };
    private readonly CheckBox _readTabs = new() { Text = Loc.T("General.ReadTabs"), AutoSize = true };
    private readonly CheckBox _checkUpdates = new() { Text = Loc.T("Updates.CheckAuto"), AutoSize = true };
    private readonly CheckBox _prereleases = new() { Text = Loc.T("Updates.Prereleases"), AutoSize = true };
    private readonly ComboBox _calls = Ui.Combo([new Ui.Choice(nameof(CallPolicy.Ignore), Loc.T("General.CallsIgnore")),
        new Ui.Choice(nameof(CallPolicy.Category), Loc.T("General.CallsCategory"))], 400);
    private readonly ComboBox _language = Ui.Combo([new Ui.Choice(UiChoices.System, Loc.T("General.SystemDefault")),
        new Ui.Choice(Loc.English, "English"), new Ui.Choice(Loc.Spanish, "Español")], 200);
    private readonly ComboBox _theme = Ui.Combo([new Ui.Choice(UiChoices.System, Loc.T("General.SystemDefault")),
        new Ui.Choice(UiChoices.Light, Loc.T("General.ThemeLight")), new Ui.Choice(UiChoices.Dark, Loc.T("General.ThemeDark"))], 200);
    private readonly NumericUpDown _idleGrace = Ui.Number(0, 3600, 60);
    private readonly NumericUpDown _minDwell = Ui.Number(0, 60_000, 5000, increment: 500);
    private readonly NumericUpDown _gameLoading = Ui.Number(0, 600_000, 60_000, increment: 5000);
    private readonly NumericUpDown _startupGrace = Ui.Number(0, 60, 60);
    private readonly NumericUpDown _checkEvery = Ui.Number(1, 3600, 60);
    private readonly NumericUpDown _attack = Ui.Number(0, 10_000, 1500, increment: 100);
    private readonly NumericUpDown _release = Ui.Number(500, 120_000, 8000, increment: 500);
    private readonly NumericUpDown _gameRelease = Ui.Number(500, 300_000, 20_000, increment: 1000);
    private bool _binding;

    public GeneralPage(SettingsForm window) : base(window)
    {
        var look = Ui.Grid(LabelColumn);
        _language.AccessibleName = Loc.T("General.Language");
        _theme.AccessibleName = Loc.T("General.Theme");
        Ui.AddField(look, Loc.T("General.Language"), _language);
        Ui.AddField(look, Loc.T("General.Theme"), _theme, Loc.T("General.LookHint"));

        var startup = Ui.Grid(LabelColumn);
        Ui.AddField(startup, "", _startWithWindows);
        Ui.AddField(startup, "", _startFx,
            Loc.T("General.StartFxHint"));
        var browse = Ui.Button(Loc.T("General.Browse"), BrowseFx);
        browse.Margin = new Padding(Theme.S2, 0, 0, 0);
        var pathRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _fxPath.AccessibleName = Loc.T("General.FxPath");
        var pathField = Ui.Field(_fxPath);
        pathField.Margin = Padding.Empty;
        pathRow.Controls.AddRange([pathField, browse]);
        Ui.AddField(startup, Loc.T("General.FxPath"), pathRow, Loc.T("General.FxPathHint"));

        var log = Ui.Grid(LabelColumn);
        Ui.AddField(log, Loc.T("General.LogLevel"), _logLevel);
        Ui.AddField(log, "", _logTitles, Loc.T("General.LogTitlesHint"));
        Ui.AddField(log, "", _readTabs, Loc.T("General.ReadTabsHint"));
        Ui.AddField(log, Loc.T("General.Calls"), _calls);

        var updates = Ui.Grid(LabelColumn);
        Ui.AddField(updates, "", _checkUpdates, Loc.T("Updates.CheckAutoHint"));
        Ui.AddField(updates, "", _prereleases, Loc.T("Updates.PrereleasesHint"));

        var timing = Ui.Grid(LabelColumn);
        AddNumber(timing, Loc.T("General.Attack"), _attack, Loc.T("Unit.Ms"));
        AddNumber(timing, Loc.T("General.Release"), _release, Loc.T("Unit.MsSilence"));
        AddNumber(timing, Loc.T("General.GameRelease"), _gameRelease, Loc.T("Unit.MsSilence"));
        AddNumber(timing, Loc.T("General.MinDwell"), _minDwell, Loc.T("Unit.Ms"));
        AddNumber(timing, Loc.T("General.GameLoading"), _gameLoading, Loc.T("Unit.Ms"));
        AddNumber(timing, Loc.T("General.IdleGrace"), _idleGrace, Loc.T("Unit.SNothingPlaying"));
        AddNumber(timing, Loc.T("General.StartupGrace"), _startupGrace, Loc.T("Unit.S"));
        AddNumber(timing, Loc.T("General.CheckEvery"), _checkEvery, Loc.T("Unit.S"));

        var stack = Stack(5);
        stack.Controls.Add(CardOf(Ui.SectionTitle(Loc.T("General.Look")), look), 0, 0);
        stack.Controls.Add(CardOf(Ui.SectionTitle(Loc.T("General.Startup")), startup), 0, 1);
        stack.Controls.Add(CardOf(Ui.SectionTitle(Loc.T("General.Log")), log), 0, 2);
        stack.Controls.Add(CardOf(Ui.SectionTitle(Loc.T("Updates.Title")), updates), 0, 3);
        stack.Controls.Add(CardOf(Ui.SectionTitle(Loc.T("General.Timing")), timing), 0, 4);
        Controls.Add(Scrolling(stack));

        _language.SelectionChangeCommitted += (_, _) => Set(g => g with { Language = ((Ui.Choice)_language.SelectedItem!).Value });
        _theme.SelectionChangeCommitted += (_, _) => Set(g => g with { Theme = ((Ui.Choice)_theme.SelectedItem!).Value });
        _startWithWindows.CheckedChanged += (_, _) => Set(g => g with { StartWithWindows = _startWithWindows.Checked });
        _startFx.CheckedChanged += (_, _) => Set(g => g with { StartFxSoundIfClosed = _startFx.Checked });
        _fxPath.TextChanged += (_, _) => Set(g => g with { FxSoundPath = _fxPath.Text.Trim().Length == 0 ? null : _fxPath.Text.Trim() });
        _logLevel.SelectionChangeCommitted += (_, _) => Set(g => g with { LogLevel = ((Ui.Choice)_logLevel.SelectedItem!).Value });
        _logTitles.CheckedChanged += (_, _) => Set(g => g with { LogTitles = _logTitles.Checked });
        _readTabs.CheckedChanged += (_, _) => Set(g => g with { ReadBrowserTabs = _readTabs.Checked });
        _checkUpdates.CheckedChanged += (_, _) => Set(g => g with { Updates = g.EffectiveUpdates with { CheckAutomatically = _checkUpdates.Checked } });
        _prereleases.CheckedChanged += (_, _) => Set(g => g with { Updates = g.EffectiveUpdates with { IncludePrereleases = _prereleases.Checked } });
        _calls.SelectionChangeCommitted += (_, _) => Set(g => g with { CallPolicy = Enum.Parse<CallPolicy>(((Ui.Choice)_calls.SelectedItem!).Value) });
        _idleGrace.ValueChanged += (_, _) => Set(g => g with { IdleGraceSeconds = (int)_idleGrace.Value });
        _minDwell.ValueChanged += (_, _) => Set(g => g with { MinDwellMs = (int)_minDwell.Value });
        _gameLoading.ValueChanged += (_, _) => Set(g => g with { GameLoadingMaxMs = (int)_gameLoading.Value });
        _startupGrace.ValueChanged += (_, _) => Set(g => g with { StartupGraceSeconds = (int)_startupGrace.Value });
        _checkEvery.ValueChanged += (_, _) => Set(g => g with { CheckIntervalSeconds = (int)_checkEvery.Value });
        _attack.ValueChanged += (_, _) => Set(g => g with { Detection = g.Detection with { AttackMs = (int)_attack.Value } });
        _release.ValueChanged += (_, _) => Set(g => g with { Detection = g.Detection with { ReleaseMs = (int)_release.Value } });
        _gameRelease.ValueChanged += (_, _) => Set(g => g with { Detection = g.Detection with { GameReleaseMs = (int)_gameRelease.Value } });
    }

    public override SettingsPage Page => SettingsPage.General;

    public override string Heading => Loc.T("Nav.General");

    public override string? Subheading => Loc.T("General.Subheading");

    /// <summary>One label column for every card on this page (logical px), so the fields line up.</summary>
    private const int LabelColumn = 250;

    private static void AddNumber(TableLayoutPanel grid, string label, NumericUpDown n, string unit)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        n.AccessibleName = $"{label}, {unit}";
        var u = Ui.Text(unit, Theme.Body);
        u.Margin = new Padding(Theme.S2, 5, 0, 0);
        row.Controls.AddRange([n, u]);
        Ui.AddField(grid, label, row);
    }

    private void Set(Func<GeneralSettings, GeneralSettings> change)
    {
        if (_binding) return;
        Window.Edit(c => c with { General = change(c.General) });
    }

    public override void Bind()
    {
        _binding = true;
        try
        {
            var g = Working.General;
            _language.SelectedItem = _language.Items.Cast<Ui.Choice>().FirstOrDefault(c => c.Value == g.Language) ?? _language.Items[0];
            _theme.SelectedItem = _theme.Items.Cast<Ui.Choice>().FirstOrDefault(c => c.Value == g.Theme) ?? _theme.Items[0];
            _startWithWindows.Checked = g.StartWithWindows;
            _startFx.Checked = g.StartFxSoundIfClosed;
            _fxPath.Text = g.FxSoundPath ?? "";
            _logLevel.SelectedItem = _logLevel.Items.Cast<Ui.Choice>().FirstOrDefault(c => c.Value == g.LogLevel) ?? _logLevel.Items[0];
            _logTitles.Checked = g.LogTitles;
            _readTabs.Checked = g.ReadBrowserTabs;
            _checkUpdates.Checked = g.EffectiveUpdates.CheckAutomatically;
            _prereleases.Checked = g.EffectiveUpdates.IncludePrereleases;
            _calls.SelectedItem = _calls.Items.Cast<Ui.Choice>().First(c => c.Value == g.CallPolicy.ToString());
            SetValue(_idleGrace, g.IdleGraceSeconds);
            SetValue(_minDwell, g.MinDwellMs);
            SetValue(_gameLoading, g.GameLoadingMaxMs);
            SetValue(_startupGrace, g.StartupGraceSeconds);
            SetValue(_checkEvery, g.CheckIntervalSeconds);
            SetValue(_attack, g.Detection.AttackMs);
            SetValue(_release, g.Detection.ReleaseMs);
            SetValue(_gameRelease, g.Detection.GameReleaseMs);
        }
        finally
        {
            _binding = false;
        }
    }

    private static void SetValue(NumericUpDown n, int v) => n.Value = Math.Clamp(v, n.Minimum, n.Maximum);

    private void BrowseFx()
    {
        using var dlg = new OpenFileDialog
        {
            Title = Loc.T("General.FindFx"),
            Filter = "FxSound|FxSound.exe|" + Loc.T("General.Programs") + "|*.exe",
            FileName = "FxSound.exe",
        };
        try { dlg.InitialDirectory = Path.GetDirectoryName(_fxPath.Text.Length > 0 ? _fxPath.Text : DefaultConfig.FxSoundPath); }
        catch (ArgumentException) { }
        if (dlg.ShowDialog(this) == DialogResult.OK) _fxPath.Text = dlg.FileName;
    }
}
