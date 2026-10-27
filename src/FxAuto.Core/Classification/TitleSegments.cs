using System.Text.RegularExpressions;

namespace FxAuto.Core.Classification;

/// <summary>Title helpers for REQ-102/103: a site matches a title only as a whole separator-delimited segment,
/// never as a loose substring (lesson: "SPOTIFY CAMP NOU" is not Spotify).</summary>
public static partial class TitleSegments
{
    /// <summary>Segment separators, in the order they are tried (longest first is not needed: all are distinct).</summary>
    public static readonly IReadOnlyList<string> Separators = [" - ", " – ", " — ", " | ", " • ", " · "];

    private static readonly char[] SeparatorChars = ['-', '–', '—', '|', '•', '·'];

    // Browser names appended by the browser to the window title. Edge puts a zero-width space in "Microsoft​ Edge";
    // it is removed before comparing.
    private static readonly string[] BrowserNames =
    [
        "Google Chrome", "Microsoft Edge", "Mozilla Firefox", "Firefox", "Brave", "Opera", "Vivaldi", "Chromium",
    ];

    /// <summary>Removes the browser suffix ("… - Google Chrome", "… — Mozilla Firefox") and zero-width spaces.</summary>
    public static string StripBrowserSuffix(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var t = title.Replace("​", "", StringComparison.Ordinal).Trim();
        foreach (var sep in Separators)
        {
            foreach (var name in BrowserNames)
            {
                var suffix = sep + name;
                if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return t[..^suffix.Length].TrimEnd();
            }
        }
        return t;
    }

    /// <summary>Splits a title (browser suffix already stripped or not) into trimmed, non-empty segments.</summary>
    public static IReadOnlyList<string> Split(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var parts = new List<string> { title };
        foreach (var sep in Separators)
        {
            var next = new List<string>(parts.Count + 2);
            foreach (var p in parts) next.AddRange(p.Split(sep));
            parts = next;
        }
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    /// <summary>Normalises a configured site value: trims spaces and separators, so " - YouTube Music" and
    /// "YouTube Music" are the same site.</summary>
    public static string NormalizeSite(string site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Trim().Trim(SeparatorChars).Trim();
    }

    /// <summary>True when one whole segment of <paramref name="title"/> equals <paramref name="site"/>
    /// (case-insensitive). "*" never matches here (it is only meaningful together with a title regex).</summary>
    public static bool HasSegment(string title, string site)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(site);
        var s = NormalizeSite(site);
        if (s.Length == 0 || s == "*") return false;
        foreach (var seg in Split(StripBrowserSuffix(title)))
            if (string.Equals(seg, s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True when a UIA accessible name of a tab contains one of the audible markers (substring,
    /// case-insensitive; e.g. "reproducción de audio", "Audio playing"). Spike 4c: never a suffix match.</summary>
    public static bool IsAudibleTabName(string accessibleName, IReadOnlyList<string> markers)
    {
        ArgumentNullException.ThrowIfNull(accessibleName);
        ArgumentNullException.ThrowIfNull(markers);
        foreach (var m in markers)
            if (!string.IsNullOrWhiteSpace(m) && accessibleName.Contains(m.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Best-effort cleanup of a UIA tab accessible name into a tab title: removes the audible markers and a
    /// trailing memory size (Chrome "…: reproducción de audio: 629 MB"), so the site segment is the last segment
    /// again. Does not try to remove localized prefixes ("Uso de memoria de …"): they only touch the first segment.</summary>
    public static string CleanTabName(string accessibleName, IReadOnlyList<string> markers)
    {
        ArgumentNullException.ThrowIfNull(accessibleName);
        ArgumentNullException.ThrowIfNull(markers);
        var t = accessibleName;
        foreach (var raw in markers)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var m = raw.Trim();
            int i;
            while ((i = t.IndexOf(m, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var start = i;
                // swallow the separator in front of the marker (": " or " - ")
                var before = t[..start].TrimEnd();
                if (before.EndsWith(':') || before.EndsWith('-') || before.EndsWith(',')) start = before.Length - 1;
                t = t[..start] + t[(i + m.Length)..];
            }
        }
        t = MemorySuffix().Replace(t, "");
        return t.Trim().TrimEnd(':', ',').Trim();
    }

    [GeneratedRegex(@"(\s*[:\-,]\s*)?\d+(?:[.,]\d+)?\s*(?:KB|MB|GB)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MemorySuffix();
}
