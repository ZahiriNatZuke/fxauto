using System.ComponentModel;

namespace FxAuto.Ui.Settings;

/// <summary>Secondary button drawn from the palette. Disabled = card fill, divider border and disabled text, so it
/// never looks clickable (the stock flat button keeps its enabled fill).</summary>
internal sealed class ThemedButton : Button
{
    private Palette _palette = Theme.Current;

    public ThemedButton()
    {
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        FlatAppearance.BorderSize = 1;
        Restyle();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Palette Palette
    {
        get => _palette;
        set { _palette = value; Restyle(); }
    }

    private void Restyle()
    {
        var p = _palette;
        var on = Enabled;
        BackColor = on ? (p.Dark ? Color.FromArgb(0x37, 0x37, 0x37) : Color.White) : p.Card;
        ForeColor = on ? p.Text : p.TextDisabled;
        FlatAppearance.BorderColor = on ? p.CardBorder : p.Divider;
        FlatAppearance.MouseOverBackColor = p.Dark ? Color.FromArgb(0x3F, 0x3F, 0x3F) : Color.FromArgb(0xF6, 0xF6, 0xF6);
        FlatAppearance.MouseDownBackColor = p.Dark ? Color.FromArgb(0x32, 0x32, 0x32) : Color.FromArgb(0xF0, 0xF0, 0xF0);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Restyle();
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        base.OnPaint(pevent);
        if (Enabled) return;
        var g = pevent.Graphics;
        g.Clear(_palette.Card);
        using (var pen = new Pen(_palette.Divider)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, _palette.TextDisabled,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Frame around a text box or list: 1 px <see cref="Palette.InputBorder"/> stroke (the native border is pure
/// white in dark mode), a bottom stroke for text fields (TextSecondary, Accent while focused) and an error state.</summary>
internal sealed class InputFrame : Panel
{
    private readonly bool _underline;
    private Palette _palette = Theme.Current;
    private bool _error;

    public InputFrame(Control inner)
    {
        Inner = inner;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
        if (inner is TextBoxBase tb)
        {
            _underline = true;
            tb.BorderStyle = BorderStyle.None;
            Padding = new Padding(7, 5, 6, 5);
            var h = tb is TextBox { Multiline: true } || tb is RichTextBox ? tb.Height : tb.PreferredHeight;
            Size = new Size(tb.Width + Padding.Horizontal, h + Padding.Vertical);
        }
        else
        {
            Padding = new Padding(1);
            if (inner is ListView lv) lv.BorderStyle = BorderStyle.None;
            Size = new Size(inner.Width + 2, inner.Height + 2);
        }
        Margin = inner.Margin;
        MinimumSize = inner.MinimumSize.IsEmpty ? Size.Empty : inner.MinimumSize + new Size(Padding.Horizontal, Padding.Vertical);
        inner.Margin = Padding.Empty;
        inner.Dock = DockStyle.Fill;
        Controls.Add(inner);
        inner.GotFocus += (_, _) => Invalidate();
        inner.LostFocus += (_, _) => Invalidate();
        BackColor = _palette.InputBack;
    }

    public Control Inner { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Palette Palette
    {
        get => _palette;
        set { _palette = value; BackColor = value.InputBack; Invalidate(); }
    }

    /// <summary>Error state: border and bottom stroke in the error colour.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Error
    {
        get => _error;
        set { if (_error == value) return; _error = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_palette.InputBack);
        var border = _error ? _palette.Error : _palette.InputBorder;
        using (var pen = new Pen(border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        if (!_underline && !_error) return;
        var focused = Inner.ContainsFocus;
        var stroke = _error ? _palette.Error : focused ? _palette.Accent : _palette.TextSecondary;
        var thick = _error || focused ? 2 : 1;
        using var b = new SolidBrush(stroke);
        g.FillRectangle(b, 0, Height - thick, Width, thick);
    }
}
