using System.Globalization;
using System.Text;
using System.Xml;

namespace FxAuto.FxSound;

/// <summary>
/// Pure helpers for FxSound preset files (.fac) and FxSound.settings (no file system; unit-tested).
/// A .fac is text with CRLF lines; line 3 is the preset name (research 03 §3.1, verified in the spike). Rewrites
/// work on bytes: only the bytes of line 3 change, so line endings, encoding and number formatting stay identical.
/// </summary>
internal static class FacFile
{
    /// <summary>Zero-based index of the name line.</summary>
    public const int NameLineIndex = 2;
    public const int DefaultMaxUserPresets = 120;
    public const int MinMaxUserPresets = 10;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Byte range of line <paramref name="index"/> without its terminator ("\r\n" or "\n").</summary>
    public static bool TryLineRange(ReadOnlySpan<byte> bytes, int index, out int start, out int length)
    {
        start = 0;
        length = 0;
        int pos = 0;
        for (int line = 0; line < index; line++)
        {
            int nl = bytes[pos..].IndexOf((byte)'\n');
            if (nl < 0) return false;
            pos += nl + 1;
        }
        if (pos > bytes.Length || (pos == bytes.Length && index > 0)) return false;
        int end = bytes[pos..].IndexOf((byte)'\n');
        end = end < 0 ? bytes.Length : pos + end;
        if (end > pos && bytes[end - 1] == (byte)'\r') end--;
        start = pos;
        length = end - pos;
        return true;
    }

    /// <summary>Preset name (line 3), or null if the file has fewer than 3 lines.</summary>
    public static string? ReadName(ReadOnlySpan<byte> bytes)
        => TryLineRange(bytes, NameLineIndex, out int s, out int n) ? Utf8NoBom.GetString(bytes.Slice(s, n)) : null;

    /// <summary>Null if <paramref name="name"/> can be written as line 3 and as a file name; otherwise the reason.</summary>
    public static string? CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "The preset name is empty.";
        if (name.Contains('\r', StringComparison.Ordinal) || name.Contains('\n', StringComparison.Ordinal))
            return "The preset name contains a line break.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "The preset name contains characters that are not valid in a file name.";
        if (name is "." or "..") return "The preset name is not valid.";
        return null;
    }

    /// <summary>Copy of <paramref name="source"/> with line 3 replaced by <paramref name="newName"/> (UTF-8 without
    /// BOM). Every other byte is kept.</summary>
    /// <exception cref="FormatException">The source has fewer than 3 lines or the name is not valid.</exception>
    public static byte[] WithName(ReadOnlySpan<byte> source, string newName)
    {
        if (CheckName(newName) is string err) throw new FormatException(err);
        if (!TryLineRange(source, NameLineIndex, out int s, out int n))
            throw new FormatException("The preset file has fewer than 3 lines.");
        var name = Utf8NoBom.GetBytes(newName);
        var result = new byte[source.Length - n + name.Length];
        source[..s].CopyTo(result);
        name.CopyTo(result.AsSpan(s));
        source[(s + n)..].CopyTo(result.AsSpan(s + name.Length));
        return result;
    }

    /// <summary>max_user_presets from FxSound.settings (&lt;VALUE name="max_user_presets" val="120"/&gt;), clamped to
    /// 10–120 like FxSound; 120 when missing or unreadable.</summary>
    public static int ParseMaxUserPresets(string? settingsXml)
    {
        if (string.IsNullOrWhiteSpace(settingsXml)) return DefaultMaxUserPresets;
        try
        {
            var xs = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var sr = new StringReader(settingsXml);
            using var r = XmlReader.Create(sr, xs);
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.Name != "VALUE") continue;
                if (r.GetAttribute("name") != "max_user_presets") continue;
                var val = r.GetAttribute("val");
                if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    || double.IsNaN(d))
                    return DefaultMaxUserPresets;
                return (int)Math.Clamp(Math.Round(d), MinMaxUserPresets, DefaultMaxUserPresets);
            }
        }
        catch (XmlException) { }
        return DefaultMaxUserPresets;
    }
}
