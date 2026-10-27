using System.Text.RegularExpressions;
using FxAuto.Core.Config;

namespace FxAuto.Core.Classification;

/// <summary>REQ-102 classifier: game sources by exe then folder (minus <c>gameFolderExclusions</c>, fallback loses to
/// specific), app sources by AUMID then exe, browsers by title segment (site sources, optional regex refinement),
/// else the browser source's default category. Inside one source: AND
/// across criteria, OR inside a list. <see cref="SourceClass.How"/> never contains a window title (privacy).</summary>
public sealed class SourceClassifier : ISourceClassifier
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly FxAutoConfig _cfg;
    private readonly Dictionary<string, Regex?> _regexes = new(StringComparer.Ordinal);

    public SourceClassifier(FxAutoConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _cfg = cfg;
        foreach (var s in cfg.Sources)
        {
            var rx = s.Match?.TitleRegex;
            if (string.IsNullOrWhiteSpace(rx) || _regexes.ContainsKey(rx)) continue;
            try
            {
                _regexes[rx] = new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException)
            {
                _regexes[rx] = null; // invalid: the validator reports it; here the source just never matches
            }
        }
    }

    public SourceClass Classify(SourceContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var exe = FileName(ctx.Exe) ?? FileName(ctx.ExePath);
        var path = ctx.ExePath?.Replace('/', '\\');

        // 1. Games, 2. apps (exe/folder/AUMID, AND across criteria).
        var game = BestNonBrowser(SourceKind.Game, exe, path, ctx);
        if (game is not null) return game;
        var app = BestNonBrowser(SourceKind.App, exe, path, ctx);
        if (app is not null) return app;

        // 3. Browsers: title classification over site sources, else the browser default.
        var browser = FindBrowser(exe, path);
        if (browser is not null)
        {
            var title = TabSelection.TitleFor(ctx);
            if (!string.IsNullOrWhiteSpace(title))
            {
                var site = BestSite(title, exe);
                if (site is not null) return site;
            }
            return Make(browser, browser.Category, null, "browser default", isBrowser: true);
        }

        return new SourceClass(Categories.Unknown, null, null, "no match", false, false);
    }

    /// <summary>Default category of the browser source matching <paramref name="exe"/> (null = not a browser).</summary>
    public SourceClass? BrowserDefault(string? exe)
    {
        var b = FindBrowser(FileName(exe), null);
        return b is null ? null : Make(b, b.Category, null, "browser default", isBrowser: true);
    }

    /// <summary>True when <paramref name="exe"/> matches a configured browser source.</summary>
    public bool IsBrowser(string? exe) => FindBrowser(FileName(exe), null) is not null;

    private SourceClass? BestNonBrowser(SourceKind kind, string? exe, string? path, SourceContext ctx)
    {
        (SourceConfig Src, int Rank, int Index, string How)? best = null;
        for (int i = 0; i < _cfg.Sources.Count; i++)
        {
            var s = _cfg.Sources[i];
            if (s.Kind != kind || s.Match is null) continue;
            var how = MatchNonBrowser(s.Match, exe, path, ctx);
            if (how is null) continue;
            // Rank (lower wins): specific before fallback; games: exe before folder-only; apps: AUMID before exe.
            int rank = (s.Fallback ? 10 : 0) + (kind == SourceKind.Game
                ? (how.Contains("exe", StringComparison.Ordinal) ? 0 : 1)
                : (how.Contains("aumid", StringComparison.Ordinal) ? 0 : 1));
            if (best is null || rank < best.Value.Rank) best = (s, rank, i, how);
        }
        if (best is null) return null;
        var b = best.Value;
        var prefix = kind == SourceKind.Game ? "game " : "app ";
        return Make(b.Src, b.Src.Category, null, prefix + b.How, isBrowser: false);
    }

    /// <summary>True when the window title can change the classification of <paramref name="ctx"/>: it is a browser,
    /// or a game/app source with a <c>titleRegex</c> matches it on its other criteria. When false, title changes of
    /// that process can be ignored (CPU).</summary>
    public bool TitleMatters(SourceContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var exe = FileName(ctx.Exe) ?? FileName(ctx.ExePath);
        var path = ctx.ExePath?.Replace('/', '\\');
        if (FindBrowser(exe, path) is not null) return true;
        foreach (var s in _cfg.Sources)
        {
            if (s.Kind is not (SourceKind.Game or SourceKind.App) || s.Match is null) continue;
            if (string.IsNullOrWhiteSpace(s.Match.TitleRegex)) continue;
            if (MatchNonBrowser(s.Match, exe, path, ctx, checkTitle: false) is not null) return true;
        }
        return false;
    }

    private string? MatchNonBrowser(SourceMatch m, string? exe, string? path, SourceContext ctx, bool checkTitle = true)
    {
        var how = new List<string>(4);
        if (m.Exe is { Count: > 0 })
        {
            if (exe is null || !m.Exe.Any(e => ExeMatches(e, exe))) return null;
            how.Add("exe");
        }
        if (m.Folder is { Count: > 0 })
        {
            if (path is null) return null;
            if (!m.Folder.Any(f => !string.IsNullOrWhiteSpace(f)
                && path.Contains(f.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))) return null;
            if (IsFolderExcluded(exe, path)) return null;
            how.Add("folder");
        }
        if (m.Aumid is { Count: > 0 })
        {
            if (ctx.Aumid is null || !m.Aumid.Any(a => string.Equals(a, ctx.Aumid, StringComparison.OrdinalIgnoreCase)))
                return null;
            how.Add("aumid");
        }
        if (!string.IsNullOrWhiteSpace(m.TitleRegex))
        {
            if (!checkTitle) { how.Add("title regex"); return string.Join('+', how); }
            if (string.IsNullOrEmpty(ctx.ActiveTitle) || !RegexMatches(m.TitleRegex, ctx.ActiveTitle)) return null;
            how.Add("title regex");
        }
        return how.Count == 0 ? null : string.Join('+', how);
    }

    private SourceConfig? FindBrowser(string? exe, string? path)
    {
        if (exe is null) return null;
        foreach (var s in _cfg.Sources)
        {
            if (s.Kind != SourceKind.Browser || s.Match is null) continue;
            if (s.Match.Exe is { Count: > 0 } && s.Match.Exe.Any(e => ExeMatches(e, exe))) return s;
            if (s.Match.Exe is not { Count: > 0 } && path is not null && s.Match.Folder is { Count: > 0 }
                && s.Match.Folder.Any(f => path.Contains(f.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)))
                return s;
        }
        return null;
    }

    /// <summary>Specificity: site+regex (3) &gt; *+regex (2) &gt; site (1); ties by list order.</summary>
    private SourceClass? BestSite(string title, string? exe)
    {
        var stripped = TitleSegments.StripBrowserSuffix(title);
        var segments = TitleSegments.Split(stripped);
        (SourceConfig Src, int Score, string? Site, string How)? best = null;
        foreach (var s in _cfg.Sources)
        {
            if (s.Kind != SourceKind.Site || s.Match is null) continue;
            var m = s.Match;
            if (m.Exe is { Count: > 0 } && (exe is null || !m.Exe.Any(e => ExeMatches(e, exe)))) continue;
            var sites = m.Site ?? [];
            bool star = sites.Any(x => TitleSegments.NormalizeSite(x) == "*");
            string? matchedSite = null;
            foreach (var raw in sites)
            {
                var site = TitleSegments.NormalizeSite(raw);
                if (site.Length == 0 || site == "*") continue;
                if (segments.Any(seg => string.Equals(seg, site, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedSite = site;
                    break;
                }
            }
            bool hasRegex = !string.IsNullOrWhiteSpace(m.TitleRegex);
            int score;
            string how;
            if (matchedSite is not null && hasRegex)
            {
                if (!RegexMatches(m.TitleRegex!, stripped)) continue;
                score = 3; how = "site segment+title regex";
            }
            else if (matchedSite is not null)
            {
                score = 1; how = "site segment";
            }
            else if (star && hasRegex)
            {
                if (!RegexMatches(m.TitleRegex!, stripped)) continue;
                score = 2; how = "title regex";
            }
            else continue;
            if (best is null || score > best.Value.Score) best = (s, score, matchedSite, how);
        }
        if (best is null) return null;
        var b = best.Value;
        return Make(b.Src, b.Src.Category, b.Site, b.How, isBrowser: true);
    }

    private bool RegexMatches(string pattern, string text)
    {
        if (!_regexes.TryGetValue(pattern, out var rx))
        {
            try
            {
                rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException)
            {
                rx = null;
            }
            _regexes[pattern] = rx;
        }
        if (rx is null) return false;
        try
        {
            return rx.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private bool IsFolderExcluded(string? exe, string path)
    {
        foreach (var pattern in _cfg.GameFolderExclusions ?? [])
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            if (exe is not null && Wildcard(pattern, exe)) return true;
            if (Wildcard("*" + pattern.Replace('/', '\\') + "*", path)) return true;
        }
        return false;
    }

    private static bool ExeMatches(string pattern, string exe)
        => pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal)
            ? Wildcard(pattern, exe)
            : string.Equals(pattern, exe, StringComparison.OrdinalIgnoreCase);

    private static SourceClass Make(SourceConfig s, string category, string? site, string how, bool isBrowser)
        => new(category, s.Id, site, how, s.Kind == SourceKind.Game || Categories.IsGame(category), isBrowser);

    private static string? FileName(string? exeOrPath)
    {
        if (string.IsNullOrWhiteSpace(exeOrPath)) return null;
        var t = exeOrPath.Replace('/', '\\');
        var i = t.LastIndexOf('\\');
        return i >= 0 ? t[(i + 1)..] : t;
    }

    /// <summary>Wildcards * and ?, case-insensitive.</summary>
    internal static bool Wildcard(string pattern, string text)
    {
        int p = 0, s = 0, star = -1, mark = 0;
        while (s < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[s])))
            {
                p++; s++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++; mark = s;
            }
            else if (star >= 0)
            {
                p = star + 1; s = ++mark;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

/// <summary>REQ-103 choice of the tab title to classify from UIA tabs (shared by the classifier and the tracker).
/// Exactly one audible tab → that one; several → the audible tab whose title is the active title (selected tab of the
/// most recently focused window), else the preferred (latched) title if still audible, else an audible selected tab,
/// else the first audible; no audible tab → null.</summary>
internal static class TabSelection
{
    public static string? AudibleTitle(IReadOnlyList<BrowserTab>? tabs, string? activeTitle, string? preferredTitle)
    {
        if (tabs is null) return null;
        var audible = tabs.Where(t => t.Audible && !string.IsNullOrWhiteSpace(t.Title)).ToList();
        if (audible.Count == 0) return null;
        if (audible.Count == 1) return audible[0].Title;
        if (activeTitle is not null)
        {
            var a = audible.FirstOrDefault(t => SameTitle(t.Title, activeTitle));
            if (a is not null) return a.Title;
        }
        if (preferredTitle is not null)
        {
            var p = audible.FirstOrDefault(t => SameTitle(t.Title, preferredTitle));
            if (p is not null) return p.Title;
        }
        return (audible.FirstOrDefault(t => t.Selected) ?? audible[0]).Title;
    }

    /// <summary>Title the classifier uses for a context: the UIA audible tab if any, else the active title.</summary>
    public static string? TitleFor(SourceContext ctx) => AudibleTitle(ctx.Tabs, ctx.ActiveTitle, null) ?? ctx.ActiveTitle;

    private static bool SameTitle(string a, string b)
        => string.Equals(TitleSegments.StripBrowserSuffix(a), TitleSegments.StripBrowserSuffix(b), StringComparison.OrdinalIgnoreCase);
}
