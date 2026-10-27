using System.Text;
using FxAuto.Core.FxSound;
using FxAuto.Core.Localization;

namespace FxAuto.Core.Presets;

/// <summary>REQ-107 preset name rules (research 02 §6.1, 03 §3.2): ≤ 64 characters, none of <c>&lt;&gt;:"/\|?*</c>,
/// unique case-insensitively against every preset (also the 13 built-ins), at most <c>max_user_presets</c> user
/// presets. FxSound strips/cuts silently and ignores clashes with exit code 0, so FxAuto rejects them before sending.
/// FxAuto is also stricter: ASCII letters, digits, spaces and <c>- _ . ( ) + &amp; # , '</c> only, no leading or
/// trailing space, dot or quote.</summary>
public static class PresetNameRules
{
    public const int MaxLength = 64;
    public const int DefaultMaxUserPresets = 120;
    public const string Forbidden = "<>:\"/\\|?*";
    public const string Separator = " - ";

    /// <summary>"G435 - Music".</summary>
    public static string Compose(string deviceTag, string categoryLabel)
    {
        ArgumentNullException.ThrowIfNull(deviceTag);
        ArgumentNullException.ThrowIfNull(categoryLabel);
        return deviceTag.Trim() + Separator + categoryLabel.Trim();
    }

    /// <summary>What FxSound itself would keep of <paramref name="name"/>: forbidden and control characters removed,
    /// cut to 64, trimmed.</summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (Forbidden.Contains(c, StringComparison.Ordinal) || char.IsControl(c)) continue;
            sb.Append(c);
        }
        var s = sb.ToString().Trim();
        return s.Length > MaxLength ? s[..MaxLength].TrimEnd() : s;
    }

    /// <param name="renamingFrom">Name being renamed (the rename does not add a preset, so the limit does not apply).</param>
    public static NameCheck Validate(string name, FxStatus status, int maxUserPresets, string? renamingFrom = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(status);
        var sanitized = Sanitize(name);
        NameCheck Bad(string key, params object?[] args)
        {
            var text = new Msg(key, args);
            return new(false, Loc.RenderEnglish(text), sanitized) { ErrorText = text };
        }

        if (name.Trim().Length == 0) return Bad("Name.E01");
        var forbidden = name.Where(c => Forbidden.Contains(c, StringComparison.Ordinal)).Distinct().ToArray();
        if (forbidden.Length > 0)
            return Bad("Name.E02", string.Join(' ', forbidden));
        if (name.Any(char.IsControl)) return Bad("Name.E03");
        if (name.Length > MaxLength) return Bad("Name.E04", MaxLength, name.Length);
        if (name != name.Trim() || name.EndsWith('.') || name.StartsWith('.'))
            return Bad("Name.E05");
        if (name.StartsWith('\'') || name.EndsWith('\''))
            return Bad("Name.E06");
        var bad = name.Where(c => !IsAllowed(c)).Distinct().ToArray();
        if (bad.Length > 0)
            return Bad("Name.E07", string.Join(' ', bad));

        if (renamingFrom is not null && string.Equals(name, renamingFrom, StringComparison.Ordinal))
            return Bad("Name.E08");
        var all = status.BuiltInPresets.Concat(status.UserPresets);
        var clash = all.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            if (renamingFrom is not null && string.Equals(clash, renamingFrom, StringComparison.Ordinal))
                return Bad("Name.E09");
            return status.BuiltInPresets.Contains(clash, StringComparer.Ordinal)
                ? Bad("Name.ClashBuiltIn", clash) : Bad("Name.ClashUser", clash);
        }
        if (renamingFrom is null && status.UserPresets.Count >= maxUserPresets)
            return Bad("Name.E10", maxUserPresets);
        return new NameCheck(true, null, sanitized);
    }

    private static bool IsAllowed(char c)
        => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')' or '+' or '&' or '#' or ',' or '\'';
}
