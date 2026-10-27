using System.Runtime.InteropServices;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

internal static class UiNative
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private const int DwmUseImmersiveDarkMode = 20;

    /// <summary>Dark title bar to match the palette (no-op where unsupported).</summary>
    public static void TitleBar(Form f, bool dark)
    {
        if (!f.IsHandleCreated) return;
        var v = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(f.Handle, DwmUseImmersiveDarkMode, ref v, sizeof(int));
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(nint hwnd, string? appName, string? idList);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    private const int LvmGetHeader = 0x101F;

    /// <summary>Native list theme matching the palette: "DarkMode_ItemsView" in dark (no light strip or dividers
    /// through the empty area), "Explorer" in light; applied to the list and to its header.</summary>
    public static void ListTheme(ListView lv, bool dark)
    {
        if (!lv.IsHandleCreated) return;
        _ = SetWindowTheme(lv.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        var header = SendMessage(lv.Handle, LvmGetHeader, 0, 0);
        if (header != 0) _ = SetWindowTheme(header, dark ? "DarkMode_ItemsView" : "ItemsView", null);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
}

/// <summary>Base for the window's dialogs: DPI scaling, palette, dark title bar, OK/Cancel row.</summary>
internal abstract class ThemedDialog : Form
{
    protected readonly Palette P = Theme.Current;
    protected readonly TableLayoutPanel Content;
    protected readonly FlowLayoutPanel Buttons;
    protected readonly AccentButton OkButton;
    protected readonly Button CancelBtn;

    protected ThemedDialog(string title, string okText, string? cancelText = null)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = AppIcon.Window;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(Theme.S5, Theme.S4, Theme.S5, Theme.S4);
        BackColor = P.Window;
        ForeColor = P.Text;

        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty };
        Content = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty, MinimumSize = new Size(420, 0) };
        Buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, Theme.S4, 0, 0), WrapContents = false };
        CancelBtn = Ui.Button(cancelText ?? Loc.T("Form.Cancel"), () => { DialogResult = DialogResult.Cancel; Close(); });
        CancelBtn.Margin = Padding.Empty;
        OkButton = new AccentButton { Text = okText, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(96, 32), Padding = new Padding(Theme.S2, 0, Theme.S2, 0), Margin = new Padding(0, 0, Theme.S2, 0), Font = Theme.Body, UseMnemonic = false };
        OkButton.Click += (_, _) => { if (OnOk()) { DialogResult = DialogResult.OK; Close(); } };
        Buttons.Controls.Add(CancelBtn);
        Buttons.Controls.Add(OkButton);
        root.Controls.Add(Content, 0, 0);
        root.Controls.Add(Buttons, 0, 1);
        Controls.Add(root);
        AcceptButton = OkButton;
        CancelButton = CancelBtn;
    }

    protected override bool ShowWithoutActivation => UiFlags.NoActivate;

    /// <summary>Validates and stores the result; false keeps the dialog open.</summary>
    protected virtual bool OnOk() => true;

    protected override void OnLoad(EventArgs e)
    {
        Theme.Apply(this, P);
        ResumeLayout(true);
        base.OnLoad(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiNative.TitleBar(this, P.Dark);
    }

    protected void Add(Control c)
    {
        c.Dock = DockStyle.Fill;
        Content.Controls.Add(c);
    }

    /// <summary>Inline validation message under the fields (tone colour, never a pop-up).</summary>
    protected static Label ErrorLabel() => new() { AutoSize = true, Font = Theme.Body, MaximumSize = new Size(520, 0), Margin = new Padding(0, Theme.S2, 0, 0), Tag = "keep-color", UseMnemonic = false };
}

