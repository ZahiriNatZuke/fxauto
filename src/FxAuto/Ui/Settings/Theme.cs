using System.Drawing.Drawing2D;

namespace FxAuto.Ui.Settings;

/// <summary>Severity used by info bars, issue rows and status dots.</summary>
internal enum Tone { Neutral, Info, Success, Warning, Error }

/// <summary>Colour tokens of one theme (design-notes.md: Windows 11 neutrals, one accent, semantic states).</summary>
internal sealed record Palette(
    bool Dark,
    Color Window,          // window ground (nav pane and page background)
    Color Card,            // grouped content surface
    Color CardBorder,      // 1 px stroke around cards and lists
    Color Divider,         // separators inside cards
    Color Text,            // primary text
    Color TextSecondary,   // labels, hints, captions (≥ 4.5:1 on Window and Card)
    Color TextDisabled,
    Color Accent,          // primary action, selection indicator, links
    Color OnAccent,        // text on Accent
    Color NavHover,
    Color NavSelected,
    Color InputBack,
    Color InputBorder,     // 1 px stroke around text fields and lists
    Color Success, Color Warning, Color Error,
    Color InfoBack, Color SuccessBack, Color WarningBack, Color ErrorBack)
{
    public Color ToneColor(Tone t) => t switch
    {
        Tone.Success => Success,
        Tone.Warning => Warning,
        Tone.Error => Error,
        Tone.Info => Accent,
        _ => TextSecondary,
    };

    public Color ToneBack(Tone t) => t switch
    {
        Tone.Success => SuccessBack,
        Tone.Warning => WarningBack,
        Tone.Error => ErrorBack,
        _ => InfoBack,
    };
}

/// <summary>Design tokens of the settings window: palettes, type ramp and spacing scale. Read once when the window
/// opens (the window is short-lived, so a Windows theme change applies the next time it opens).</summary>
internal static class Theme
{
    public static readonly Palette Light = new(
        Dark: false,
        Window: Rgb(0xF3F3F3), Card: Rgb(0xFBFBFB), CardBorder: Rgb(0xE0E0E0), Divider: Rgb(0xEAEAEA),
        Text: Rgb(0x1A1A1A), TextSecondary: Rgb(0x5C5C5C), TextDisabled: Rgb(0x9A9A9A),
        Accent: Rgb(0x005FB8), OnAccent: Color.White,
        NavHover: Rgb(0xEAEAEA), NavSelected: Rgb(0xE4E4E4), InputBack: Color.White, InputBorder: Rgb(0xE0E0E0),
        Success: Rgb(0x0F7B0F), Warning: Rgb(0x8A5300), Error: Rgb(0xC42B1C),
        InfoBack: Rgb(0xEAF2FB), SuccessBack: Rgb(0xDFF6DD), WarningBack: Rgb(0xFFF4CE), ErrorBack: Rgb(0xFDE7E9));

    public static readonly Palette DarkPalette = new(
        Dark: true,
        Window: Rgb(0x202020), Card: Rgb(0x2B2B2B), CardBorder: Rgb(0x3D3D3D), Divider: Rgb(0x353535),
        Text: Rgb(0xF5F5F5), TextSecondary: Rgb(0xC5C5C5), TextDisabled: Rgb(0x7A7A7A),
        Accent: Rgb(0x60CDFF), OnAccent: Rgb(0x000000),
        NavHover: Rgb(0x2D2D2D), NavSelected: Rgb(0x333333), InputBack: Rgb(0x1F1F1F), InputBorder: Rgb(0x3D3D3D),
        Success: Rgb(0x6CCB5F), Warning: Rgb(0xFCE100), Error: Rgb(0xFF99A4),
        InfoBack: Rgb(0x26313B), SuccessBack: Rgb(0x2A3A27), WarningBack: Rgb(0x433519), ErrorBack: Rgb(0x442726));

    /// <summary>Palette that matches how WinForms paints the native controls right now.</summary>
    public static Palette Current => Application.IsDarkModeEnabled ? DarkPalette : Light;

    // ── spacing scale (logical pixels at 96 DPI; WinForms scales them with AutoScaleMode.Dpi) ──
    public const int S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 24, S6 = 32;

    // ── type ramp: one family (Segoe UI Variable, falling back to Segoe UI) ──
    private static readonly HashSet<string> Installed = LoadFamilies();

