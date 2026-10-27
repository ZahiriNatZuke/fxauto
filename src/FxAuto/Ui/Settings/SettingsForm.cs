using FxAuto.App;
using FxAuto.Core;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.FxSound;
using FxAuto.Core.Presets;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>FxAuto settings window (REQ-109). Works on a copy of the live config: Apply validates (errors block,
/// warnings are shown) and hands it to the host; a stale apply rebases per block and shows what changed. Preset
/// operations run at once, with confirmation, and rebase onto the config they return. Created on open, disposed on
/// close; never activates itself (only the opener does, when the user asks for it).</summary>
internal sealed class SettingsForm : Form
{
    private static readonly (SettingsPage Page, string Glyph, string Key)[] Sections =
    [
        (SettingsPage.Status, Theme.IconStatus, "Nav.Status"),
        (SettingsPage.Devices, Theme.IconDevices, "Nav.Devices"),
        (SettingsPage.Presets, Theme.IconPresets, "Nav.Presets"),
        (SettingsPage.Sources, Theme.IconSources, "Nav.Sources"),
        (SettingsPage.Categories, Theme.IconCategories, "Nav.Categories"),
        (SettingsPage.General, Theme.IconGeneral, "Nav.General"),
        (SettingsPage.About, Theme.IconAbout, "Nav.About"),
    ];

    private readonly ISettingsHost _host;
    private readonly WorkingCopy _copy;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<SettingsPage, SettingsPageBase> _pages = [];
    private readonly NavList _nav = new();
    private readonly Label _title = new() { AutoSize = true, Font = Theme.Title, Margin = new Padding(0, 0, 0, Theme.S1), UseMnemonic = false };
    private readonly Label _subtitle = new() { AutoSize = true, Font = Theme.Caption, Margin = new Padding(0, 0, 0, Theme.S3), UseMnemonic = false };
    private readonly FlowLayoutPanel _messages = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, AutoScroll = true };
    private readonly EditInFxSoundBar _editBar;
    private readonly Label _footer = new() { AutoSize = true, Font = Theme.Body, Margin = new Padding(0, 9, Theme.S3, 0), Tag = "keep-color", UseMnemonic = false };
    private readonly AccentButton _apply = new() { Text = Loc.T("Form.Apply"), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(96, 32), Padding = new Padding(Theme.S2, 0, Theme.S2, 0), Font = Theme.Body, Margin = new Padding(0, 0, Theme.S2, 0) };
    private readonly Button _cancel;
    private readonly Dictionary<string, InfoBar> _messageByKey = [];
    private SettingsPageBase? _current;
    private bool _busy;
    private bool _closingConfirmed;
    private IReadOnlyList<ConfigIssue> _issues = [];
    private string? _editingPreset;
    private readonly string _builtLanguage;

    /// <summary>The applied settings changed the language or the colours: the opener closes this window and opens a
    /// new one on <see cref="SettingsPage"/> (texts and palette are read when a window is built).</summary>
    public event Action<SettingsPage>? ReopenRequested;

    /// <summary>Closes without asking (the working copy is clean after Apply); used by the opener to reopen.</summary>
    public void CloseForReopen()
    {
        _closingConfirmed = true;
        Close();
    }

    public SettingsForm(ISettingsHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _copy = new WorkingCopy(host.Config);
        Palette = Theme.Current;
        _builtLanguage = Loc.Language;
        _cancel = Ui.Button(Loc.T("Form.Close"), OnCancel);
        _cancel.Margin = Padding.Empty;
        _editBar = new EditInFxSoundBar(this) { Visible = false };

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Loc.T("Form.Title");
        Font = Theme.Body;
        MinimumSize = new Size(860, 560);
        Size = new Size(1000, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Window;
        KeyPreview = true;
        BackColor = Palette.Window;
        ForeColor = Palette.Text;
        BuildLayout();
        ResumeLayout(false);
        PerformLayout();

        _host.StatusChanged += OnHostStatus;
        _host.ConfigChanged += OnHostConfig;
        Revalidate();
    }

    public ISettingsHost Host => _host;

    public Palette Palette { get; }

    /// <summary>The working copy every page edits.</summary>
    public FxAutoConfig Working => _copy.Working;

    /// <summary>Last FxSound status read (preset list, modified flags); null until the first read answers.</summary>
    public FxStatus? Fx { get; private set; }

    /// <summary>Validation of the working copy (against <see cref="Fx"/> when known).</summary>
    public IReadOnlyList<ConfigIssue> WorkingIssues => _issues;

    public bool IsDirty => _copy.IsDirty;

    /// <summary>Version of the live config the working copy is based on.</summary>
    public long BaseVersion => _copy.Base.Version;

    /// <summary>Keys of the messages shown above the page (preview checks).</summary>
    public IReadOnlyCollection<string> MessageKeys => _messageByKey.Keys;

    /// <summary>Bars above the page now: messages plus the edit bar (at most <see cref="MaxBars"/>).</summary>
    public int VisibleBars => _messageByKey.Count + (_editBar.Visible ? 1 : 0);

    /// <summary>Apply is possible now (something changed and nothing blocks it).</summary>
    public bool CanApply => _apply.Enabled;

    /// <summary>Runs Apply as if the button was pressed (keyboard shortcut and the preview tool).</summary>
    public Task ApplyNowAsync() => ApplyAsync();

    public SettingsPage CurrentPage => _current?.Page ?? SettingsPage.Status;

    /// <summary>A game is playing now: preset operations warn first (the sound changes while FxSound works).</summary>
    public bool GameRunning => _host.Status.Playing.Any(p => p.Playing && Categories.IsGame(p.Category));

    protected override bool ShowWithoutActivation => UiFlags.NoActivate;

    // ───────────────────────── layout ─────────────────────────

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 216));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // navigation pane
        var navPane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = new Padding(Theme.S2, Theme.S4, Theme.S2, Theme.S4) };
        navPane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        navPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var app = new Label { Text = "FxAuto", AutoSize = true, Font = Theme.BodyStrong, Margin = new Padding(Theme.S3, 0, 0, Theme.S4), UseMnemonic = false };
        navPane.Controls.Add(app, 0, 0);
        foreach (var s in Sections) _nav.Items.Add(new NavList.Item(s.Glyph, Loc.T(s.Key)));
        _nav.Dock = DockStyle.Fill;
        _nav.Margin = Padding.Empty;
        _nav.SelectedIndexChanged += (_, _) =>
        {
            if (_nav.SelectedIndex >= 0) SwitchTo(Sections[_nav.SelectedIndex].Page, null);
        };
        navPane.Controls.Add(_nav, 0, 1);
        root.Controls.Add(navPane, 0, 0);

        // content pane: edit bar, heading, messages, page, footer
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty, Padding = new Padding(Theme.S5, Theme.S4, Theme.S5, Theme.S3) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // edit bar
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // title
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // subtitle
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // messages
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // page
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // footer
        _messages.Controls.Add(_editBar); // pinned first in the message stack
        content.Controls.Add(_title, 0, 1);
        content.Controls.Add(_subtitle, 0, 2);
        content.Controls.Add(_messages, 0, 3);
        content.Controls.Add(_pageHost, 0, 4);
        _messages.SizeChanged += (_, _) => FitMessages();
        content.SizeChanged += (_, _) => FitMessages();

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, Theme.S3, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footer.Anchor = AnchorStyles.Left;
        footer.Controls.Add(_footer, 0, 0);
        footer.Controls.Add(_apply, 1, 0);
        footer.Controls.Add(_cancel, 2, 0);
        _apply.Click += async (_, _) => await ApplyAsync();
        content.Controls.Add(footer, 0, 5);
        root.Controls.Add(content, 1, 0);
        Controls.Add(root);
        CancelButton = _cancel;
    }

    private void FitMessages()
    {
        var w = _messages.Parent is { } p ? p.ClientSize.Width - p.Padding.Horizontal : _messages.Width;
        foreach (Control c in _messages.Controls) if (c.Width != w) c.Width = w;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiNative.TitleBar(this, Palette.Dark);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Theme.Apply(this, Palette);
        if (_current is null) ShowPage(SettingsPage.Status, null);
        UpdateEditBar();
        UpdateSaveBlocked();
        _ = RefreshFxAsync();
    }

    // ───────────────────────── navigation ─────────────────────────

    /// <summary>Shows a page (and the item <paramref name="focusId"/> on it). UI thread.</summary>
    public void ShowPage(SettingsPage page, string? focusId)
    {
        var index = Array.FindIndex(Sections, s => s.Page == page);
        if (_nav.SelectedIndex != index)
        {
            _nav.SelectedIndex = index; // raises SwitchTo
        }
        else if (_current?.Page != page)
        {
            SwitchTo(page, null);
        }
        if (focusId is not null) _current?.FocusItem(focusId);
    }

    private void SwitchTo(SettingsPage page, string? focusId)
    {
        if (!_pages.TryGetValue(page, out var p))
        {
            p = page switch
            {
                SettingsPage.Status => new StatusPage(this),
                SettingsPage.Devices => new DevicesPage(this),
                SettingsPage.Presets => new PresetsPage(this),
                SettingsPage.Sources => new SourcesPage(this),
                SettingsPage.Categories => new CategoriesPage(this),
                SettingsPage.About => new AboutPage(this),
                _ => new GeneralPage(this),
            };
            _pages[page] = p;
            Theme.Apply(p, Palette);
            p.BackColor = Palette.Window;
        }
        _pageHost.SuspendLayout();
        if (_current is not null && _current != p) _pageHost.Controls.Remove(_current);
        if (!_pageHost.Controls.Contains(p)) _pageHost.Controls.Add(p);
        _current = p;
        _title.Text = p.Heading;
        _subtitle.Text = p.Subheading ?? "";
        _subtitle.Visible = p.Subheading is not null;
        p.Bind();
        Theme.Apply(p, Palette);
        _pageHost.ResumeLayout(true);
        if (focusId is not null) p.FocusItem(focusId);
    }

    /// <summary>Called by the opener when the user opens the window again.</summary>
    public void BringToFrontByUser()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    // ───────────────────────── working copy ─────────────────────────

    /// <summary>Changes the working copy (not applied until Apply).</summary>
    public void Edit(Func<FxAutoConfig, FxAutoConfig> change)
    {
        _copy.Edit(change);
        Revalidate();
    }

    private void Revalidate()
    {
        _issues = ConfigValidator.Validate(_copy.Working, Fx);
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        var dirty = _copy.Dirty;
        var errors = _issues.Count(i => i.Level == IssueLevel.Error);
        var warnings = _issues.Count(i => i.Level == IssueLevel.Warning);
        _cancel.Text = dirty.Count > 0 ? Loc.T("Form.Cancel") : Loc.T("Form.Close");
        _cancel.AccessibleDescription = dirty.Count > 0 ? Loc.T("Form.CancelHint") : Loc.T("Form.CloseHint");
        var blocked = SaveBlocked;
        _apply.Enabled = !_busy && dirty.Count > 0 && errors == 0 && blocked is null;
        if (blocked is not null && !_busy)
        {
            _footer.Text = Loc.T("Form.Footer.Blocked");
            _footer.ForeColor = Palette.Error;
        }
        else if (_busy)
        {
            _footer.Text = Loc.T("Form.Footer.Busy");
            _footer.ForeColor = Palette.TextSecondary;
        }
        else if (dirty.Count > 0 && errors > 0)
        {
            _footer.Text = errors == 1 ? Loc.T("Form.Footer.OneError") : Loc.F("Form.Footer.Errors", errors);
            _footer.ForeColor = Palette.Error;
        }
        else if (dirty.Count > 0)
        {
            var names = string.Join(", ", dirty.Select(WorkingCopy.Name));
            _footer.Text = warnings == 0 ? Loc.F("Form.Footer.Dirty", names)
                : warnings == 1 ? Loc.F("Form.Footer.DirtyOneWarning", names) : Loc.F("Form.Footer.DirtyWarnings", names, warnings);
            _footer.ForeColor = Palette.Text;
        }
        else
        {
            _footer.Text = Loc.T("Form.Footer.Clean");
            _footer.ForeColor = Palette.TextSecondary;
        }
        _footer.AccessibleName = _footer.Text;
    }

    private async Task ApplyAsync(bool replacing = false)
    {
        if (_busy || (!replacing && (!_copy.IsDirty || SaveBlocked is not null))) return;
        Revalidate();
        var errors = _issues.Where(i => i.Level == IssueLevel.Error).ToList();
        if (errors.Count > 0)
        {
            ShowMessage("apply", Tone.Error, errors.Count > 1 ? Loc.F("Form.NotAppliedMore", errors[0].Shown, errors.Count - 1)
                : Loc.F("Form.NotApplied", errors[0].Shown),
                (Loc.T("Form.ShowAll"), () => ShowPage(SettingsPage.Status, "issues")));
            return;
        }
        var startWithWindows = _copy.Working.General.StartWithWindows;
        SetBusy(true);
        ApplyConfigResult r;
        try
        {
            r = replacing
                ? await _host.ApplyConfigReplacingAsync(_copy.Working, _copy.Base.Version)
                : await _host.ApplyConfigAsync(_copy.Working, _copy.Base.Version);
        }
        finally
        {
            SetBusy(false);
        }
        if (IsDisposed) return;
        if (r.Applied)
        {
            _copy.Reset(r.Current);
            DismissMessage("rebase");
            DismissMessage("blocked");
            if (_host.StartWithWindows != startWithWindows)
            {
                try
                {
                    _host.SetStartWithWindows(startWithWindows);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
                {
                    ShowMessage("startup", Tone.Error, Loc.F("Form.StartupFailed", ex.Message));
                }
            }
            UiPreferences.Apply(r.Current.Config.General);
            if (Loc.Language != _builtLanguage || Theme.Current.Dark != Palette.Dark)
            {
                var page = CurrentPage;
                BeginInvoke(() => ReopenRequested?.Invoke(page));
                return;
            }
            var warn = r.Issues.Count(i => i.Level == IssueLevel.Warning);
            ShowMessage("apply", warn > 0 ? Tone.Warning : Tone.Success,
                warn == 0 ? Loc.T("Form.Applied") : warn == 1 ? Loc.T("Form.AppliedOneWarning") : Loc.F("Form.AppliedWarnings", warn),
                warn > 0 ? (Loc.T("Form.ShowWarnings"), () => ShowPage(SettingsPage.Status, "issues")) : default);
        }
        else if (r.Issues.Any(IsStale))
        {
            var rb = _copy.Rebase(r.Current);
            DismissMessage("apply");
            ShowRebase(rb, Loc.T("Form.Rebase.Stale"), askAgain: true);
        }
        else
        {
            var first = r.Issues.FirstOrDefault(i => i.Level == IssueLevel.Error);
            ShowMessage("apply", Tone.Error, Loc.F("Form.NotApplied", first?.Shown ?? Loc.T("Form.Rejected")),
                (Loc.T("Form.ShowAll"), () => ShowPage(SettingsPage.Status, "issues")));
            _issues = r.Issues;
        }
        Revalidate();
        _current?.Bind();
    }

    private string? _blockedShown;

    /// <summary>Why saving is refused now (StatusSnapshot.ConfigSaveBlocked), or null.</summary>
    private string? SaveBlocked => _host.Status.ConfigSaveBlocked;

    /// <summary><see cref="SaveBlocked"/> as shown (in the UI language).</summary>
    private string? SaveBlockedText => _host.Status.ConfigSaveBlocked is { } r ? Loc.Show(_host.Status.ConfigSaveBlockedText, r) : null;

    /// <summary>A read-only config (written by a newer FxAuto, or a read-only file) can never be saved from here; a
    /// protected one (unusable on disk) can be replaced after confirmation.</summary>
    private bool IsReadOnly => !_host.Status.ConfigSaveReplaceable;

    private void UpdateSaveBlocked()
    {
        var reason = SaveBlocked;
        if (reason == _blockedShown) return;
        _blockedShown = reason;
        if (reason is null)
        {
            DismissMessage("blocked");
        }
        else if (IsReadOnly)
        {
            ShowMessage("blocked", Tone.Error, Loc.F("Form.Blocked", SaveBlockedText));
        }
        else
        {
            ShowMessage("blocked", Tone.Error, Loc.F("Form.BlockedYet", SaveBlockedText),
                (Loc.T("Form.ReplaceAction"), () => _ = ReplaceConfigAsync()));
        }
        UpdateFooter();
    }

    /// <summary>Saves the working copy over a config.json FxAuto could not use (the user confirms first).</summary>
    private async Task ReplaceConfigAsync()
    {
        if (_busy || SaveBlocked is null || IsReadOnly) return;
        Revalidate();
        if (_issues.Any(i => i.Level == IssueLevel.Error))
        {
            ShowMessage("apply", Tone.Error, Loc.T("Form.FixFirst"),
                (Loc.T("Form.ShowAll"), () => ShowPage(SettingsPage.Status, "issues")));
            return;
        }
        if (!Confirm(Loc.T("Form.Replace.Title"), Loc.T("Form.Replace.Message"), Loc.T("Form.Replace.Ok"), destructive: true))
            return;
        await ApplyAsync(replacing: true);
    }

    private static bool IsStale(ConfigIssue i) => i.Level == IssueLevel.Error &&
        (string.Equals(i.Path, "stale", StringComparison.OrdinalIgnoreCase) || i.Message.Contains("stale", StringComparison.OrdinalIgnoreCase));

    private void ShowRebase(RebaseResult rb, string lead, bool askAgain)
    {
        var changed = rb.ChangedOutside.Count == 0 ? Loc.T("Form.Rebase.Nothing") : string.Join(", ", rb.ChangedOutside.Select(WorkingCopy.Name));
        var text = lead + " " + Loc.F("Form.Rebase.Updated", changed);
        if (rb.KeptLocal.Count > 0)
            text += " " + Loc.F("Form.Rebase.Kept", string.Join(", ", rb.KeptLocal.Select(WorkingCopy.Name)));
        if (askAgain) text += " " + Loc.T("Form.Rebase.AskAgain");
        ShowMessage("rebase", rb.KeptLocal.Count > 0 || askAgain ? Tone.Warning : Tone.Info, text);
    }

    private void OnCancel()
    {
        if (_copy.IsDirty)
        {
            _copy.Discard();
            Revalidate();
        }
        _closingConfirmed = true;
        Close();
    }

    // ───────────────────────── host events ─────────────────────────

    private void OnHostStatus(StatusSnapshot s)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnHostStatus(s)); return; }
        UpdateEditBar();
        UpdateSaveBlocked();
        _current?.OnStatusChanged();
    }

    private void OnHostConfig(VersionedConfig next)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnHostConfig(next)); return; }
        if (next.Version <= _copy.Base.Version) return;
        // our own Apply / preset operation: its awaited result resets or rebases with the same config
        if (_busy) return;
        var rb = _copy.Rebase(next);
        if (rb.ChangedOutside.Count > 0) ShowRebase(rb, Loc.T("Form.Rebase.Outside"), askAgain: false);
        Revalidate();
        _current?.Bind();
    }

    /// <summary>Re-reads FxSound's presets (status.json).</summary>
    public async Task RefreshFxAsync()
    {
        try
        {
            var fx = await _host.RefreshFxStatusAsync();
            if (IsDisposed) return;
            if (fx is not null) Fx = fx;
            else ShowMessage("fx", Tone.Warning, Loc.T("Form.FxNoAnswer"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (IsDisposed) return;
            ShowMessage("fx", Tone.Warning, Loc.F("Form.FxReadFailed", ex.Message));
        }
        Revalidate();
        _current?.Bind();
    }

    // ───────────────────────── preset operations ─────────────────────────

    /// <summary>Asks for confirmation (with the "A game is running" warning), runs the operation now and rebases
    /// onto the config it returns. Null when the user said no. <paramref name="title"/> null = the caller already
    /// confirmed with its own dialog.</summary>
    public async Task<PresetOpResult?> RunPresetOpAsync(PresetOpRequest req, string? title, string? message, string? okText,
        IEnumerable<string>? details = null)
    {
        if (_busy) return null;
        if (title is not null && !Confirm(title, message ?? "", okText ?? Loc.T("Common.Ok"), details, destructive: req.Kind == PresetOpKind.Delete))
            return null;
        SetBusy(true);
        try
        {
            var (result, config) = await _host.RunPresetOpAsync(req, _cts.Token);
            if (IsDisposed) return result;
            var rb = _copy.Rebase(config);
            if (rb.KeptLocal.Count > 0) ShowRebase(rb, Loc.T("Form.Rebase.PresetsChanged"), askAgain: false);
            if (result.Final is not null) Fx = result.Final;
            else await RefreshFxAsync();
            if (result.Ok)
            {
                ShowMessage("op", Tone.Success, OpDoneText(req));
            }
            else
            {
                var steps = result.Steps.Count > 0 ? " " + Loc.F("Form.Op.Steps", string.Join(" → ", result.Steps)) : "";
                ShowMessage("op", Tone.Error, Loc.F("Form.Op.Failed", OpName(req.Kind), result.ShownError ?? Loc.T("Form.Op.NotConfirmed")) + steps);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (!IsDisposed)
            {
                SetBusy(false);
                Revalidate();
                _current?.Bind();
            }
        }
    }

    private static string OpName(PresetOpKind k) => k switch
    {
        PresetOpKind.Duplicate => Loc.T("Form.Op.Duplicate"),
        PresetOpKind.Rename => Loc.T("Form.Op.Rename"),
        PresetOpKind.Delete => Loc.T("Form.Op.Delete"),
        PresetOpKind.SwitchOutput => Loc.T("Form.Op.SwitchOutput"),
        _ => Loc.T("Form.Op.Other"),
    };

    private static string OpDoneText(PresetOpRequest r) => r.Kind switch
    {
        PresetOpKind.Duplicate => Loc.F("Form.Op.Duplicated", r.Target, r.Source),
        PresetOpKind.Rename => Loc.F("Form.Op.Renamed", r.Source, r.Target),
        PresetOpKind.Delete => Loc.F("Form.Op.Deleted", r.Source),
        _ => Loc.T("Form.Op.Done"),
    };

    /// <summary>"Edit in FxSound": pauses automation on <paramref name="preset"/> until Done.</summary>
    public async Task BeginEditAsync(string preset)
    {
        if (_busy) return;
        if (!Confirm(Loc.T("Edit.Title"), Loc.F("Edit.Confirm", preset), Loc.T("Edit.Start")))
            return;
        SetBusy(true);
        try
        {
            var r = await _host.BeginEditAsync(preset);
            if (IsDisposed) return;
            if (r.Ok) _editingPreset = preset;
            else ShowMessage("op", Tone.Error, Loc.F("Edit.StartFailed", r.ShownError ?? Loc.T("Form.Op.NotConfirmed")));
        }
        finally
        {
            if (!IsDisposed) { SetBusy(false); UpdateEditBar(); }
        }
    }

    public async Task FinishEditAsync(EditOutcome outcome)
    {
        if (_busy) return;
        string? name = null;
        var preset = EditingPreset ?? Loc.T("Edit.ThePreset");
        if (outcome == EditOutcome.SaveAsNew)
        {
            using var dlg = new TextPromptDialog(Loc.T("Edit.SaveAsNew.Title"), Loc.T("Edit.SaveAsNew.Label"), preset + " (2)", Loc.T("Edit.SaveAsNew.Ok"),
                n => Fx is null ? null : PresetNameRules.Validate(n, Fx, PresetNameRules.DefaultMaxUserPresets).ShownError);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            name = dlg.Value;
        }
        else if (outcome == EditOutcome.Discard &&
                 !Confirm(Loc.T("Edit.Discard.Title"), Loc.F("Edit.Discard.Message", preset), Loc.T("Edit.Discard.Ok"), destructive: true))
        {
            return;
        }
        SetBusy(true);
        try
        {
            var (result, config) = await _host.FinishEditAsync(outcome, name);
            if (IsDisposed) return;
            _copy.Rebase(config);
            if (result.Final is not null) Fx = result.Final;
            if (result.Ok)
            {
                _editingPreset = null;
                ShowMessage("op", Tone.Success, outcome switch
                {
                    EditOutcome.Save => Loc.F("Edit.Saved", preset),
                    EditOutcome.SaveAsNew => Loc.F("Edit.SavedAs", name, preset),
                    _ => Loc.F("Edit.Discarded", preset),
                });
            }
            else
            {
                ShowMessage("op", Tone.Error, Loc.F("Edit.FinishFailed", result.ShownError ?? Loc.T("Form.Op.NotConfirmed")));
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                SetBusy(false);
                UpdateEditBar();
                Revalidate();
                _current?.Bind();
            }
        }
    }

    public string? EditingPreset => _editingPreset ?? _host.Status.EditingPreset
        ?? (_host.Status.Mode == Mode.Editing ? _host.Status.ExpectedPreset : null);

    private void UpdateEditBar()
    {
        if (_host.Status.Mode != Mode.Editing && !_busy) _editingPreset = null;
        var editing = _host.Status.Mode == Mode.Editing || _editingPreset is not null;
        _editBar.Preset = EditingPreset;
        if (_editBar.Visible != editing)
        {
            _editBar.Visible = editing;
            TrimMessages();
            BarsChanged?.Invoke();
        }
        _nav.Invalidate(); // the owner-drawn list loses its selected row when the layout above it changes
    }

    /// <summary>Bars above the page: messages plus the edit bar, at most <see cref="MaxBars"/>.</summary>
    public const int MaxBars = 2;

    /// <summary>Any bar is showing (pages hide their own banners then, to keep the cap).</summary>
    public bool HasBars => _messageByKey.Count > 0 || _editBar.Visible;

    /// <summary>The set of bars changed.</summary>
    public event Action? BarsChanged;

    private void TrimMessages(int room = 0)
    {
        while (_messageByKey.Count > 0 && _messageByKey.Count + (_editBar.Visible ? 1 : 0) + room > MaxBars)
            RemoveMessage(_messages.Controls.OfType<InfoBar>().Select(c => _messageByKey.First(kv => kv.Value == c).Key)
                .OrderBy(k => k == "blocked").First());
    }

    // ───────────────────────── messages and dialogs ─────────────────────────

    /// <summary>Shows (or replaces) the message with this key above the page.</summary>
    public void ShowMessage(string key, Tone tone, string text, (string Text, Action OnClick) action = default)
    {
        if (_messageByKey.Remove(key, out var old))
        {
            _messages.Controls.Remove(old);
            old.Dispose();
        }
        var bar = new InfoBar { Palette = Palette, Tone = tone, Dock = DockStyle.None, Width = Math.Max(200, _messages.ClientSize.Width) };
        bar.Message = text;
        if (action.Text is not null) bar.AddAction(action.Text, action.OnClick!);
        bar.AddAction(Loc.T("Common.Dismiss"), () => DismissMessage(key));
        // at most two bars above the page (the edit bar counts), so the page keeps its room; the oldest goes first
        TrimMessages(room: 1);
        _messageByKey[key] = bar;
        _messages.Controls.Add(bar);
        FitMessages();
        _nav.Invalidate();
        BarsChanged?.Invoke();
        if (tone is Tone.Success or Tone.Info)
        {
            // confirmations go away by themselves; problems stay until dismissed or fixed
            var timer = new System.Windows.Forms.Timer { Interval = tone == Tone.Success ? 8000 : 15000 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                if (_messageByKey.TryGetValue(key, out var current) && current == bar) DismissMessage(key);
            };
            bar.Disposed += (_, _) => timer.Dispose();
            timer.Start();
        }
    }

    public void DismissMessage(string key)
    {
        if (!RemoveMessage(key)) return;
        _nav.Invalidate();
        BarsChanged?.Invoke();
    }

    private bool RemoveMessage(string key)
    {
        if (!_messageByKey.Remove(key, out var bar)) return false;
        _messages.Controls.Remove(bar);
        bar.Dispose();
        return true;
    }

    /// <summary>Confirmation dialog. <paramref name="destructive"/>: Enter does not confirm and focus starts on Cancel.</summary>
    public bool Confirm(string title, string message, string okText, IEnumerable<string>? details = null, bool destructive = false)
    {
        var warning = GameRunning ? GameRunningWarning : null;
        using var dlg = new ConfirmDialog(title, message, okText, warning, details, destructive);
        return dlg.ShowDialog(this) == DialogResult.OK;
    }

    public static string GameRunningWarning => Loc.T("Form.GameRunning");

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _pageHost.Enabled = !busy;
        _editBar.Enabled = !busy;
        UseWaitCursor = busy;
        UpdateFooter();
    }

    // ───────────────────────── closing ─────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Control && e.KeyCode == Keys.S && _apply.Enabled)
        {
            e.Handled = true;
            _ = ApplyAsync();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            ShowMessage("busy", Tone.Info, Loc.T("Form.WaitBeforeClosing"));
            return;
        }
        if (!_closingConfirmed && e.CloseReason == CloseReason.UserClosing && _copy.IsDirty)
        {
            using var dlg = new ConfirmDialog(Loc.T("Form.DiscardOnClose.Title"),
                Loc.F("Form.DiscardOnClose.Message", string.Join(", ", _copy.Dirty.Select(WorkingCopy.Name))), Loc.T("Form.DiscardOnClose.Ok"), destructive: true);
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _host.StatusChanged -= OnHostStatus;
        _host.ConfigChanged -= OnHostConfig;
        _cts.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _host.StatusChanged -= OnHostStatus;
            _host.ConfigChanged -= OnHostConfig;
            _cts.Dispose();
            foreach (var p in _pages.Values) p.Dispose();
            _pages.Clear();
        }
        base.Dispose(disposing);
    }
}
