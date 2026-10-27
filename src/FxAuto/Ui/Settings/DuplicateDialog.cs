using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.FxSound;
using FxAuto.Core.Presets;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>"Duplicate for device…" (REQ-107): exact copy of a preset named "&lt;DeviceTag&gt; - &lt;Category&gt;", optionally
/// assigned to that device and category.</summary>
internal sealed class DuplicateDialog : ThemedDialog
{
    private readonly FxStatus _fx;
    private readonly ComboBox _device;
    private readonly ComboBox _category;
    private readonly TextBox _name = new() { Width = 320 };
    private readonly CheckBox _assign = new() { Text = Loc.T("Duplicate.Assign"), AutoSize = true, Checked = true };
    private readonly Label _error = ErrorLabel();
    private bool _nameTouched;
    private bool _setting;

    public DuplicateDialog(string source, FxAutoConfig config, FxStatus fx) : base(Loc.T("Duplicate.Title"), Loc.T("Duplicate.Ok"))
    {
        _fx = fx;
        Add(Ui.Text(Loc.F("Duplicate.Intro", source), wrap: true, maxWidth: 520));
        var grid = Ui.Grid();
        grid.Margin = new Padding(0, Theme.S3, 0, 0);
        var devices = config.Devices.Select(d => new Ui.Choice(d.Id, $"{d.DisplayName}  ({d.Tag})")).ToList();
        _device = Ui.Combo(devices, 320);
        _category = Ui.Combo(Categories.All.Where(c => c != Categories.Hold).Select(c => new Ui.Choice(c, Categories.DisplayName(c))), 320);
        Ui.AddField(grid, Loc.T("Field.Device"), _device);
        Ui.AddField(grid, Loc.T("Field.Category"), _category);
        Ui.AddField(grid, Loc.T("Edit.SaveAsNew.Label"), _name, Loc.T("Duplicate.NameHint"));
        Ui.AddField(grid, "", _assign);
        Add(grid);
        Add(_error);

        var guessDevice = config.Devices.FirstOrDefault(d => !source.StartsWith(d.Tag, StringComparison.OrdinalIgnoreCase)) ?? config.Devices.FirstOrDefault();
        if (guessDevice is not null) _device.SelectedIndex = devices.FindIndex(c => c.Value == guessDevice.Id);
        // "G435 - Music" → Music
        var guessCategory = Categories.All.FirstOrDefault(c => source.EndsWith(PresetNameRules.Separator + Categories.PresetLabel(c), StringComparison.OrdinalIgnoreCase))
            ?? Categories.All.FirstOrDefault(c => source.EndsWith(Categories.PresetLabel(c), StringComparison.OrdinalIgnoreCase)) ?? Categories.Unknown;
        _category.SelectedItem = _category.Items.Cast<Ui.Choice>().FirstOrDefault(c => c.Value == guessCategory);
        _assign.Enabled = config.Devices.Count > 0;
        if (config.Devices.Count == 0) _assign.Checked = false;

        _device.SelectedIndexChanged += (_, _) => Propose(config);
        _category.SelectedIndexChanged += (_, _) => Propose(config);
        _name.TextChanged += (_, _) => { if (!_setting) _nameTouched = true; Check(); };
        Propose(config);
    }

    public string NewName => _name.Text;

    /// <summary>Device to assign the copy to (null = no assignment).</summary>
    public string? AssignDevice => _assign.Checked ? (_device.SelectedItem as Ui.Choice)?.Value : null;

    public string? AssignCategory => _assign.Checked ? (_category.SelectedItem as Ui.Choice)?.Value : null;

    private void Propose(FxAutoConfig config)
    {
        if (_nameTouched) { Check(); return; }
        var device = config.Devices.FirstOrDefault(d => d.Id == (_device.SelectedItem as Ui.Choice)?.Value);
        var category = (_category.SelectedItem as Ui.Choice)?.Value ?? Categories.Unknown;
        _setting = true;
        _name.Text = PresetNameRules.Compose(device?.Tag ?? "Device", Categories.PresetLabel(category));
        _setting = false;
        Check();
    }

    private bool Check()
    {
        var r = PresetNameRules.Validate(_name.Text, _fx, PresetNameRules.DefaultMaxUserPresets);
        _error.Text = r.Error ?? "";
        _error.ForeColor = P.Error;
        OkButton.Enabled = r.Ok;
        return r.Ok;
    }

    protected override bool OnOk() => Check();
}
