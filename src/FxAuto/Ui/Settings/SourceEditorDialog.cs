using System.Text;
using System.Text.RegularExpressions;
using FxAuto.App;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>Adds or edits a source (app, game, site, browser): how it is matched, its category and, optionally, a
/// specific preset per device (written as source assignments).</summary>
internal sealed class SourceEditorDialog : ThemedDialog
{
    private static string CategoryPreset => Loc.T("Editor.CategoryPreset");

    private readonly FxAutoConfig _config;
    private readonly SourceConfig? _original;
    private readonly ComboBox _kind = Ui.Combo(Enum.GetValues<SourceKind>().Select(k => new Ui.Choice(k.ToString(), KindName(k))), 200);
    private readonly TextBox _name = new() { Width = 360 };
    private readonly TextBox _exe = new() { Width = 360 };
    private readonly TextBox _aumid = new() { Width = 360 };
    private readonly TextBox _folder = new() { Width = 360 };
    private readonly TextBox _site = new() { Width = 360 };
    private readonly TextBox _regex = new() { Width = 360 };
    private readonly ComboBox _category = Ui.Combo(Categories.All.Select(c => new Ui.Choice(c, Categories.DisplayName(c))), 220);
    private readonly CheckBox _fallback = new() { Text = Loc.T("Editor.Fallback"), AutoSize = true };
    private readonly Dictionary<string, ComboBox> _overrides = [];
    private readonly Label _error = ErrorLabel();
    private readonly InfoBar _duplicate = new() { Tone = Tone.Warning, Visible = false, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Theme.S2) };
    // one grid for every field (match fields and per-device presets) with a fixed label column, so they all line up
    private readonly TableLayoutPanel _grid = Ui.Grid(LabelColumn);
    private readonly Dictionary<Control, (Label Label, Control Host)> _rows = [];

    private const int LabelColumn = 130;

    public SourceEditorDialog(FxAutoConfig config, IReadOnlyList<string> presets, SourceConfig? source, string? proposedFrom = null)
        : base(source is null || proposedFrom is not null ? Loc.T("Editor.AddTitle") : Loc.T("Editor.EditTitle"), Loc.T("Editor.Save"))
    {
        _config = config;
        _original = proposedFrom is null ? source : null;
        var s = source ?? new SourceConfig("", "", SourceKind.App, new SourceMatch([], [], [], [], null), Categories.Music, false, DefaultConfig.OriginUser);

        if (proposedFrom is not null)
        {
            var bar = new InfoBar
            {
                Tone = Tone.Warning,
                Message = Loc.F("Editor.Proposed", proposedFrom),
                Width = 560,
                Margin = new Padding(0, 0, 0, Theme.S3),
            };
            Add(bar);
        }

        AddRow(Loc.T("Editor.Kind"), _kind);
        AddRow(Loc.T("Editor.Name"), _name);
        AddRow(Loc.T("Editor.Programs"), _exe, Loc.T("Editor.ProgramsHint"));
        AddRow(Loc.T("Editor.AppId"), _aumid, Loc.T("Editor.AppIdHint"));
        AddRow(Loc.T("Editor.Folders"), _folder, Loc.T("Editor.FoldersHint"));
        AddRow(Loc.T("Editor.Site"), _site, Loc.T("Editor.SiteHint"));
        AddRow(Loc.T("Editor.Pattern"), _regex, Loc.T("Editor.PatternHint"));
        AddWide(_duplicate); // right under the match fields it is about
        AddRow(Loc.T("Field.Category"), _category);
        AddRow("", _fallback);

        if (config.Devices.Count > 0)
        {
            var title = Ui.SectionTitle(Loc.T("Editor.PresetTitle"));
            title.Margin = new Padding(0, Theme.S3, 0, Theme.S2);
            AddWide(title);
            foreach (var d in config.Devices)
            {
                var combo = Ui.Combo([CategoryPreset, .. presets], 300, Loc.F("Editor.PresetOn", d.DisplayName));
                var current = config.Assignments.FirstOrDefault(a => a.Device == d.Id && a.Source is not null && a.Source == s.Id && s.Id.Length > 0);
                combo.SelectedItem = current is not null && combo.Items.Contains(current.Preset) ? current.Preset : CategoryPreset;
                _overrides[d.Id] = combo;
                AddRow(d.DisplayName, combo);
            }
        }
        Add(_grid);
        Add(_error);

        _kind.SelectedItem = _kind.Items.Cast<Ui.Choice>().First(c => c.Value == s.Kind.ToString());
        _name.Text = s.Name;
        _exe.Text = string.Join(", ", s.Match.Exe);
        _aumid.Text = string.Join(", ", s.Match.Aumid);
        _folder.Text = string.Join(", ", s.Match.Folder);
        _site.Text = string.Join(", ", s.Match.Site);
        _regex.Text = s.Match.TitleRegex ?? "";
        _category.SelectedItem = _category.Items.Cast<Ui.Choice>().FirstOrDefault(c => c.Value == s.Category) ?? _category.Items[0];
        _fallback.Checked = s.Fallback;

        _kind.SelectedIndexChanged += (_, _) => { UpdateKind(); Check(); };
        foreach (var tb in new[] { _name, _exe, _aumid, _folder, _site, _regex }) tb.TextChanged += (_, _) => Check();
        _category.SelectedIndexChanged += (_, _) => Check();
        UpdateKind();
        Shown += (_, _) => { _name.Focus(); Check(); };
    }

    public SourceConfig? Result { get; private set; }

    /// <summary>Device id → preset for this source (null = remove the source assignment).</summary>
    public IReadOnlyDictionary<string, string?> Overrides =>
        _overrides.ToDictionary(kv => kv.Key, kv => kv.Value.SelectedItem as string is { } p && p != CategoryPreset ? p : null);

    public static string KindName(SourceKind k) => k switch
    {
        SourceKind.App => Loc.T("Kind.App"),
        SourceKind.Game => Loc.T("Kind.Game"),
        SourceKind.Site => Loc.T("Editor.KindSite"),
        _ => Loc.T("Kind.Browser"),
    };

    private void AddRow(string label, Control editor, string? hint = null) => _rows[editor] = Ui.AddField(_grid, label, editor, hint);

    /// <summary>A row across both columns of the grid.</summary>
    private void AddWide(Control c)
    {
        var row = _grid.RowCount;
        _grid.RowCount = row + 1;
        _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _grid.Controls.Add(c, 0, row);
        _grid.SetColumnSpan(c, 2);
    }

    private SourceKind Kind => Enum.Parse<SourceKind>(((Ui.Choice)_kind.SelectedItem!).Value);

    private void UpdateKind()
    {
        var k = Kind;
        Show(_exe, k is SourceKind.App or SourceKind.Game or SourceKind.Browser);
        Show(_aumid, k is SourceKind.App or SourceKind.Game);
        Show(_folder, k is SourceKind.Game or SourceKind.App);
        Show(_site, k == SourceKind.Site);
        Show(_regex, k is SourceKind.Site or SourceKind.Browser);
    }

    private void Show(Control editor, bool visible)
    {
        if (!_rows.TryGetValue(editor, out var row)) return;
        row.Host.Visible = visible;
        row.Label.Visible = visible;
    }

    private static List<string> Split(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private SourceConfig Build()
    {
        var k = Kind;
        var match = new SourceMatch(
            k is SourceKind.App or SourceKind.Game or SourceKind.Browser ? Split(_exe.Text) : [],
            k is SourceKind.App or SourceKind.Game ? Split(_aumid.Text) : [],
            k is SourceKind.Game or SourceKind.App ? Split(_folder.Text) : [],
            k == SourceKind.Site ? Split(_site.Text) : [],
            k is SourceKind.Site or SourceKind.Browser && _regex.Text.Trim().Length > 0 ? _regex.Text.Trim() : null);
        var category = ((Ui.Choice)_category.SelectedItem!).Value;
        var id = _original?.Id ?? UniqueId(_name.Text.Trim(), _config.Sources.Select(x => x.Id));
        var origin = _original?.Origin is { } o && o != DefaultConfig.OriginBuiltIn ? o : DefaultConfig.OriginUser;
        return new SourceConfig(id, _name.Text.Trim(), k, match, category, _fallback.Checked, origin);
    }

    private bool Check()
    {
        var s = Build();
        string? error = null;
        if (s.Name.Length == 0) error = Loc.T("Editor.NeedName");
        else if (s.Kind == SourceKind.Site && s.Match.Site.Count == 0) error = Loc.T("Editor.NeedSite");
        else if (s.Kind != SourceKind.Site && s.Match.Exe.Count == 0 && s.Match.Aumid.Count == 0 && s.Match.Folder.Count == 0)
            error = Loc.T("Editor.NeedMatch");
        else if (s.Match.TitleRegex is { } rx)
        {
            try { _ = new Regex(rx, RegexOptions.CultureInvariant, ConfigValidator.RegexTimeout); }
            catch (ArgumentException ex) { error = Loc.F("Editor.BadPattern", ex.Message); }
        }
        _error.Text = error ?? "";
        _error.ForeColor = P.Error;
        _error.Visible = error is not null;

        var dup = _config.Sources.FirstOrDefault(o => o.Id != s.Id && !o.Fallback &&
            (o.Match.Exe.Intersect(s.Match.Exe, StringComparer.OrdinalIgnoreCase).Any() && s.Kind != SourceKind.Site && o.Kind != SourceKind.Site
             || o.Kind == SourceKind.Site && s.Kind == SourceKind.Site && o.Match.Site.Intersect(s.Match.Site, StringComparer.OrdinalIgnoreCase).Any()
                && o.Match.TitleRegex == s.Match.TitleRegex));
        if (dup is not null)
            _duplicate.Message = Loc.F("Editor.Duplicate", dup.Name, Categories.DisplayName(dup.Category));
        _duplicate.Visible = dup is not null;
        OkButton.Enabled = error is null;
        return error is null;
    }

    protected override bool OnOk()
    {
        if (!Check()) return false;
        Result = Build();
        return true;
    }

    /// <summary>"YouTube Music" → "youtube-music", unique among <paramref name="taken"/>.</summary>
    public static string UniqueId(string name, IEnumerable<string> taken)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[^1] != '-' && ch < 128) sb.Append('-');
        }
        var baseId = sb.ToString().Trim('-');
        if (baseId.Length == 0) baseId = "source";
        var set = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = baseId;
        for (var n = 2; set.Contains(id); n++) id = $"{baseId}-{n}";
        return id;
    }

    /// <summary>Source proposed from a detected one: kind, match and category filled in.</summary>
    public static SourceConfig Propose(DetectedSource d)
    {
        var exe = d.Exe is null ? [] : new List<string> { d.Exe };
        if (d.Class.IsBrowser && d.Class.Site is { } site)
            return new SourceConfig("", site, SourceKind.Site, new SourceMatch([], [], [], [site], null),
                Categories.IsKnown(d.Class.Category) ? d.Class.Category : Categories.Video, false, DefaultConfig.OriginUser);
        if (d.Class.IsGame)
        {
            var folder = d.ExePath is null ? null : Path.GetDirectoryName(d.ExePath);
            return new SourceConfig("", d.Title ?? Path.GetFileNameWithoutExtension(d.Exe ?? "Game"), SourceKind.Game,
                new SourceMatch(exe, [], folder is null ? [] : [folder], [], null),
                Categories.IsGame(d.Class.Category) ? d.Class.Category : Categories.GameAction, false, DefaultConfig.OriginUser);
        }
        return new SourceConfig("", d.Title ?? Path.GetFileNameWithoutExtension(d.Exe ?? "App"), SourceKind.App,
            new SourceMatch(exe, d.Aumid is null ? [] : [d.Aumid], [], [], null),
            Categories.IsKnown(d.Class.Category) && d.Class.Category != Categories.Unknown ? d.Class.Category : Categories.Music, false, DefaultConfig.OriginUser);
    }
}

