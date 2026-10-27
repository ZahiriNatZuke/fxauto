using FxAuto.App;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>Sources: apps, games and sites with their category; editor; "Detect what is playing now" (REQ-102,
/// REQ-103, REQ-109).</summary>
internal sealed class SourcesPage : SettingsPageBase
{
    /// <summary>focusId that starts the detect flow (tray "Create source from what's playing").</summary>
    public const string DetectPlayingId = "detect-playing";

    private readonly ThemedListView _list = new() { AccessibleName = Loc.T("Nav.Sources") };
    private readonly ComboBox _filter = Ui.Combo(
        [new Ui.Choice("", Loc.T("Sources.AllKinds")), new Ui.Choice(nameof(SourceKind.App), Loc.T("Sources.Apps")), new Ui.Choice(nameof(SourceKind.Game), Loc.T("Sources.Games")),
         new Ui.Choice(nameof(SourceKind.Site), Loc.T("Sources.Sites")), new Ui.Choice(nameof(SourceKind.Browser), Loc.T("Sources.Browsers"))], 160, Loc.T("Sources.ShowKind"));
    private readonly TextBox _search = new() { Width = 240, PlaceholderText = Loc.T("Sources.Search"), AccessibleName = Loc.T("Sources.Search") };
    private readonly Button _editBtn, _deleteBtn;
    private readonly List<int> _shown = [];
    private string? _selectedId;
    private bool _pendingDetect;

    public SourcesPage(SettingsForm window) : base(window)
    {
        _list.SetColumns((Loc.T("Sources.Col.Name"), 165), (Loc.T("Sources.Col.Kind"), 95), (Loc.T("Sources.Col.Match"), 145), (Loc.T("Sources.Col.Category"), 150), (Loc.T("Sources.Col.Notes"), 95));
        _list.SelectedIndexChanged += (_, _) => OnSelection();
        _list.DoubleClick += (_, _) => EditSelected();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) DeleteSelected(); else if (e.KeyCode == Keys.Enter) EditSelected(); };
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => Bind();
        _search.TextChanged += (_, _) => Bind();

        var top = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, Theme.S2) };
        _filter.Margin = new Padding(0, 0, Theme.S2, 0);
        _search.Margin = new Padding(0, 1, Theme.S4, 0);
        var detect = Ui.Button(Loc.T("Sources.Detect"), DetectPlaying);
        var searchField = Ui.Field(_search);
        searchField.Margin = new Padding(0, 1, Theme.S4, 0);
        top.Controls.AddRange([_filter, searchField, detect]);

        _editBtn = Ui.Button(Loc.T("Sources.Edit"), EditSelected);
        _deleteBtn = Ui.Button(Loc.T("Sources.Delete"), DeleteSelected);
        var actions = Ui.Row(
            Ui.Button(Loc.T("Sources.AddApp"), () => Add(SourceKind.App)),
            Ui.Button(Loc.T("Sources.AddGame"), () => Add(SourceKind.Game)),
            Ui.Button(Loc.T("Sources.AddSite"), () => Add(SourceKind.Site)),
            _editBtn, _deleteBtn);

        var lists = Ui.Row(
            Ui.Button(Loc.T("Sources.IgnoredButton"), () => EditList(Loc.T("Sources.Ignored"), Loc.T("Sources.IgnoredHint"),
                c => c.IgnoredApps, (c, l) => c with { IgnoredApps = l })),
            Ui.Button(Loc.T("Sources.LaunchersButton"), () => EditList(Loc.T("Sources.Launchers"), Loc.T("Sources.LaunchersHint"),
                c => c.Launchers, (c, l) => c with { Launchers = l })),
            Ui.Button(Loc.T("Sources.ExclusionsButton"), () => EditList(Loc.T("Sources.Exclusions"), Loc.T("Sources.ExclusionsHint"),
                c => c.GameFolderExclusions, (c, l) => c with { GameFolderExclusions = l })));

        Controls.Add(Wrap(FillCard(top, _list, actions, lists)));
    }

    private static TableLayoutPanel Wrap(Control c)
    {
        var s = Stack(1, 0);
        s.Controls.Add(c, 0, 0);
        return s;
    }

    public override SettingsPage Page => SettingsPage.Sources;

    public override string Heading => Loc.T("Nav.Sources");

    public override string? Subheading => Loc.T("Sources.Subheading");

    public override void Bind()
    {
        var kind = (_filter.SelectedItem as Ui.Choice)?.Value ?? "";
        var q = _search.Text.Trim();
        _shown.Clear();
        _list.BeginUpdate();
        _list.Items.Clear();
        for (var i = 0; i < Working.Sources.Count; i++)
        {
            var s = Working.Sources[i];
            if (kind.Length > 0 && s.Kind.ToString() != kind) continue;
            var match = MatchText(s);
            if (q.Length > 0 && !s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && !match.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !Categories.DisplayName(s.Category).Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            _shown.Add(i);
            var item = new ListViewItem(s.Name) { UseItemStyleForSubItems = false };
            item.SubItems.Add(Loc.T("Kind." + s.Kind));
            item.SubItems.Add(match);
            var cat = item.SubItems.Add(Categories.DisplayName(s.Category));
            if (s.Category == Categories.Hold) cat.ForeColor = P.Warning;
            var notes = new List<string>();
            if (s.Fallback) notes.Add(Loc.T("Sources.Note.Fallback"));
            if (Working.Assignments.Any(a => a.Source == s.Id)) notes.Add(Loc.T("Sources.Note.OwnPreset"));
            if (s.Origin == DefaultConfig.OriginBuiltIn) notes.Add(Loc.T("Sources.Note.BuiltIn"));
            var n = item.SubItems.Add(string.Join(", ", notes));
            n.ForeColor = P.TextSecondary;
            _list.Items.Add(item);
        }
        _list.EndUpdateAndFit();
        var sel = _selectedId is null ? -1 : _shown.FindIndex(i => Working.Sources[i].Id == _selectedId);
        if (sel >= 0) { _list.Items[sel].Selected = true; _list.EnsureVisible(sel); }
        OnSelection();
    }

    private static string MatchText(SourceConfig s)
    {
        var m = s.Match;
        var parts = new List<string>();
        if (m.Exe.Count > 0) parts.Add(string.Join(", ", m.Exe));
        if (m.Aumid.Count > 0) parts.Add(Loc.F("Sources.Match.App", string.Join(", ", m.Aumid)));
        if (m.Folder.Count > 0) parts.Add(Loc.F("Sources.Match.Folder", string.Join(", ", m.Folder)));
        if (m.Site.Count > 0) parts.Add(string.Join(", ", m.Site.Select(x => x == "*" ? Loc.T("Sources.Match.AnySite") : x)));
        if (m.TitleRegex is not null) parts.Add(Loc.T("Sources.Match.Pattern"));
        return string.Join(" · ", parts);
    }

    private int? SelectedIndex => _list.SelectedIndices.Count > 0 ? _shown[_list.SelectedIndices[0]] : null;

    private void OnSelection()
    {
        var i = SelectedIndex;
        if (i is { } k) _selectedId = Working.Sources[k].Id;
        _editBtn.Enabled = _deleteBtn.Enabled = i is not null;
    }

    private void Add(SourceKind kind)
    {
        var blank = new SourceConfig("", "", kind, new SourceMatch([], [], [], [], null),
            kind == SourceKind.Game ? Categories.GameAction : kind == SourceKind.Site ? Categories.Video : Categories.Music, false, DefaultConfig.OriginUser);
        using var dlg = new SourceEditorDialog(Working, PresetChoices(), blank, proposedFrom: null);
        Save(dlg, null);
    }

    private void EditSelected()
    {
        if (SelectedIndex is not { } i) return;
        using var dlg = new SourceEditorDialog(Working, PresetChoices(), Working.Sources[i]);
        Save(dlg, i);
    }

    private void Save(SourceEditorDialog dlg, int? index)
    {
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is not { } s) return;
        var overrides = dlg.Overrides;
        Window.Edit(c =>
        {
            var sources = c.Sources.ToList();
            if (index is { } i) sources[i] = s;
            else sources.Add(s);
            var assignments = c.Assignments.Where(a => a.Source != s.Id || !overrides.ContainsKey(a.Device)).ToList();
            foreach (var (device, preset) in overrides)
                if (preset is not null) assignments.Add(new Assignment(device, null, s.Id, preset));
            return c with { Sources = sources, Assignments = assignments };
        });
        _selectedId = s.Id;
        Bind();
    }

    private void DeleteSelected()
    {
        if (SelectedIndex is not { } i) return;
        var s = Working.Sources[i];
        var builtIn = s.Origin == DefaultConfig.OriginBuiltIn ? " " + Loc.T("Sources.Delete.BuiltIn") : "";
        if (!Window.Confirm(Loc.T("Sources.Delete.Title"), Loc.F("Sources.Delete.Message", s.Name) + builtIn, Loc.T("Sources.Delete"), destructive: true))
            return;
        Window.Edit(c => c with
        {
            Sources = c.Sources.Where(x => x.Id != s.Id).ToList(),
            Assignments = c.Assignments.Where(a => a.Source != s.Id).ToList(),
        });
        _selectedId = null;
        Bind();
    }

    private void DetectPlaying()
    {
        var found = Host.DetectPlayingNow();
        if (found.Count == 0)
        {
            Window.ShowMessage("detect", Tone.Info, Loc.T("Sources.NothingPlaying"));
            return;
        }
        DetectedSource? pick = found.Count == 1 ? found[0] : null;
        if (pick is null)
        {
            using var picker = new PickPlayingDialog(found);
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            pick = picker.Picked;
        }
        if (pick is null) return;
        var proposal = SourceEditorDialog.Propose(pick);
        using var dlg = new SourceEditorDialog(Working, PresetChoices(), proposal, proposedFrom: pick.Exe ?? Loc.T("Sources.AProgram"));
        Save(dlg, null);
    }

    private void EditList(string title, string explanation, Func<FxAutoConfig, IReadOnlyList<string>> get,
        Func<FxAutoConfig, IReadOnlyList<string>, FxAutoConfig> set)
    {
        using var dlg = new ListEditorDialog(title, explanation, get(Working));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var items = dlg.Items;
        Window.Edit(c => set(c, items));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_pendingDetect) return;
        _pendingDetect = false;
        BeginInvoke(DetectPlaying);
    }

    public override void FocusItem(string id)
    {
        if (id == DetectPlayingId)
        {
            if (IsHandleCreated) BeginInvoke(DetectPlaying); // after the window has shown
            else _pendingDetect = true;
            return;
        }
        var i = IndexFromPath(id, "sources");
        if (i is null) return;
        _filter.SelectedIndex = 0;
        _search.Text = "";
        var row = _shown.IndexOf(i.Value);
        if (row < 0) return;
        _list.SelectedItems.Clear();
        _list.Items[row].Selected = true;
        _list.EnsureVisible(row);
        _list.Focus();
    }
}
