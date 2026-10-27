using System.Drawing.Drawing2D;

using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>Set by the preview tool so its windows never take focus; the app leaves it false.</summary>
internal static class UiFlags
{
    public static bool NoActivate { get; set; }
}

/// <summary>Base for controls that paint themselves from the palette.</summary>
internal abstract class ThemedControl : Control
{
    private Palette _palette = Theme.Current;

    protected ThemedControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Palette Palette
    {
        get => _palette;
        set { _palette = value; OnPaletteChanged(); Invalidate(); }
    }

    protected virtual void OnPaletteChanged() { }
}

/// <summary>Grouped content surface: rounded 1 px stroke, card colour, inner padding.</summary>
internal sealed class Card : Panel
{
    private Palette _palette = Theme.Current;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint, true);
        Padding = new Padding(Theme.S4, Theme.S3, Theme.S4, Theme.S3);
        Margin = new Padding(0, 0, 0, Theme.S3);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Palette Palette
    {
        get => _palette;
        set { _palette = value; BackColor = value.Card; Invalidate(); }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var ground = Parent?.BackColor ?? _palette.Window;
        e.Graphics.Clear(ground);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var s = Theme.Scale(this);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, 6 * s);
        using var fill = new SolidBrush(_palette.Card);
        using var pen = new Pen(_palette.CardBorder, 1);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>Primary action button (accent fill). One per window: Apply.</summary>
internal sealed class AccentButton : Button
{
    private Palette _palette = Theme.Current;

    public AccentButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Palette = Theme.Current;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Palette Palette
    {
        get => _palette;
        set
        {
            _palette = value;
            BackColor = Enabled ? value.Accent : DisabledBack;
            ForeColor = value.OnAccent;
            FlatAppearance.MouseOverBackColor = ControlPaint.Light(value.Accent, value.Dark ? -0.1f : 0.15f);
            FlatAppearance.MouseDownBackColor = ControlPaint.Dark(value.Accent, 0.05f);
            Invalidate();
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        BackColor = Enabled ? _palette.Accent : DisabledBack;
    }

    private Color DisabledBack => _palette.Dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xE0, 0xE0, 0xE0);