/// <summary>Picks one of the sources playing now ("Detect what is playing now").</summary>
internal sealed class PickPlayingDialog : ThemedDialog
{
    private readonly ThemedListView _list = new() { AccessibleName = Loc.T("Pick.Accessible"), Height = 200, Width = 560, Dock = DockStyle.None };
    private readonly IReadOnlyList<DetectedSource> _items;

    public PickPlayingDialog(IReadOnlyList<DetectedSource> items) : base(Loc.T("Pick.Title"), Loc.T("Pick.Next"))
    {
        _items = items;
        Add(Ui.Text(Loc.T("Pick.Intro"), wrap: true, maxWidth: 560));
        _list.SetColumns((Loc.T("Pick.Col.Playing"), 220), (Loc.T("Pick.Col.Program"), 160), (Loc.T("Pick.Col.Category"), 170));
        foreach (var d in items)
        {
            var item = new ListViewItem(d.Class.Site ?? d.Title ?? d.Exe ?? Loc.F("Status.Process", d.RootPid));
            item.SubItems.Add(d.Exe ?? "");
            item.SubItems.Add(Categories.DisplayName(d.Class.Category) + (d.Class.SourceId is null ? " " + Loc.T("Pick.NoSource") : ""));
            _list.Items.Add(item);
        }
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        _list.Margin = new Padding(0, Theme.S2, 0, 0);
        Content.Controls.Add(new InputFrame(_list) { Margin = new Padding(0, Theme.S2, 0, 0) });
        _list.DoubleClick += (_, _) => { if (OnOk()) { DialogResult = DialogResult.OK; Close(); } };
    }

    public DetectedSource? Picked { get; private set; }

    protected override bool OnOk()
    {
        if (_list.SelectedIndices.Count == 0) return false;
        Picked = _items[_list.SelectedIndices[0]];
        return true;
    }
}