/// <summary>Themed confirmation (MessageBox and TaskDialog stay light in dark mode).</summary>
internal sealed class ConfirmDialog : ThemedDialog
{
    /// <param name="destructive">Irreversible action: Enter does not confirm (no accept button) and focus starts on
    /// Cancel.</param>
    public ConfirmDialog(string title, string message, string okText, string? warning = null, IEnumerable<string>? details = null,
        bool destructive = false)
        : base(title, okText)
    {
        if (destructive)
        {
            AcceptButton = null;
            Shown += (_, _) => CancelBtn.Focus();
            ActiveControl = CancelBtn;
        }
        Add(Ui.Text(message, Theme.Body, wrap: true, maxWidth: 520));
        if (details is not null)
        {
            var list = details.ToList();
            if (list.Count > 0)
            {
                var box = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    Text = string.Join(Environment.NewLine, list),
                    Height = Math.Min(220, 24 + list.Count * 20),
                    Width = 520,
                    AccessibleName = Loc.T("Confirm.Details"),
                    Font = Theme.Body,
                    Margin = new Padding(0, Theme.S2, 0, 0),
                };
                Add(Ui.Field(box));
            }
        }
        if (warning is not null)
        {
            var bar = new InfoBar { Tone = Tone.Warning, Message = warning, Width = 520, Margin = new Padding(0, Theme.S3, 0, 0) };
            Add(bar);
        }
    }
}

/// <summary>One-line text prompt with live validation (rename, save as new).</summary>
internal sealed class TextPromptDialog : ThemedDialog
{
    private readonly TextBox _box;
    private readonly Label _error = ErrorLabel();
    private readonly Func<string, string?> _validate;

    public TextPromptDialog(string title, string label, string initial, string okText, Func<string, string?> validate, string? hint = null)
        : base(title, okText)
    {
        _validate = validate;
        var grid = Ui.Grid();
        _box = new TextBox { Text = initial, Width = 340, Font = Theme.Body };
        Ui.AddField(grid, label, _box, hint);
        Add(grid);
        Add(_error);
        _box.TextChanged += (_, _) => Check();
        Shown += (_, _) => { _box.SelectAll(); _box.Focus(); Check(); };
    }

    public string Value => _box.Text;

    private bool Check()
    {
        var err = _validate(_box.Text);
        _error.Text = err ?? "";
        _error.ForeColor = P.Error;
        OkButton.Enabled = err is null;
        return err is null;
    }

    protected override bool OnOk() => Check();
}

/// <summary>Picks one value from a list (for example the replacement for a deleted default preset).</summary>
internal sealed class ChoiceDialog : ThemedDialog
{
    private readonly ComboBox _combo;

    public ChoiceDialog(string title, string message, string label, IEnumerable<string> choices, string okText,
        string? warning = null, bool destructive = false)
        : base(title, okText)
    {
        Add(Ui.Text(message, Theme.Body, wrap: true, maxWidth: 520));
        var grid = Ui.Grid();
        grid.Margin = new Padding(0, Theme.S3, 0, 0);
        _combo = Ui.Combo(choices, 300);
        if (_combo.Items.Count > 0) _combo.SelectedIndex = 0;
        Ui.AddField(grid, label, _combo);
        Add(grid);
        if (warning is not null)
            Add(new InfoBar { Tone = Tone.Warning, Message = warning, Width = 520, Margin = new Padding(0, Theme.S3, 0, 0) });
        OkButton.Enabled = _combo.Items.Count > 0;
        if (destructive)
        {
            AcceptButton = null;
            ActiveControl = CancelBtn;
            Shown += (_, _) => CancelBtn.Focus();
        }
    }

    public string? Value => _combo.SelectedItem as string;
}

/// <summary>Edits a list of names, one per line (ignored apps, launchers, game folder exclusions).</summary>
internal sealed class ListEditorDialog : ThemedDialog
{
    private readonly TextBox _box;

    public ListEditorDialog(string title, string explanation, IEnumerable<string> items)
        : base(title, Loc.T("Common.Ok"))
    {
        Add(Ui.Text(explanation, Theme.Body, wrap: true, maxWidth: 520));
        _box = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            Width = 520,
            Height = 260,
            Text = string.Join(Environment.NewLine, items),
            AccessibleName = Loc.F("ListEditor.Accessible", title),
            Font = Theme.Body,
            Margin = new Padding(0, Theme.S2, 0, 0),
        };
        Add(Ui.Field(_box));
        Add(Ui.Hint(Loc.T("ListEditor.Hint"), 520));
    }

    public IReadOnlyList<string> Items => _box.Lines.Select(l => l.Trim()).Where(l => l.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
