using FxAuto.App;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Presets;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>Presets: FxSound's presets (user and built-in), which ones are modified or missing, where FxAuto uses
/// them, volume leveling, and the preset operations (REQ-107, REQ-110 banner).</summary>
internal sealed class PresetsPage : SettingsPageBase
{
    private sealed record Row(string Name, bool BuiltIn, bool Missing);

    private readonly ThemedListView _list = new() { AccessibleName = Loc.T("Presets.Accessible") };
    private readonly Label _summary = Ui.Text("");
    private readonly Button _duplicate, _rename, _delete, _edit;
    private readonly NumericUpDown _leveling = Ui.Number(0, 12, 0, 1, 0.5m, Loc.T("Presets.LevelingAccessible"));
    private readonly Label _levelingLabel = Ui.Text(Loc.T("Presets.Leveling"));
    private readonly ToolTip _tips = new();
    private readonly List<Row> _rows = [];
    private bool _binding;
    private string? _selected;

    public PresetsPage(SettingsForm window) : base(window)
    {
        _list.SetColumns((Loc.T("Presets.Col.Name"), 190), (Loc.T("Presets.Col.Type"), 80), (Loc.T("Presets.Col.Leveling"), 80), (Loc.T("Presets.Col.UsedBy"), 220), (Loc.T("Presets.Col.State"), 110));
        _list.SelectedIndexChanged += (_, _) => OnSelection();
        _list.DoubleClick += (_, _) => { if (_edit?.Enabled == true) _ = Edit(); };

        _duplicate = Ui.Button(Loc.T("Presets.DuplicateButton"), () => _ = Duplicate());
        _rename = Ui.Button(Loc.T("Presets.RenameButton"), () => _ = Rename());
        _delete = Ui.Button(Loc.T("Presets.DeleteButton"), () => _ = Delete());
        _edit = Ui.Button(Loc.T("Edit.Title"), () => _ = Edit());
        var refresh = Ui.Button(Loc.T("Presets.Refresh"), () => _ = Window.RefreshFxAsync(), Loc.T("Presets.RefreshAccessible"));

        var head = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Theme.S2) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _summary.Anchor = AnchorStyles.Left;
        head.Controls.Add(_summary, 0, 0);
        refresh.Margin = Padding.Empty;
        head.Controls.Add(refresh, 1, 0);

