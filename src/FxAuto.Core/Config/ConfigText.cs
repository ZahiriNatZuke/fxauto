using System.Globalization;
using System.Text;
using FxAuto.Core.Presets;

namespace FxAuto.Core.Config;

/// <summary>Small text helpers shared by the config code.</summary>
internal static class ConfigText
{
    /// <summary>"Reproductores de música" → "reproductores-de-musica". Never empty ("item" as a last resort).</summary>
    public static string Slug(string text)
    {
        var decomposed = (text ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var dash = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch < 128 && char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                dash = false;
            }
            else if (ch == '+' && sb.Length > 0)
            {
                if (!dash) sb.Append('-');
                sb.Append("plus");
                dash = false;
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "item" : s;
    }

    /// <summary>Returns <paramref name="baseId"/> or <c>baseId-2</c>, <c>-3</c>… not present in <paramref name="taken"/>,
    /// and adds it.</summary>
    public static string UniqueId(string baseId, ISet<string> taken)
    {
        var id = baseId;
        for (var n = 2; taken.Contains(id); n++) id = $"{baseId}-{n}";
        taken.Add(id);
        return id;
    }

    /// <summary>Syntax checks of a preset name referenced by the config. FxSound-side rules for new names (sanitising,
    /// clashes, limit) live in <see cref="PresetNameRules.Validate"/>.</summary>
    public static void CheckPresetName(string? name, string path, List<ConfigIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(ConfigIssue.Of(IssueLevel.Error, path, "Issue.Text.01"));
            return;
        }
        if (name.Length > PresetNameRules.MaxLength)
            issues.Add(ConfigIssue.Of(IssueLevel.Error, path, "Issue.Text.02", name, PresetNameRules.MaxLength));
        if (name.Any(char.IsControl))
            issues.Add(ConfigIssue.Of(IssueLevel.Error, path, "Issue.Text.03", name));
        if (name.IndexOfAny(PresetNameRules.Forbidden.ToCharArray()) >= 0)
            issues.Add(ConfigIssue.Of(IssueLevel.Warning, path, "Issue.Text.04", name));
        if (name != name.Trim())
            issues.Add(ConfigIssue.Of(IssueLevel.Warning, path, "Issue.Text.05", name));
    }
}

/// <summary>Light structural reading of .NET regular expressions (no full parser): top-level alternatives,
/// literal detection and the "bounded" rule for <c>site: ["*"]</c> regexes.</summary>
internal static class RegexText
{
    private const string MetaChars = "[](){}.*+?^$|\\";

    /// <summary>Splits a pattern on the <c>|</c> that are outside groups and character classes.</summary>
    public static IReadOnlyList<string> TopLevelAlternatives(string pattern)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                sb.Append(c).Append(pattern[++i]);
                continue;
            }
            if (inClass)
            {
                if (c == ']') inClass = false;
                sb.Append(c);
                continue;
            }
            switch (c)
            {
                case '[': inClass = true; break;
                case '(': depth++; break;
                case ')': depth = Math.Max(0, depth - 1); break;
                case '|' when depth == 0:
                    parts.Add(sb.ToString());
                    sb.Clear();
                    continue;
            }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>A pattern is bounded when every top-level alternative contains a word boundary (<c>\b</c>), an
    /// anchor, whitespace or an explicit title separator. It keeps a "*" site regex from matching inside words
    /// ("SPOTIFY CAMP NOU" lesson).</summary>
    public static bool IsBounded(string pattern)
        => TopLevelAlternatives(pattern).All(AlternativeIsBounded);

    private static bool AlternativeIsBounded(string alt)
    {
        if (alt.Length == 0) return false;
        var inClass = false;
        for (var i = 0; i < alt.Length; i++)
        {
            var c = alt[i];
            if (c == '\\' && i + 1 < alt.Length)
            {
                var n = alt[++i];
                if (!inClass && (n is 'b' or 's' or 'A' or 'z' or 'Z' or '|' or '-')) return true;
                continue;
            }
            if (inClass)
            {
                if (c == ']') inClass = false;
                else if (c is ' ' or '–' or '—' or '•' or '·' or '|') return true;
                continue;
            }
            if (c == '[') { inClass = true; continue; }
            if (c is '^' or '$' or ' ' or '–' or '—' or '•' or '·') return true;
        }
        return false;
    }

    /// <summary>True when <paramref name="alt"/> is a plain literal (escaped punctuation allowed); returns it unescaped.</summary>
    public static bool TryLiteral(string alt, out string literal)
    {
        var sb = new StringBuilder(alt.Length);
        literal = "";
        for (var i = 0; i < alt.Length; i++)
        {
            var c = alt[i];
            if (c == '\\')
            {
                if (i + 1 >= alt.Length) return false;
                var n = alt[++i];
                if (char.IsLetterOrDigit(n)) return false;   // \d, \b, \s… are not literals
                sb.Append(n);
                continue;
            }
            if (MetaChars.Contains(c, StringComparison.Ordinal)) return false;
            sb.Append(c);
        }
        literal = sb.ToString();
        return literal.Length > 0;
    }
}
