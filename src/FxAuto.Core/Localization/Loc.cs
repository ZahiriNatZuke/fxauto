using System.Globalization;
using System.Text.RegularExpressions;

namespace FxAuto.Core.Localization;

/// <summary>Text catalog of the user interface (English and Spanish). Only what the user sees goes through it: the
/// JSONL log, config.json keys and values, preset names and command-line arguments stay in English.</summary>
public static class Loc
{
    /// <summary>Config value: follow the Windows display language.</summary>
    public const string System = "system";
    public const string English = "en";
    public const string Spanish = "es";

    /// <summary>Values accepted in <c>general.language</c>.</summary>
    public static readonly IReadOnlyList<string> Choices = [System, English, Spanish];

    private static volatile string _language = English;

    /// <summary>Language of the text now: "en" or "es".</summary>
    public static string Language => _language;

    /// <summary>Raised after <see cref="SetLanguage"/> changes the language (any thread).</summary>
    public static event Action? LanguageChanged;

    /// <summary>"system" → Spanish when the Windows display language is es-*, otherwise English; "en"/"es" as is;
    /// anything else (unknown or missing) behaves like "system".</summary>
    public static string Resolve(string? setting, string? uiCultureName)
    {
        var s = setting?.Trim().ToLowerInvariant();
        if (s is English or Spanish) return s;
        var c = uiCultureName ?? "";
        return c.Equals(Spanish, StringComparison.OrdinalIgnoreCase) || c.StartsWith("es-", StringComparison.OrdinalIgnoreCase)
            ? Spanish : English;
    }

    /// <summary>Resolves <paramref name="setting"/> against the Windows display language of this process.</summary>
    public static string ResolveForThisMachine(string? setting) => Resolve(setting, CultureInfo.CurrentUICulture.Name);

    /// <summary>Sets the language ("en"/"es"; anything else = English).</summary>
    public static void SetLanguage(string language)
    {
        var next = language == Spanish ? Spanish : English;
        if (next == _language) return;
        _language = next;
        LanguageChanged?.Invoke();
    }

    /// <summary>Catalog of the language now (falls back to English, then to the key itself). Catalog texts are
    /// composite format strings: literal braces are doubled; use <see cref="F"/> (or T for texts without braces).</summary>
    public static string T(string key)
    {
        var text = Raw(key);
        return text.Contains("{{", StringComparison.Ordinal) || text.Contains("}}", StringComparison.Ordinal)
            ? text.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal) : text;
    }

    private static string Raw(string key)
    {
        if (_language == Spanish && Catalog.Spanish.TryGetValue(key, out var es)) return es;
        return Catalog.English.TryGetValue(key, out var en) ? en : key;
    }

    /// <summary>Formatted text of the language now. <see cref="Msg"/> arguments are rendered too.</summary>
    public static string F(string key, params object?[] args) => Format(Raw(key), args);

    /// <summary>Text of a structured message in the language now.</summary>
    public static string Render(Msg message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Format(Raw(message.Key), message.ArgArray);
    }

    /// <summary>English text of a structured message (whatever the language now).</summary>
    public static string RenderEnglish(Msg message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var args = message.Args.Select(a => a is Msg m ? RenderEnglish(m) : a).ToArray();
        var text = Catalog.English.TryGetValue(message.Key, out var en) ? en : message.Key;
        return string.Format(CultureInfo.InvariantCulture, text, args);
    }

    /// <summary>Message text when there is one, otherwise the English text (older producers, tests).</summary>
    public static string Show(Msg? message, string fallback) => message is null ? fallback : Render(message);

    /// <summary>The catalog has this key.</summary>
    public static bool Has(string key) => Catalog.English.ContainsKey(key);

    /// <summary>How a source was matched ("app exe+folder", "browser default", "site segment"…) in the language now.
    /// The classifier keeps these short English terms for the log; unknown terms are shown as they are.</summary>
    public static string How(string? how)
    {
        if (string.IsNullOrEmpty(how)) return "";
        if (Has("How." + how)) return T("How." + how);
        var space = how.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0 && Has("How." + how[..space] + " {0}"))
            return F("How." + how[..space] + " {0}", How(how[(space + 1)..]));
        return string.Join(" + ", how.Split('+').Select(t => Has("How." + t) ? T("How." + t) : t));
    }

    /// <summary>Prefixes of catalog keys whose English text arrives as a plain string (preset operation errors,
    /// FxSound failure reasons) and is matched back by <see cref="Known"/>.</summary>
    public static readonly IReadOnlyList<string> KnownPrefixes = ["Op.", "Fail."];

    private static readonly Lazy<(string Key, Regex Pattern)[]> KnownPatterns = new(() => Catalog.English
        .Where(kv => KnownPrefixes.Any(p => kv.Key.StartsWith(p, StringComparison.Ordinal)))
        .OrderByDescending(kv => kv.Value.Length)
        .Select(kv => (kv.Key, PatternOf(kv.Value))).ToArray());

    /// <summary>A message that comes as English text from a layer that keeps strings (preset operations, FxSound
    /// failures): when it is one of the catalog's known texts, it is shown in the language now; otherwise as is.</summary>
    public static string Known(string english)
    {
        ArgumentNullException.ThrowIfNull(english);
        if (_language == English) return english;
        foreach (var (key, pattern) in KnownPatterns.Value)
        {
            var m = pattern.Match(english);
            if (!m.Success) continue;
            var args = m.Groups.Cast<Group>().Skip(1).Select(g => (object?)Known(g.Value)).ToArray();
            return Format(Raw(key), args);
        }
        return english;
    }

    private static Regex PatternOf(string template)
    {
        var parts = Regex.Split(template, @"(\{\d+(?::[^}]*)?\})");
        var sb = new System.Text.StringBuilder("^");
        foreach (var part in parts)
        {
            if (part.Length > 2 && part[0] == '{' && part[1] != '{' && char.IsAsciiDigit(part[1])) sb.Append("(.+?)");
            else sb.Append(Regex.Escape(part.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal)));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    private static string Format(string text, object?[] args)
    {
        var rendered = args.Select(a => a is Msg m ? Render(m) : a).ToArray();
        return string.Format(CultureInfo.InvariantCulture, text, rendered);
    }
}

/// <summary>Text for the user kept as data: a catalog key plus its arguments (strings, numbers or other messages).
/// The log keeps its own English text; the UI renders this one in the chosen language.</summary>
public sealed class Msg : IEquatable<Msg>
{
    public Msg(string key, params object?[] args)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Args = args ?? [];
    }

    public string Key { get; }
    public IReadOnlyList<object?> Args { get; }

    internal object?[] ArgArray => [.. Args];

    public bool Equals(Msg? other)
        => other is not null && Key == other.Key && Args.SequenceEqual(other.Args);

    public override bool Equals(object? obj) => Equals(obj as Msg);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Key);
        foreach (var a in Args) h.Add(a);
        return h.ToHashCode();
    }

    public override string ToString() => Loc.RenderEnglish(this);
}