    protected override void OnPaint(PaintEventArgs pevent)
    {
        base.OnPaint(pevent);
        if (!Enabled)
        {
            // WinForms paints disabled flat text in a grey that fails contrast on the disabled fill; repaint it.
            pevent.Graphics.Clear(BackColor);
            TextRenderer.DrawText(pevent.Graphics, Text, Font, ClientRectangle, _palette.TextDisabled,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

/// <summary>Side navigation (owner-drawn ListBox: native keyboard and UI Automation, drawn from the palette).
/// Selected row: soft fill plus a short accent pill on the leading edge, as in Windows 11 Settings.</summary>
internal sealed class NavList : ListBox
{
    private Palette _palette = Theme.Current;
    private int _hover = -1;

    public sealed record Item(string Glyph, string Text)
    {
        public override string ToString() => Text;
    }

    public NavList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        AccessibleName = Loc.T("Nav.Accessible");
        Font = Theme.Body;
        ItemHeight = 36;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Palette Palette
    {
        get => _palette;
        set { _palette = value; BackColor = value.Window; ForeColor = value.Text; Invalidate(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = IndexFromPoint(e.Location);
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = Theme.Scale(this);
        using (var ground = new SolidBrush(_palette.Window)) g.FillRectangle(ground, e.Bounds);

        var selected = (e.State & DrawItemState.Selected) != 0;
        var r = new RectangleF(e.Bounds.X + 4 * s, e.Bounds.Y + 2 * s, e.Bounds.Width - 8 * s, e.Bounds.Height - 4 * s);
        if (selected || e.Index == _hover)
        {
            using var path = Theme.Rounded(r, 4 * s);
            using var fill = new SolidBrush(selected ? _palette.NavSelected : _palette.NavHover);
            g.FillPath(fill, path);
        }
        if (selected)
        {
            var pill = new RectangleF(r.X, r.Y + r.Height / 2 - 8 * s, 3 * s, 16 * s);
            using var path = Theme.Rounded(pill, 1.5f * s);
            using var accent = new SolidBrush(_palette.Accent);
            g.FillPath(accent, path);
        }
        if ((e.State & DrawItemState.Focus) != 0 && (e.State & DrawItemState.NoFocusRect) == 0)
        {
            using var pen = new Pen(_palette.Text, 1) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, Rectangle.Round(r));
        }

        var item = (Item)Items[e.Index];
        var iconRect = new Rectangle((int)(r.X + 12 * s), e.Bounds.Y, (int)(20 * s), e.Bounds.Height);
        TextRenderer.DrawText(g, item.Glyph, Theme.Icon, iconRect, _palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        var textRect = new Rectangle(iconRect.Right + (int)(12 * s), e.Bounds.Y, e.Bounds.Right - iconRect.Right - (int)(16 * s), e.Bounds.Height);
        TextRenderer.DrawText(g, item.Text, selected ? Theme.BodyStrong : Theme.Body, textRect, _palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        using var b = new SolidBrush(_palette.Window);
        pevent.Graphics.FillRectangle(b, ClientRectangle);
    }
}

/// <summary>Inline message bar (Windows 11 InfoBar): tone icon, bold title, message and optional actions.</summary>
internal sealed class InfoBar : ThemedControl
{
    private readonly Label _icon = new() { AutoSize = false, Font = Theme.Icon, Tag = "keep-color", TextAlign = ContentAlignment.TopLeft };
    private readonly Label _text = new() { AutoSize = false, Font = Theme.Body, Tag = "keep-color", UseMnemonic = false };
    private readonly FlowLayoutPanel _actions = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
    private Tone _tone;
    private bool _layingOut;

    public InfoBar()
    {
        Padding = new Padding(Theme.S4, Theme.S2, Theme.S2, Theme.S2);
        Margin = new Padding(0, 0, 0, Theme.S2);
        Dock = DockStyle.Top;
        Height = 44;
        Controls.Add(_icon);
        Controls.Add(_text);
        Controls.Add(_actions);
        AccessibleRole = AccessibleRole.Alert;
        _actions.SizeChanged += (_, _) => PerformLayout();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Tone Tone
    {
        get => _tone;
        set { _tone = value; OnPaletteChanged(); Invalidate(); }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Message
    {
        get => _text.Text;
        set { _text.Text = value; AccessibleName = value; PerformLayout(); }
    }

    public Button AddAction(string text, Action onClick)
    {
        var b = new ThemedButton { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 30), Margin = new Padding(Theme.S2, 0, 0, 0), Font = Theme.Body, Padding = new Padding(Theme.S2, 0, Theme.S2, 0), UseMnemonic = false };
        b.Click += (_, _) => onClick();
        _actions.Controls.Add(b);
        Theme.Apply(_actions, Palette, Palette.ToneBack(_tone));
        PerformLayout();
        return b;
    }

    protected override void OnPaletteChanged()
    {
        _icon.Text = _tone switch
        {
            Tone.Success => "",
            Tone.Warning => "",
            Tone.Error => "",
            _ => "",
        };
        _icon.ForeColor = Palette.ToneColor(_tone);
        _text.ForeColor = Palette.Text;
        _icon.BackColor = _text.BackColor = _actions.BackColor = Palette.ToneBack(_tone);
        Theme.Apply(_actions, Palette, Palette.ToneBack(_tone));
    }

    /// <summary>Height the bar needs at <paramref name="width"/> (text wraps).</summary>
    private int MeasureHeight(int width, out int textW, out int textH, out Size actions)
    {
        var s = Theme.Scale(this);
        var pad = Padding;
        actions = _actions.Controls.Count > 0 ? _actions.PreferredSize : Size.Empty;
        var textX = pad.Left + (int)(20 * s) + (int)(8 * s);
        textW = Math.Max((int)(120 * s), width - textX - pad.Right - actions.Width - (int)(12 * s));
        textH = TextRenderer.MeasureText(_text.Text.Length == 0 ? " " : _text.Text, _text.Font, new Size(textW, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        return Math.Max(Math.Max(textH, actions.Height), (int)(22 * s)) + pad.Vertical;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var w = proposedSize.Width is > 0 and < 100_000 ? proposedSize.Width : Width;
        return new Size(w, MeasureHeight(w, out _, out _, out _));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_layingOut || Width <= 0) return;
        _layingOut = true;
        try
        {
            var s = Theme.Scale(this);
            var pad = Padding;
            var icon = new Size((int)(20 * s), (int)(22 * s));
            var actions = _actions.Controls.Count > 0 ? _actions.PreferredSize : Size.Empty;
            var textX = pad.Left + icon.Width + (int)(8 * s);
            var textW = Math.Max((int)(120 * s), Width - textX - pad.Right - actions.Width - (int)(12 * s));
            var measured = TextRenderer.MeasureText(_text.Text.Length == 0 ? " " : _text.Text, _text.Font, new Size(textW, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            var inner = Math.Max(Math.Max(measured.Height, actions.Height), icon.Height);
            var h = inner + pad.Vertical;
            _icon.Bounds = new Rectangle(pad.Left, pad.Top + (int)(2 * s), icon.Width, icon.Height);
            _text.Bounds = new Rectangle(textX, pad.Top + Math.Max(0, (inner - measured.Height) / 2), textW, measured.Height + 2);
            _actions.Location = new Point(Width - pad.Right - actions.Width, pad.Top + Math.Max(0, (inner - actions.Height) / 2));
            if (Height != h) Height = h;
        }
        finally { _layingOut = false; }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        PerformLayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Palette.Window);
        var s = Theme.Scale(this);
        using var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 6 * s);
        using var fill = new SolidBrush(Palette.ToneBack(_tone));
        using var pen = new Pen(Palette.Dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
    }
}

/// <summary>Small filled circle that marks a state next to text (active device, playing source…).</summary>
internal sealed class Dot : ThemedControl
{
    private Tone _tone;

    public Dot()
    {
        Size = new Size(10, 10);
        Margin = new Padding(0, 6, Theme.S2, 0);
        AccessibleRole = AccessibleRole.Graphic;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Tone Tone
    {
        get => _tone;
        set { _tone = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Palette.Window);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(Palette.ToneColor(_tone));
        e.Graphics.FillEllipse(b, 1, 1, Width - 3, Height - 3);
    }
}

/// <summary>ListView in details mode with themed column headers (the native header stays light in dark mode) and
/// the last column filling the remaining width.</summary>
internal sealed class ThemedListView : ListView
{
    private Palette _palette = Theme.Current;

    public ThemedListView()
    {
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = false;
        OwnerDraw = true;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        BorderStyle = BorderStyle.None; // the InputFrame around it draws the themed border
        Font = Theme.Body;
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        MinimumSize = new Size(100, 120);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Palette Palette
    {
        get => _palette;
        set { _palette = value; BackColor = value.InputBack; ForeColor = value.Text; UiNative.ListTheme(this, value.Dark); Invalidate(); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiNative.ListTheme(this, _palette.Dark);
        FillLast();
    }

    /// <summary>EndUpdate, then refit the columns (a vertical scrollbar may have appeared).</summary>
    public void EndUpdateAndFit()
    {
        EndUpdate();
        FillLast();
    }

    /// <summary>Column widths in logical pixels; the last one fills.</summary>
    public void SetColumns(params (string Text, int Width)[] cols)
    {
        Columns.Clear();
        _designWidths = null;
        foreach (var (t, w) in cols) Columns.Add(t, (int)(w * Theme.Scale(this)));
        FillLast();
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics!;
        using (var b = new SolidBrush(_palette.Card)) g.FillRectangle(b, e.Bounds);
        using (var pen = new Pen(_palette.Divider))
        {
            g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            if (e.ColumnIndex < Columns.Count - 1)
                g.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
        }
        var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height);
        TextRenderer.DrawText(g, e.Header?.Text ?? "", Theme.Caption, r, _palette.TextSecondary,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        if (!CheckBoxes) return;
        e.DrawDefault = true;
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        var g = e.Graphics!;
        var item = e.Item!;
        var selected = item.Selected;
        var back = selected ? (Focused ? _palette.Accent : _palette.NavSelected) : _palette.InputBack;
        var fore = selected && Focused ? _palette.OnAccent : (e.SubItem?.ForeColor is { IsEmpty: false } c && c != ForeColor ? c : _palette.Text);
        if (CheckBoxes)
        {
            e.DrawDefault = true;
            return;
        }
        using (var b = new SolidBrush(back)) g.FillRectangle(b, e.Bounds);
        var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height);
        var font = e.SubItem?.Font ?? Font;
        TextRenderer.DrawText(g, e.SubItem?.Text ?? "", font, r, fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (selected && Focused && e.ColumnIndex == 0 && item.Focused)
            ControlPaint.DrawFocusRectangle(g, item.Bounds, fore, back);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F /* WM_PAINT */ || !_palette.Dark || View != View.Details) return;
        // the dark native theme draws column dividers through the empty area under the rows: paint it over
        var top = Items.Count > 0 ? GetItemRect(Items.Count - 1).Bottom : HeaderBottom();
        if (top >= ClientSize.Height) return;
        using var g = CreateGraphics();
        using var b = new SolidBrush(_palette.InputBack);
        g.FillRectangle(b, 0, top, ClientSize.Width, ClientSize.Height - top);
    }

    private int HeaderBottom() => Columns.Count == 0 ? 0 : (int)(24 * Theme.Scale(this));

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FillLast();
    }

    private int[]? _designWidths;

    private void FillLast()
    {
        if (Columns.Count == 0 || (!IsHandleCreated && Width == 0)) return;
        var s = Theme.Scale(this);
        _designWidths ??= Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
        if (_designWidths.Length != Columns.Count) _designWidths = Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
        var minLast = (int)(72 * s);
        var avail = ClientSize.Width;
        var fixedSum = _designWidths.Take(Columns.Count - 1).Sum();
        // narrow window: shrink the other columns in proportion so the last one stays visible (no sideways scroll)
        var factor = fixedSum + minLast > avail && fixedSum > 0 ? Math.Max(0.5, (avail - minLast) / (double)fixedSum) : 1.0;
        var used = 0;
        for (var i = 0; i < Columns.Count - 1; i++)
        {
            var w = (int)(_designWidths[i] * factor);
            if (Columns[i].Width != w) Columns[i].Width = w;
            used += w;
        }
        // explicit width a few px short of the edge: -2 (fill) overshoots by the frame and brings back sideways scrolling
        Columns[^1].Width = Math.Max(minLast, avail - used - (int)(4 * s));
    }
}

/// <summary>Mouse-wheel routing for value editors: the wheel never changes a value, it scrolls the page instead.
/// A wheel turned to scroll the window must not pick another preset or number (a closed combo with the focus used
/// to walk its list one item per notch and, on "(device default)", cleared the assignment).</summary>
internal static class WheelGuard
{
    public const int WmMouseWheel = 0x020A;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    /// <summary>Hands the wheel message to the parent; the first ancestor that scrolls takes it (unscrollable ones
    /// pass it up through DefWindowProc).</summary>
    public static void ToParent(Control c, ref Message m)
    {
        if (c.Parent is { IsHandleCreated: true } p) m.Result = SendMessage(p.Handle, m.Msg, m.WParam, m.LParam);
    }
}

/// <summary>Combo box whose closed list ignores the wheel (an open list still scrolls with it).</summary>
internal sealed class WheelSafeComboBox : ComboBox
{
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WheelGuard.WmMouseWheel && !DroppedDown)
        {
            WheelGuard.ToParent(this, ref m);
            return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>Number box whose value ignores the wheel; arrows, keyboard and typing still change it.</summary>
internal sealed class WheelSafeNumericUpDown : NumericUpDown
{
    // not calling base leaves the event unhandled, so Control.WmMouseWheel passes it to DefWindowProc → parent
    protected override void OnMouseWheel(MouseEventArgs e)
    {
    }
}

/// <summary>Layout helpers shared by the pages and dialogs.</summary>
internal static class Ui
{
    public static Label Text(string text, Font? font = null, bool wrap = false, int maxWidth = 640) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Theme.Body,
        Margin = new Padding(0, 0, 0, Theme.S1),
        MaximumSize = wrap ? new Size(maxWidth, 0) : Size.Empty,
        UseMnemonic = false,
    };

    public static Label Hint(string text, int maxWidth = 640) => Text(text, Theme.Caption, true, maxWidth);

    public static Label SectionTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Theme.Section,
        Margin = new Padding(0, Theme.S2, 0, Theme.S2),
        UseMnemonic = false,
    };

    public static Button Button(string text, Action onClick, string? accessibleName = null)
    {
        var b = new ThemedButton
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(88, 32),
            Padding = new Padding(Theme.S2, 0, Theme.S2, 0),
            Margin = new Padding(0, 0, Theme.S2, 0),
            Font = Theme.Body,
            UseMnemonic = false,
        };
        if (accessibleName is not null) b.AccessibleName = accessibleName;
        b.Click += (_, _) => onClick();
        return b;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = new Padding(0, Theme.S2, 0, 0),
            Padding = Padding.Empty,
            Dock = DockStyle.Top,
        };
        f.Controls.AddRange(controls);
        return f;
    }

    /// <summary>Two-column form grid: label + editor (fill). <paramref name="labelWidth"/> fixes the label column
    /// (logical px) so several grids line up; null = as wide as the longest label.</summary>
    public static TableLayoutPanel Grid(int? labelWidth = null)
    {
        var t = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        t.ColumnStyles.Add(labelWidth is { } w ? new ColumnStyle(SizeType.Absolute, w) : new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    /// <summary>A text box inside its themed frame (dark-safe border, focus underline).</summary>
    public static InputFrame Field(TextBoxBase box) => new(box);

    /// <summary>True for editors that show text, not a field: they align on the label's text line.</summary>
    private static bool IsTextValue(Control c) => c is Label ||
        (c is FlowLayoutPanel f && f.Controls.Count > 0 && f.Controls.Cast<Control>().All(x => x is Label or Dot));

    /// <summary>Adds a label + editor row (a bare text box gets its frame). Returns the label and the control placed
    /// in the editor column.</summary>
    public static (Label Label, Control Host) AddField(TableLayoutPanel grid, string label, Control editor, string? hint = null)
    {
        if (editor is TextBoxBase bare) editor = Field(bare);
        var row = grid.RowCount;
        grid.RowCount = row + 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label
        {
            Text = label,
            AutoSize = true,
            Font = Theme.Body,
            Margin = new Padding(0, 6, Theme.S4, Theme.S2),
            UseMnemonic = false,
        };
        // text values sit on the label's line (same top margin); fields and combos start 4 px higher to centre
        var top = IsTextValue(editor) ? 6 : 2;
        editor.Margin = new Padding(0, top, 0, Theme.S2);
        var named = editor is InputFrame fr ? fr.Inner : editor;
        if (string.IsNullOrEmpty(named.AccessibleName)) named.AccessibleName = label.TrimEnd(':');
        grid.Controls.Add(l, 0, row);
        if (hint is null)
        {
            grid.Controls.Add(editor, 1, row);
            return (l, editor);
        }
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Dock = DockStyle.Fill };
        editor.Margin = new Padding(0, top, 0, 0);
        stack.Controls.Add(editor);
        var h = Hint(hint, 520);
        h.Margin = new Padding(0, 2, 0, Theme.S2);
        stack.Controls.Add(h);
        grid.Controls.Add(stack, 1, row);
        return (l, stack);
    }

    public static ComboBox Combo(IEnumerable<object> items, int width = 220, string? accessibleName = null)
    {
        var c = new WheelSafeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Font = Theme.Body, FlatStyle = FlatStyle.Flat };
        foreach (var i in items) c.Items.Add(i);
        if (accessibleName is not null) c.AccessibleName = accessibleName;
        return c;
    }

    public static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals = 0, decimal increment = 1, string? accessibleName = null)
    {
        var n = new WheelSafeNumericUpDown
        {
            Minimum = min,
            Maximum = max,
            DecimalPlaces = decimals,
            Increment = increment,
            Width = 96,
            Font = Theme.Body,
            TextAlign = HorizontalAlignment.Right,
        };
        n.Value = Math.Clamp(value, min, max);
        if (accessibleName is not null) n.AccessibleName = accessibleName;
        return n;
    }

    /// <summary>Value + name pair for combo boxes.</summary>
    public sealed record Choice(string Value, string Display)
    {
        public override string ToString() => Display;
    }
}
