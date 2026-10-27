using FxAuto.App;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>Categories: the category × device matrix. An empty cell uses the device's default preset (REQ-106).</summary>
internal sealed class CategoriesPage : SettingsPageBase
{
    private static string DeviceDefault => Loc.T("Cat.DeviceDefault");

    private readonly ThemedListView _matrix = new() { AccessibleName = Loc.T("Cat.MatrixAccessible") };
    private readonly Label _rowTitle = Ui.SectionTitle("");
    private readonly TableLayoutPanel _cells = Ui.Grid();
    private readonly Dictionary<string, ComboBox> _combos = [];
    private string _category = Categories.Music;
    private bool _binding;
    private int _deviceCount = -1;

    public CategoriesPage(SettingsForm window) : base(window)
    {
        _matrix.SelectedIndexChanged += (_, _) =>
        {
            if (_binding || _matrix.SelectedIndices.Count == 0) return;
            _category = Categories.All[_matrix.SelectedIndices[0]];
            BindRow();
        };
        _matrix.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.F2 && _combos.Count > 0) { _combos.Values.First().Focus(); e.Handled = true; }
        };
        var clear = Ui.Button(Loc.T("Cat.Clear"), ClearRow);
        var editor = CardOf(_rowTitle, Ui.Hint(Loc.T("Cat.EditorHint")), _cells, Ui.Row(clear));
        editor.Margin = new Padding(0, Theme.S3, 0, 0);
        var stack = Stack(2, 0);
        stack.Controls.Add(FillCard(_matrix, Ui.Hint(Loc.T("Cat.MatrixHint"))), 0, 0);
        stack.Controls.Add(editor, 0, 1);
        Controls.Add(stack);
    }

    public override SettingsPage Page => SettingsPage.Categories;

    public override string Heading => Loc.T("Nav.Categories");

    public override string? Subheading => Loc.T("Cat.Subheading");

    public override void Bind()
    {
        _binding = true;
        try
        {
            var devices = Working.Devices;
            if (_deviceCount != devices.Count || _matrix.Columns.Count != devices.Count + 1 ||
                devices.Where((d, i) => _matrix.Columns[i + 1].Text != d.DisplayName).Any())
            {
                var cols = new List<(string, int)> { (Loc.T("Field.Category"), 180) };
                cols.AddRange(devices.Select(d => (d.DisplayName, 170)));
                _matrix.SetColumns([.. cols]);
                _deviceCount = devices.Count;
            }
            var fx = Window.Fx;
            var known = fx is null ? null : fx.UserPresets.Concat(fx.BuiltInPresets).ToHashSet(StringComparer.Ordinal);
            _matrix.BeginUpdate();
            _matrix.Items.Clear();
            foreach (var c in Categories.All)
            {
                var item = new ListViewItem(Categories.DisplayName(c)) { UseItemStyleForSubItems = false };
                foreach (var d in devices)
                {
                    if (c == Categories.Hold)
                    {
                        var h = item.SubItems.Add(Loc.T("Cat.HoldCell"));
                        h.ForeColor = P.TextSecondary;
                        continue;
                    }
                    var a = Working.Assignments.FirstOrDefault(x => x.Device == d.Id && x.Category == c);
                    if (a is null)
                    {
                        var e = item.SubItems.Add(Loc.F("Cat.DefaultCell", d.DefaultPreset));
                        e.ForeColor = P.TextSecondary;
                    }
                    else
                    {
                        var s = item.SubItems.Add(a.Preset);
                        if (known is not null && !known.Contains(a.Preset)) { s.ForeColor = P.Error; s.Text = Loc.F("Devices.Missing", a.Preset); }
                    }
                }
                _matrix.Items.Add(item);
            }
            _matrix.EndUpdateAndFit();
            var row = Categories.All.ToList().IndexOf(_category);
            if (row >= 0) { _matrix.Items[row].Selected = true; _matrix.Items[row].Focused = true; _matrix.EnsureVisible(row); }
        }
        finally
        {
            _binding = false;
        }
        BindRow();
    }

    private void BindRow()
    {
        _binding = true;
        try
        {
            _rowTitle.Text = Categories.DisplayName(_category);
            _cells.SuspendLayout();
            foreach (Control c in _cells.Controls.Cast<Control>().ToArray()) c.Dispose();
            _cells.Controls.Clear();
            _cells.RowStyles.Clear();
            _cells.RowCount = 0;
            _combos.Clear();
            if (_category == Categories.Hold)
            {
                Ui.AddField(_cells, "", Ui.Text(Loc.T("Cat.HoldText"), wrap: true, maxWidth: 560));
            }
            else if (Working.Devices.Count == 0)
            {
                Ui.AddField(_cells, "", Ui.Text(Loc.T("Cat.NoDevices"), wrap: true));
            }
            else
            {
                var presets = PresetChoices();
                foreach (var d in Working.Devices)
                {
                    var combo = Ui.Combo([DeviceDefault, .. presets], 280, Loc.F("Cat.ComboAccessible", Categories.DisplayName(_category), d.DisplayName));
                    var a = Working.Assignments.FirstOrDefault(x => x.Device == d.Id && x.Category == _category);
                    if (a is not null && !combo.Items.Contains(a.Preset)) combo.Items.Add(a.Preset);
                    combo.SelectedItem = a?.Preset ?? DeviceDefault;
                    var id = d.Id;
                    combo.SelectionChangeCommitted += (_, _) => { if (!_binding) SetCell(id, combo.SelectedItem as string); };
                    _combos[id] = combo;
                    Ui.AddField(_cells, d.DisplayName, combo);
                }
            }
            _cells.ResumeLayout(true);
            Theme.Apply(_cells, P, P.Card);
        }
        finally
        {
            _binding = false;
        }
    }

    private void SetCell(string device, string? preset)
    {
        var category = _category;
        Window.Edit(c =>
        {
            var list = c.Assignments.Where(a => !(a.Device == device && a.Category == category)).ToList();
            if (preset is not null && preset != DeviceDefault) list.Add(new Assignment(device, category, null, preset));
            return c with { Assignments = list };
        });
        RefreshMatrixRow();
    }

    private void ClearRow()
    {
        var category = _category;
        Window.Edit(c => c with { Assignments = c.Assignments.Where(a => a.Category != category).ToList() });
        Bind();
    }

    private void RefreshMatrixRow()
    {
        // cheap: rebuild the matrix but keep the editor (and focus) as it is
        var focused = _combos.Values.FirstOrDefault(c => c.Focused);
        _binding = true;
        try
        {
            var row = Categories.All.ToList().IndexOf(_category);
            var item = _matrix.Items[row];
            for (var i = 0; i < Working.Devices.Count; i++)
            {
                var d = Working.Devices[i];
                var a = Working.Assignments.FirstOrDefault(x => x.Device == d.Id && x.Category == _category);
                var sub = item.SubItems[i + 1];
                var missing = a is not null && Window.Fx is { } fx && !fx.UserPresets.Concat(fx.BuiltInPresets).Contains(a.Preset, StringComparer.Ordinal);
                sub.Text = a is null ? Loc.F("Cat.DefaultCell", d.DefaultPreset) : missing ? Loc.F("Devices.Missing", a.Preset) : a.Preset;
                sub.ForeColor = a is null ? P.TextSecondary : missing ? P.Error : P.Text;
            }
            _matrix.Invalidate();
        }
        finally { _binding = false; }
        focused?.Focus();
    }

    public override void FocusItem(string id)
    {
        var i = IndexFromPath(id, "assignments");
        if (i is not { } k || k >= Working.Assignments.Count) return;
        var a = Working.Assignments[k];
        if (a.Category is null) return;
        _category = a.Category;
        Bind();
        if (_combos.TryGetValue(a.Device, out var combo)) combo.Focus();
    }
}
