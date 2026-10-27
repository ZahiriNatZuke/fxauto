using FxAuto.App;
using FxAuto.Core.Config;

namespace FxAuto.Ui.Settings;

/// <summary>One section of the settings window. Pages read and edit the window's working copy through
/// <see cref="Window"/>; they never call the host for config changes themselves.</summary>
internal abstract class SettingsPageBase : UserControl
{
    protected SettingsPageBase(SettingsForm window)
    {
        Window = window;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        Font = Theme.Body;
        // below this the list pages would overlap their buttons; the page host scrolls instead
        MinimumSize = new Size(0, 380);
    }

    protected SettingsForm Window { get; }

    protected ISettingsHost Host => Window.Host;

    protected FxAutoConfig Working => Window.Working;

    protected Palette P => Window.Palette;

    public abstract SettingsPage Page { get; }

    public abstract string Heading { get; }

    /// <summary>One line under the heading.</summary>
    public virtual string? Subheading => null;

    /// <summary>Reloads the controls from the working copy (after open, rebase, apply or a preset operation).</summary>
    public abstract void Bind();

    /// <summary>The live status changed.</summary>
    public virtual void OnStatusChanged() { }

    /// <summary>Selects an item ("configure" notification, [Fix]): an endpoint id, a config path…</summary>
    public virtual void FocusItem(string id) { }

    /// <summary>Single-column layout. <paramref name="fillRow"/> is the row that takes the remaining height
    /// (a list); -1 = everything auto-sized inside a scrolling panel.</summary>
    protected static TableLayoutPanel Stack(int rows, int fillRow = -1)
    {
        var t = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = rows,
            Dock = fillRow >= 0 ? DockStyle.Fill : DockStyle.Top,
            AutoSize = fillRow < 0,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < rows; i++)
            t.RowStyles.Add(i == fillRow ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        return t;
    }

    /// <summary>Puts an auto-sized stack in a vertically scrolling panel.</summary>
    protected Panel Scrolling(TableLayoutPanel stack)
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, Padding = new Padding(0, 0, Theme.S2, 0) };
        panel.Controls.Add(stack);
        return panel;
    }

    /// <summary>A card of auto-sized rows (a list keeps its own height).</summary>
    protected static Card CardOf(params Control[] rows) => MakeCard(false, rows);

    /// <summary>A card whose list takes the remaining height of the page.</summary>
    protected static Card FillCard(params Control[] rows) => MakeCard(true, rows);

    private static Card MakeCard(bool fill, Control[] rows)
    {
        var fillRow = fill ? Array.FindIndex(rows, r => r is ListView) : -1;
        var inner = Stack(rows.Length, fillRow);
        inner.Dock = fill ? DockStyle.Fill : DockStyle.Top;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row is ListView lv)
            {
                // lists get the themed frame (their native border is pure white in dark mode)
                var height = lv.Height;
                row = new InputFrame(lv) { Height = height + 2 };
            }
            if (row is Label) { }
            else if (rows[i] is ListView && !fill) row.Dock = DockStyle.Top;
            else row.Dock = DockStyle.Fill;
            inner.Controls.Add(row, 0, i);
        }
        var card = new Card { Dock = DockStyle.Fill };
        if (fillRow >= 0)
        {
            card.AutoSize = false;
            card.Margin = Padding.Empty;
        }
        card.Controls.Add(inner);
        return card;
    }

    /// <summary>Presets offered in pickers: FxSound's user presets, then built-ins, then any name the config uses
    /// that FxSound does not have (kept so the user can see and replace it).</summary>
    protected IReadOnlyList<string> PresetChoices()
    {
        var fx = Window.Fx;
        var list = new List<string>();
        if (fx is not null)
        {
            list.AddRange(fx.UserPresets.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            list.AddRange(fx.BuiltInPresets.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        }
        foreach (var p in ConfigEdits.ReferencedPresets(Working))
            if (!list.Contains(p, StringComparer.Ordinal)) list.Add(p);
        return list;
    }

    /// <summary>"devices[3].tag" → 3 when the path starts with <paramref name="block"/>.</summary>
    protected static int? IndexFromPath(string path, string block)
    {
        if (!path.StartsWith(block + "[", StringComparison.Ordinal)) return null;
        var end = path.IndexOf(']', block.Length + 1);
        return end > 0 && int.TryParse(path.AsSpan(block.Length + 1, end - block.Length - 1), out var i) ? i : null;
    }
}
