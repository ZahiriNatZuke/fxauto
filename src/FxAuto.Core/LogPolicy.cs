using System.Globalization;

namespace FxAuto.Core;

/// <summary>Daily log file names and rotation.</summary>
public static class LogPolicy
{
    private const string Prefix = "fxauto-";
    private const string Suffix = ".jsonl";
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>fxauto-yyyy-MM-dd.jsonl (date in the offset of the given value).</summary>
    public static string FileName(DateTimeOffset d)
        => Prefix + d.ToString(DateFormat, CultureInfo.InvariantCulture) + Suffix;

    /// <summary>Files older than <paramref name="days"/> days (today and the previous days-1 are kept).
    /// Files that do not follow the pattern are never touched.</summary>
    public static IEnumerable<string> Expired(IEnumerable<string> files, DateTimeOffset now, int days = 7)
    {
        ArgumentNullException.ThrowIfNull(files);
        var today = DateOnly.FromDateTime(now.DateTime);
        var cutoff = today.AddDays(-(Math.Max(days, 1) - 1)); // first date that is kept
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var middle = name[Prefix.Length..^Suffix.Length];
            if (DateOnly.TryParseExact(middle, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && date < cutoff)
                yield return f;
        }
    }
}