    public static readonly Font Body = Make(10f, FontStyle.Regular, "Segoe UI Variable Text", "Segoe UI");
    public static readonly Font BodyStrong = Make(10f, FontStyle.Regular, "Segoe UI Variable Text Semibold", "Segoe UI Semibold");
    public static readonly Font Caption = Make(9f, FontStyle.Regular, "Segoe UI Variable Small", "Segoe UI");
    public static readonly Font Section = Make(11.5f, FontStyle.Regular, "Segoe UI Variable Text Semibold", "Segoe UI Semibold");
    public static readonly Font Title = Make(17f, FontStyle.Regular, "Segoe UI Variable Display Semib", "Segoe UI Semibold");
    public static readonly Font Icon = Make(11f, FontStyle.Regular, "Segoe Fluent Icons", "Segoe MDL2 Assets");

    // Segoe Fluent Icons / MDL2 code points used by the navigation (same glyphs in both fonts).
    public const string IconStatus = "", IconDevices = "", IconPresets = "", IconSources = "",
        IconCategories = "", IconGeneral = "", IconAbout = "";

    private static Color Rgb(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private static HashSet<string> LoadFamilies()
    {
        using var fonts = new System.Drawing.Text.InstalledFontCollection();
        return new HashSet<string>(fonts.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
    }

    private static Font Make(float size, FontStyle style, string preferred, string fallback)
    {
        if (Installed.Contains(preferred)) return new Font(preferred, size, style, GraphicsUnit.Point);
        if (Installed.Contains(fallback)) return new Font(fallback, size, style, GraphicsUnit.Point);
        return new Font(FontFamily.GenericSansSerif, size, style == FontStyle.Regular && fallback.Contains("Semibold", StringComparison.Ordinal)
            ? FontStyle.Bold : style, GraphicsUnit.Point);
    }

    /// <summary>Applies window/card colours to a control tree: containers and labels get the palette; native input
    /// controls keep the colours WinForms dark mode gives them, except lists and text boxes, which get the input
    /// background so they read as fields.</summary>
    public static void Apply(Control root, Palette p, Color? ground = null)
    {
        var back = ground ?? p.Window;
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case Card card:
                    card.Palette = p;
                    Apply(card, p, p.Card);
                    continue;
                case NavList nav:
                    nav.Palette = p;
                    continue;
                case ThemedListView tlv:
                    tlv.Palette = p;
                    continue;
                case ThemedControl t:
                    t.Palette = p; // paints itself and its children (info bars keep their tone fill)
                    continue;
                case Label l:
                    l.BackColor = back;
                    if (l.Tag is not "keep-color") l.ForeColor = l.Font.Equals(Caption) ? p.TextSecondary : p.Text;
                    break;
                case ListView lv:
                    lv.BackColor = p.InputBack;
                    lv.ForeColor = p.Text;
                    break;
                case TextBoxBase tb:
                    tb.BackColor = p.InputBack;
                    tb.ForeColor = p.Text;
                    break;
                case NumericUpDown nud:
                    nud.BackColor = p.InputBack;
                    nud.ForeColor = p.Text;
                    break;
                case CheckBox or RadioButton:
                    c.BackColor = back;
                    c.ForeColor = p.Text;
                    break;
                case AccentButton ab:
                    ab.Palette = p;
                    break;
                case ThemedButton tbn:
                    tbn.Palette = p;
                    continue;
                case InputFrame frame:
                    frame.Palette = p;
                    Apply(frame, p, p.InputBack);
                    continue;
                case Button b:
                    b.BackColor = p.Dark ? Rgb(0x373737) : Color.White;
                    b.ForeColor = p.Text;
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = p.CardBorder;
                    b.FlatAppearance.MouseOverBackColor = p.Dark ? Rgb(0x3F3F3F) : Rgb(0xF6F6F6);
                    b.FlatAppearance.MouseDownBackColor = p.Dark ? Rgb(0x323232) : Rgb(0xF0F0F0);
                    b.FlatAppearance.BorderSize = 1;
                    break;
                case ComboBox cb:
                    cb.BackColor = p.InputBack;
                    cb.ForeColor = p.Text;
                    break;
                case Panel or TableLayoutPanel or FlowLayoutPanel or UserControl:
                    c.BackColor = back;
                    c.ForeColor = p.Text;
                    break;
            }
            if (c.HasChildren) Apply(c, p, back);
        }
    }

    /// <summary>Rounded rectangle path for cards and the selection pill.</summary>
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (d <= 0 || r.Width < d || r.Height < d)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Scale factor of <paramref name="c"/> (1.0 at 96 DPI).</summary>
    public static float Scale(Control c) => c.DeviceDpi / 96f;
}
