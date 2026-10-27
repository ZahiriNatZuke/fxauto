using System.Text.RegularExpressions;
using FxAuto.App;
using FxAuto.Core.Config;
using FxAuto.Core.Devices;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>Devices: the outputs Windows reports (FxSound's virtual device hidden), which ones FxAuto manages, their
/// tag ("Short name used in preset names") and default preset (REQ-105, REQ-109).</summary>
internal sealed partial class DevicesPage : SettingsPageBase
{
    private sealed record Row(int DeviceIndex, DeviceConfig? Device, EndpointInfo? Endpoint)
    {
        public string Key => Device is not null ? "device:" + Device.Id : "endpoint:" + Endpoint!.EndpointId;
    }

    private readonly ThemedListView _list = new() { AccessibleName = Loc.T("Nav.Devices") };
    private readonly CheckBox _showOff = new() { Text = Loc.T("Devices.ShowOff"), AutoSize = true, Margin = new Padding(0, Theme.S2, 0, 0) };
    private readonly TableLayoutPanel _editorHost = Stack(1);
    private readonly List<Row> _rows = [];

    // editor for a managed device
    private readonly TableLayoutPanel _deviceEditor;
    private readonly TextBox _name = new() { Width = 260 };
    private readonly TextBox _tag = new() { Width = 160 };
    private readonly Label _tagError = Ui.Text("", wrap: true, maxWidth: 520);
    private readonly Label _tagHint = Ui.Hint(Loc.T("Devices.TagHint"));
    private readonly InputFrame _tagFrame;
    private readonly ComboBox _default = Ui.Combo([], 260);
    private readonly CheckBox _enabled = new() { Text = Loc.T("Devices.Managed"), AutoSize = true };
    private readonly Label _match = Ui.Hint("");

    // editor for an endpoint FxAuto does not know
    private readonly TableLayoutPanel _endpointEditor;
    private readonly Label _endpointText = Ui.Text("", wrap: true, maxWidth: 560);

    private bool _binding;
    private string? _selectedKey;

    public DevicesPage(SettingsForm window) : base(window)
    {
        _list.SetColumns((Loc.T("Devices.Col.Name"), 165), (Loc.T("Devices.Col.Tag"), 70), (Loc.T("Devices.Col.Adapter"), 165), (Loc.T("Devices.Col.State"), 130), (Loc.T("Devices.Col.Default"), 150));
        _list.SelectedIndexChanged += (_, _) => OnSelection();
        _showOff.CheckedChanged += (_, _) => { if (!_binding) Bind(); };

        _deviceEditor = Ui.Grid();
        Ui.AddField(_deviceEditor, Loc.T("Devices.Col.Name"), _name);
        var tagStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _tagFrame = Ui.Field(_tag);
        _tagFrame.Margin = Padding.Empty;
        tagStack.Controls.Add(_tagFrame);
        _tagHint.Margin = new Padding(0, 2, 0, 0);
        tagStack.Controls.Add(_tagHint);
        _tagError.Tag = "keep-color";
        tagStack.Controls.Add(_tagError);
        _tag.AccessibleName = Loc.T("Devices.TagAccessible");
        Ui.AddField(_deviceEditor, Loc.T("Devices.Col.Tag"), tagStack);
        Ui.AddField(_deviceEditor, Loc.T("Devices.Col.Default"), _default, Loc.T("Devices.DefaultHint"));
        Ui.AddField(_deviceEditor, "", _enabled);
        Ui.AddField(_deviceEditor, Loc.T("Devices.RecognisedBy"), _match);
        var forget = Ui.Button(Loc.T("Devices.ForgetButton"), Forget);
        Ui.AddField(_deviceEditor, "", Ui.Row(forget));

        _endpointEditor = Ui.Grid();
        Ui.AddField(_endpointEditor, "", _endpointText);
        Ui.AddField(_endpointEditor, "", Ui.Row(Ui.Button(Loc.T("Devices.Manage"), Manage)));

        _name.TextChanged += (_, _) => EditSelected(d => d with { DisplayName = _name.Text });
        _tag.TextChanged += (_, _) => { EditSelected(d => d with { Tag = _tag.Text }); CheckTag(); };
        _default.SelectionChangeCommitted += (_, _) => { if (_default.SelectedItem is string p) EditSelected(d => d with { DefaultPreset = p }); };
        _enabled.CheckedChanged += (_, _) => EditSelected(d => d with { Enabled = _enabled.Checked });

        var listCard = FillCard(_list, _showOff, Ui.Hint(Loc.T("Devices.ListHint")));
        var editorCard = CardOf(_editorHost);
        editorCard.Margin = new Padding(0, Theme.S3, 0, 0);
        var stack = Stack(2, 0);
        stack.Controls.Add(listCard, 0, 0);
        stack.Controls.Add(editorCard, 0, 1);
        Controls.Add(stack);
    }

    public override SettingsPage Page => SettingsPage.Devices;

    public override string Heading => Loc.T("Nav.Devices");

    public override string? Subheading => Loc.T("Devices.Subheading");

    [GeneratedRegex(@"^[A-Za-z0-9 _.\-]{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagRx();

    private static readonly LearnedState NoLearned = new(1, new Dictionary<string, IReadOnlyList<string>>(), [], null);

    public override void Bind()
    {
        _binding = true;
        try
        {
            var endpoints = Host.Endpoints.Endpoints.Where(e => !DeviceMatcher.IsFxSoundVirtual(e)).ToList();
            var matches = endpoints.Select(e => (Ep: e, M: DeviceMatcher.Match(e, Working.Devices, NoLearned, endpoints))).ToList();
            _rows.Clear();
            for (var i = 0; i < Working.Devices.Count; i++)
            {
                var d = Working.Devices[i];
                var eps = matches.Where(m => m.M.DeviceId == d.Id).Select(m => m.Ep).ToList();
                var ep = eps.FirstOrDefault(e => e.State == 1) ?? eps.FirstOrDefault();
                _rows.Add(new Row(i, d, ep));
            }
            foreach (var (ep, m) in matches)
            {
                if (m.DeviceId is not null) continue;
                if (ep.State != 1 && !_showOff.Checked) continue;
                _rows.Add(new Row(-1, null, ep));
            }

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var r in _rows) _list.Items.Add(ItemFor(r));
            _list.EndUpdateAndFit();

            var choices = PresetChoices();
            _default.Items.Clear();
            foreach (var c in choices) _default.Items.Add(c);

            var index = _selectedKey is null ? (_rows.Count > 0 ? 0 : -1) : _rows.FindIndex(r => r.Key == _selectedKey);
            if (index < 0 && _rows.Count > 0) index = 0;
            if (index >= 0)
            {
                _list.Items[index].Selected = true;
                _list.Items[index].Focused = true;
            }
        }
        finally
        {
            _binding = false;
        }
        OnSelection();
    }

    private ListViewItem ItemFor(Row r)
    {
        var status = Host.Status;
        string name, tag, adapter, state, preset;
        var on = r.Endpoint?.State == 1;
        if (r.Device is { } d)
        {
            name = d.DisplayName;
            tag = d.Tag;
            adapter = d.Match.Adapters.Count > 0 ? string.Join(", ", d.Match.Adapters) : r.Endpoint?.Adapter ?? "";
            state = !d.Enabled ? Loc.T("Devices.State.NotManaged") : status.DeviceId == d.Id ? Loc.T("Devices.State.Current") : on ? Loc.T("Devices.State.Connected") : Loc.T("Devices.State.Disconnected");
            preset = d.DefaultPreset;
        }
        else
        {
            var ep = r.Endpoint!;
            name = ep.FriendlyName;
            tag = "";
            adapter = ep.Adapter ?? "";
            state = on ? Loc.T("Devices.State.NotManaged") : Loc.T("Devices.State.NotManagedOff");
            preset = "";
        }
        var item = new ListViewItem(name) { UseItemStyleForSubItems = false, Tag = r };
        item.SubItems.Add(tag);
        item.SubItems.Add(adapter);
        var st = item.SubItems.Add(state);
        st.ForeColor = state == Loc.T("Devices.State.Current") ? P.Success : on && r.Device is not null ? P.Text : P.TextSecondary;
        var pr = item.SubItems.Add(preset);
        if (r.Device is not null && Window.Fx is { } fx && !fx.UserPresets.Concat(fx.BuiltInPresets).Contains(preset, StringComparer.Ordinal))
        {
            pr.ForeColor = P.Error;
            pr.Text = Loc.F("Devices.Missing", preset);
        }
        if (r.Device is null) item.ForeColor = P.TextSecondary;
        return item;
    }

    private Row? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Row : null;

    private void OnSelection()
    {
        if (_binding) return;
        var r = Selected;
        _selectedKey = r?.Key;
        _editorHost.SuspendLayout();
        _editorHost.Controls.Clear();
        if (r?.Device is { } d)
        {
            _binding = true;
            try
            {
                _name.Text = d.DisplayName;
                _tag.Text = d.Tag;
                _enabled.Checked = d.Enabled;
                if (!_default.Items.Contains(d.DefaultPreset)) _default.Items.Add(d.DefaultPreset);
                _default.SelectedItem = d.DefaultPreset;
                var m = d.Match;
                _match.Text = Loc.F("Devices.Match.Adapter", string.Join(Loc.T("Devices.Match.Or"), m.Adapters.Select(a => $"\"{a}\"")))
                    + (m.FormFactor is { } ff ? ", " + Loc.T("FormFactor." + ff) : "")
                    + (m.EndpointDesc is not null ? Loc.F("Devices.Match.Output", m.EndpointDesc) : "")
                    + (m.PinnedEndpointIds.Count > 0 ? Loc.F("Devices.Match.Pinned", m.PinnedEndpointIds.Count) : "");
                CheckTag();
            }
            finally { _binding = false; }
            _editorHost.Controls.Add(_deviceEditor, 0, 0);
        }
        else if (r?.Endpoint is { } ep)
        {
            _endpointText.Text = Loc.F("Devices.EndpointText", ep.FriendlyName);
            _editorHost.Controls.Add(_endpointEditor, 0, 0);
        }
        else
        {
            _editorHost.Controls.Add(Ui.Text(Loc.T("Devices.SelectOne")), 0, 0);
        }
        Theme.Apply(_editorHost, P, P.Card);
        _tagError.ForeColor = P.Error;
        _editorHost.ResumeLayout(true);
    }

    /// <summary>Shows the validator's own message for this device's tag (one wording everywhere).</summary>
    private void CheckTag()
    {
        string? error = null;
        if (Selected is { Device: not null } r)
        {
            var path = $"devices[{r.DeviceIndex}].tag";
            error = Window.WorkingIssues.FirstOrDefault(i => i.Level == IssueLevel.Error && i.Path.StartsWith(path, StringComparison.Ordinal))?.Shown;
            if (error is null && !TagRx().IsMatch(_tag.Text)) error = Loc.T("Devices.TagError");
        }
        _tagError.Text = error ?? "";
        _tagError.ForeColor = P.Error;
        _tagError.Visible = error is not null;
        _tagHint.Visible = error is null;
        _tagFrame.Error = error is not null;
    }

    private void EditSelected(Func<DeviceConfig, DeviceConfig> change)
    {
        if (_binding || Selected is not { Device: not null } r) return;
        var i = r.DeviceIndex;
        var next = change(Working.Devices[i]);
        Window.Edit(c => c with { Devices = c.Devices.Select((d, k) => k == i ? next : d).ToList() });
        var item = _list.SelectedItems[0];
        var updated = r with { Device = next };
        var fresh = ItemFor(updated);
        for (var k = 0; k < fresh.SubItems.Count; k++)
        {
            item.SubItems[k].Text = fresh.SubItems[k].Text;
            item.SubItems[k].ForeColor = fresh.SubItems[k].ForeColor;
        }
        item.Tag = updated;
    }

    private void Manage()
    {
        if (Selected?.Endpoint is not { } ep) return;
        var adapter = ep.Adapter ?? DeviceKeys.AdapterFromFriendlyName(ep.FriendlyName) ?? ep.FriendlyName;
        var ids = Working.Devices.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = DeviceKeys.SuggestId(adapter);
        for (var n = 2; ids.Contains(id); n++) id = DeviceKeys.SuggestId(adapter) + "-" + n;
        var tags = Working.Devices.Select(d => d.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tag = DeviceKeys.SuggestTag(adapter);
        for (var n = 2; tags.Contains(tag); n++) tag = DeviceKeys.SuggestTag(adapter) + " " + n;
        var preset = Host.Status.VerifiedPreset ?? PresetChoices().FirstOrDefault() ?? "General";
        var device = new DeviceConfig(id, ep.EndpointDesc ?? ep.FriendlyName, tag,
            new DeviceMatchSpec([adapter], ep.FormFactor, ep.EndpointDesc, [], ep.ContainerId),
            preset, Enabled: true, LastSeenOutputName: null);
        Window.Edit(c => c with { Devices = [.. c.Devices, device] });
        _selectedKey = "device:" + id;
        Bind();
        _name.Focus();
    }

    private void Forget()
    {
        if (Selected?.Device is not { } d) return;
        var used = Working.Assignments.Count(a => a.Device == d.Id);
        if (!Window.Confirm(Loc.T("Devices.Forget.Title"), used == 1 ? Loc.F("Devices.Forget.One", d.DisplayName) : Loc.F("Devices.Forget.Many", d.DisplayName, used),
            Loc.T("Devices.Forget.Ok"), destructive: true))
            return;
        Window.Edit(c => c with
        {
            Devices = c.Devices.Where(x => x.Id != d.Id).ToList(),
            Assignments = c.Assignments.Where(a => a.Device != d.Id).ToList(),
        });
        _selectedKey = null;
        Bind();
    }

    public override void FocusItem(string id)
    {
        var index = IndexFromPath(id, "devices");
        var row = index is { } i ? _rows.FindIndex(r => r.DeviceIndex == i)
            : _rows.FindIndex(r => r.Endpoint?.EndpointId == id || r.Device?.Id == id);
        if (row < 0 && !_showOff.Checked)
        {
            _showOff.Checked = true; // the endpoint may be disconnected again
            row = _rows.FindIndex(r => r.Endpoint?.EndpointId == id);
        }
        if (row < 0) return;
        _list.SelectedItems.Clear();
        _list.Items[row].Selected = true;
        _list.Items[row].Focused = true;
        _list.EnsureVisible(row);
        if (_rows[row].Device is null) _list.Focus();
        else if (id.EndsWith(".tag", StringComparison.Ordinal)) _tag.Focus();
        else if (id.EndsWith(".defaultPreset", StringComparison.Ordinal)) _default.Focus();
        else _list.Focus();
    }
}
