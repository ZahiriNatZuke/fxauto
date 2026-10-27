using System.Globalization;

namespace FxAuto.Core.Updates;

/// <summary>Semantic version (SemVer 2.0): <c>MAJOR.MINOR.PATCH[-pre.release][+build]</c>. A leading <c>v</c>/<c>V</c>
/// (release tags such as <c>v1.2.0</c>) is accepted; build metadata is parsed and ignored for ordering and equality.</summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Pre-release identifiers (<c>1.2.0-beta.2</c> → <c>["beta", "2"]</c>); empty for a release.</summary>
    public IReadOnlyList<string> PreRelease { get; }

    public bool IsPreRelease => PreRelease.Count > 0;

    public SemVersion(int major, int minor, int patch, IReadOnlyList<string>? preRelease = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease ?? [];
    }

    /// <summary><c>X.Y.Z</c> without the pre-release part (the asset names use it).</summary>
    public string Core => $"{Major}.{Minor}.{Patch}";

    public override string ToString() => IsPreRelease ? Core + "-" + string.Join('.', PreRelease) : Core;

    public static SemVersion Parse(string text)
        => TryParse(text, out var v) ? v : throw new FormatException($"Not a semantic version: \"{text}\".");

    /// <summary>Parses <c>[v]X.Y.Z[-pre][+build]</c>. A fourth numeric part (assembly versions such as
    /// <c>1.0.0.0</c>) is accepted only when it is 0.</summary>
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];

        int plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            var build = s[(plus + 1)..];
            if (build.Length == 0 || !build.Split('.').All(IsIdentifier)) return false;
            s = s[..plus];
        }

        string[] pre = [];
        int dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            var p = s[(dash + 1)..];
            if (p.Length == 0) return false;
            pre = p.Split('.');
            foreach (var id in pre)
            {
                if (!IsIdentifier(id)) return false;
                if (id.Length > 1 && id[0] == '0' && id.All(char.IsAsciiDigit)) return false;   // no leading zeros
            }
            s = s[..dash];
        }

        var parts = s.Split('.');
        if (parts.Length == 4 && parts[3] == "0" && pre.Length == 0) parts = parts[..3];
        if (parts.Length != 3) return false;
        var nums = new int[3];
        for (int i = 0; i < 3; i++)
        {
            var p = parts[i];
            if (p.Length == 0 || !p.All(char.IsAsciiDigit) || (p.Length > 1 && p[0] == '0')) return false;
            if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return false;
        }
        version = new SemVersion(nums[0], nums[1], nums[2], pre);
        return true;
    }

    private static bool IsIdentifier(string id)
        => id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>SemVer precedence: numbers first; a pre-release is lower than its release; pre-release identifiers
    /// compare numerically when both are numeric, numeric below alphanumeric, otherwise ordinally; more identifiers
    /// win when all shared ones are equal.</summary>
    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (!IsPreRelease) return other.IsPreRelease ? 1 : 0;
        if (!other.IsPreRelease) return -1;
        for (int i = 0; i < Math.Min(PreRelease.Count, other.PreRelease.Count); i++)
        {
            var a = PreRelease[i];
            var b = other.PreRelease[i];
            bool an = a.All(char.IsAsciiDigit), bn = b.All(char.IsAsciiDigit);
            if (an && bn)
            {
                c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
            }
            else if (an) c = -1;
            else if (bn) c = 1;
            else c = string.CompareOrdinal(a, b);
            if (c != 0) return c;
        }
        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVersion v && Equals(v);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Major);
        h.Add(Minor);
        h.Add(Patch);
        foreach (var p in PreRelease) h.Add(p, StringComparer.Ordinal);
        return h.ToHashCode();
    }

    public static bool operator ==(SemVersion? a, SemVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SemVersion? a, SemVersion? b) => !(a == b);
    public static bool operator <(SemVersion? a, SemVersion? b) => a is null ? b is not null : a.CompareTo(b) < 0;
    public static bool operator >(SemVersion? a, SemVersion? b) => b < a;
    public static bool operator <=(SemVersion? a, SemVersion? b) => !(a > b);
    public static bool operator >=(SemVersion? a, SemVersion? b) => !(a < b);
}