        var levelRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, Theme.S3, 0, 0) };
        _levelingLabel.Margin = new Padding(0, 6, Theme.S2, 0);
        var db = Ui.Text("dB");
        db.Margin = new Padding(Theme.S1, 6, Theme.S3, 0);
        levelRow.Controls.AddRange([_levelingLabel, _leveling, db]);
        _leveling.ValueChanged += (_, _) => SetLeveling();

        var card = FillCard(head, _list,
            Ui.Row(_duplicate, _rename, _delete, _edit),
            levelRow,
            Ui.Hint(Loc.T("Presets.LevelingHint")));
        var stack = Stack(1, 0);
        stack.Controls.Add(card, 0, 0);
        Controls.Add(stack);
        _tips.SetToolTip(_rename, Loc.T("Presets.BuiltInNoRename"));
    }

    public override SettingsPage Page => SettingsPage.Presets;

    public override string Heading => Loc.T("Nav.Presets");

    public override string? Subheading => Loc.T("Presets.Subheading");

    public override void Bind()
    {
        _binding = true;
        try
        {
            var fx = Window.Fx;
            _rows.Clear();
            if (fx is not null)
            {
                _rows.AddRange(fx.UserPresets.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => new Row(p, false, false)));
                _rows.AddRange(fx.BuiltInPresets.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => new Row(p, true, false)));
                foreach (var p in ConfigEdits.ReferencedPresets(Working))
                    if (!_rows.Any(r => string.Equals(r.Name, p, StringComparison.Ordinal))) _rows.Add(new Row(p, false, true));
            }
            var max = PresetNameRules.DefaultMaxUserPresets;
            _summary.Text = fx is null ? Loc.T("Presets.Reading")
                : Loc.F("Presets.Summary", fx.UserPresets.Count, max, fx.BuiltInPresets.Count);

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var r in _rows) _list.Items.Add(ItemFor(r));
            _list.EndUpdateAndFit();
            var i = _selected is null ? -1 : _rows.FindIndex(r => r.Name == _selected);
            if (i < 0 && _rows.Count > 0) i = 0;
            if (i >= 0) { _list.Items[i].Selected = true; _list.Items[i].Focused = true; }
        }
        finally
        {
            _binding = false;
        }
        OnSelection();
    }

    private ListViewItem ItemFor(Row r)
    {
        var fx = Window.Fx;
        var item = new ListViewItem(r.Name) { UseItemStyleForSubItems = false, Tag = r };
        item.SubItems.Add(r.Missing ? "—" : r.BuiltIn ? Loc.T("Presets.Type.BuiltIn") : Loc.T("Presets.Type.User"));
        item.SubItems.Add(Working.Presets.TryGetValue(r.Name, out var meta) ? $"{meta.VolumeLeveling:0.#} dB" : "—");
        item.SubItems.Add(UsedBy(r.Name));
        var modified = fx is not null && fx.ModifiedByPreset.TryGetValue(r.Name, out var m) && m;
        var active = fx is not null && fx.SelectedPreset == r.Name;
        var state = r.Missing ? Loc.T("Presets.State.Missing") : modified ? Loc.T("Presets.State.Modified") : active ? Loc.T("Presets.State.Active") : "";
        var st = item.SubItems.Add(state);
        st.ForeColor = r.Missing ? P.Error : modified ? P.Warning : active ? P.Success : P.Text;
        if (r.Missing) item.ForeColor = P.TextSecondary;
        return item;
    }

    /// <summary>"G435 headset: Music, Video; Desk speakers: default".</summary>
    private string UsedBy(string preset)
    {
        var parts = new List<string>();
        foreach (var d in Working.Devices)
        {
            var uses = new List<string>();
            if (d.DefaultPreset == preset) uses.Add(Loc.T("Presets.UsedDefault"));
            foreach (var a in Working.Assignments.Where(a => a.Device == d.Id && a.Preset == preset))
            {
                if (a.Category is not null) uses.Add(Categories.DisplayName(a.Category));
                else if (a.Source is not null) uses.Add(Working.Sources.FirstOrDefault(s => s.Id == a.Source)?.Name ?? a.Source);
            }
            if (uses.Count > 0) parts.Add($"{d.DisplayName}: {string.Join(", ", uses)}");
        }
        return parts.Count == 0 ? "—" : string.Join("; ", parts);
    }

    private Row? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Row : null;

    private void OnSelection()
    {
        if (_binding) return;
        var r = Selected;
        _selected = r?.Name;
        var present = r is { Missing: false };
        var user = present && !r!.BuiltIn;
        var busyEditing = Window.EditingPreset is not null;
        _duplicate.Enabled = present && Window.Fx is not null && !busyEditing;
        _rename.Enabled = user && !busyEditing;
        _delete.Enabled = user && !busyEditing;
        _edit.Enabled = present && !busyEditing;
        _binding = true;
        try
        {
            _leveling.Enabled = r is not null;
            _levelingLabel.Text = r is null ? Loc.T("Presets.Leveling") : Loc.F("Presets.LevelingFor", r.Name);
            _leveling.Value = r is not null && Working.Presets.TryGetValue(r.Name, out var meta) ? (decimal)Math.Clamp(meta.VolumeLeveling, 0, 12) : 0;
        }
        finally { _binding = false; }
    }

    private void SetLeveling()
    {
        if (_binding || Selected is not { } r) return;
        var value = (double)_leveling.Value;
        Window.Edit(c =>
        {
            var presets = new Dictionary<string, PresetMeta>(c.Presets, StringComparer.Ordinal);
            presets[r.Name] = presets.TryGetValue(r.Name, out var old) ? old with { VolumeLeveling = value } : new PresetMeta(value, null);
            return c with { Presets = presets };
        });
        if (_list.SelectedItems.Count > 0) _list.SelectedItems[0].SubItems[2].Text = $"{value:0.#} dB";
    }

    private async Task Duplicate()
    {
        if (Selected is not { } r || Window.Fx is not { } fx) return;
        using var dlg = new DuplicateDialog(r.Name, Working, fx);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var result = await Window.RunPresetOpAsync(new PresetOpRequest(PresetOpKind.Duplicate, r.Name, dlg.NewName),
            Loc.T("Duplicate.Title"), Loc.F("Presets.Duplicate.Confirm", dlg.NewName, r.Name), Loc.T("Duplicate.Ok"));
        if (result is { Ok: true } && dlg.AssignDevice is { } device && dlg.AssignCategory is { } category)
        {
            Window.Edit(c => c with
            {
                Assignments = [.. c.Assignments.Where(a => !(a.Device == device && a.Category == category)),
                    new Assignment(device, category, null, dlg.NewName)],
            });
            Window.ShowMessage("assign", Tone.Info, Loc.F("Presets.Assigned", dlg.NewName, Categories.DisplayName(category), Working.Devices.FirstOrDefault(d => d.Id == device)?.DisplayName ?? device));
        }
        _selected = dlg.NewName;
        Bind();
    }

    private async Task Rename()
    {
        if (Selected is not { BuiltIn: false, Missing: false } r || Window.Fx is not { } fx) return;
        using var dlg = new TextPromptDialog(Loc.T("Presets.Rename.Title"), Loc.T("Edit.SaveAsNew.Label"), r.Name, Loc.T("Presets.Rename.Ok"),
            n => PresetNameRules.Validate(n, fx, PresetNameRules.DefaultMaxUserPresets, renamingFrom: r.Name).ShownError,
            Loc.T("Presets.Rename.Hint"));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var to = dlg.Value;
        await Window.RunPresetOpAsync(new PresetOpRequest(PresetOpKind.Rename, r.Name, to),
            Loc.T("Presets.Rename.Title"), Loc.F("Presets.Rename.Confirm", r.Name, to), Loc.T("Presets.Rename.Ok"));
        _selected = to;
        Bind();
    }

    private async Task Delete()
    {
        if (Selected is not { BuiltIn: false, Missing: false } r) return;
        var used = UsedBy(r.Name);
        var defaults = Working.Devices.Where(d => d.DefaultPreset == r.Name).Select(d => d.DisplayName).ToList();
        if (PresetOperations.CheckDelete(Working, r.Name, null) is not null && defaults.Count > 0)
        {
            // a device default cannot be left empty: FxAuto needs the replacement, and every use moves to it
            var others = PresetChoices().Where(p => p != r.Name && !_rows.Any(x => x.Name == p && x.Missing)).ToList();
            using var pick = new ChoiceDialog(Loc.T("Presets.Delete.Title"),
                Loc.F("Presets.Delete.Replace", r.Name, string.Join(", ", defaults), used),
                Loc.T("Presets.Delete.ReplaceWith"), others, Loc.T("Presets.Delete.Ok"), Window.GameRunning ? SettingsForm.GameRunningWarning : null, destructive: true);
            if (pick.ShowDialog(this) != DialogResult.OK || pick.Value is not { } replacement) return;
            await Window.RunPresetOpAsync(new PresetOpRequest(PresetOpKind.Delete, r.Name, replacement), null, null, null);
        }
        else
        {
            var message = used == "—"
                ? Loc.F("Presets.Delete.Confirm", r.Name)
                : Loc.F("Presets.Delete.ConfirmUsed", r.Name, used);
            await Window.RunPresetOpAsync(new PresetOpRequest(PresetOpKind.Delete, r.Name), Loc.T("Presets.Delete.Title"), message, Loc.T("Presets.Delete.Ok"));
        }
        _selected = null;
        Bind();
    }

    private async Task Edit()
    {
        if (Selected is not { Missing: false } r) return;
        await Window.BeginEditAsync(r.Name);
        OnSelection();
    }

    public override void FocusItem(string id)
    {
        var i = _rows.FindIndex(r => r.Name == id || id.Contains($"\"{r.Name}\"", StringComparison.Ordinal));
        if (i < 0) return;
        _list.SelectedItems.Clear();
        _list.Items[i].Selected = true;
        _list.EnsureVisible(i);
        _list.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
