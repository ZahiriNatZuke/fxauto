using System.Text;
using System.Text.RegularExpressions;

namespace FxAuto.Core.Devices;

/// <summary>Adapter-name normalisation and suggestions for new devices (research 02 §2.2).</summary>
public static partial class DeviceKeys
{
    /// <summary>Adapter of the FxSound virtual endpoint (bus ROOT).</summary>
    public const string FxSoundAdapter = "FxSound Audio Enhancer";

    private static readonly string[] BluetoothSuffixes = [" Hands-Free", " Stereo"];

    // Words that describe the kind of device rather than the device itself; dropped from suggested tags.
    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio", "wireless", "gaming", "headset", "headphones", "headphone", "earphones", "speakers", "speaker",
        "high", "definition", "device", "controller", "enhancer", "usb", "bluetooth", "stereo", "hands-free",
        "sound", "output", "digital",
    };

    /// <summary>Trim, collapse spaces, strip a leading "\d+- " and the " Hands-Free" / " Stereo" suffixes.</summary>
    public static string NormalizeAdapter(string adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var s = SpacesRx().Replace(adapter.Trim(), " ");
        s = PortPrefixRx().Replace(s, "");
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suffix in BluetoothSuffixes)
            {
                if (s.Length > suffix.Length && s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    s = s[..^suffix.Length].TrimEnd();
                    changed = true;
                }
            }
        }
        return s;
    }

    /// <summary>Normalised adapters are compared case-insensitively.</summary>
    public static bool SameAdapter(string? a, string? b)
        => a is not null && b is not null
           && string.Equals(NormalizeAdapter(a), NormalizeAdapter(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Adapter taken from a FriendlyName "&lt;desc&gt; (&lt;adapter&gt;)", for endpoints whose adapter
    /// property could not be read. null if the name has no trailing parenthesis.</summary>
    public static string? AdapterFromFriendlyName(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return null;
        var s = friendlyName.Trim();
        if (!s.EndsWith(')')) return null;
        // the adapter may itself contain parentheses ("Realtek(R) Audio"): match the parenthesis that closes at the end
        var depth = 0;
        for (var i = s.Length - 1; i >= 0; i--)
        {
            if (s[i] == ')') depth++;
            else if (s[i] == '(')
            {
                depth--;
                if (depth == 0)
                {
                    var inner = s[(i + 1)..^1].Trim();
                    return inner.Length == 0 ? null : inner;
                }
            }
        }
        return null;
    }

    /// <summary>"Realtek(R) Audio" → "Realtek", "G435 Wireless Gaming Headset" → "G435",
    /// "2- DualSense Wireless Controller" → "DualSense". Never empty; only characters valid in preset names.</summary>
    public static string SuggestTag(string adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var s = TrademarkRx().Replace(NormalizeAdapter(adapter), " ");
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '+' or '&').ToArray()))
            .Where(w => w.Length > 0)
            .ToList();
        var kept = words.Where(w => !GenericWords.Contains(w)).ToList();
        if (kept.Count == 0)
        {
            // generic driver names ("High Definition Audio Device")
            return words.Count >= 3 && words[0].Equals("High", StringComparison.OrdinalIgnoreCase) ? "HD Audio"
                : words.Count > 0 ? words[0] : "Device";
        }
        var sb = new StringBuilder();
        foreach (var w in kept)
        {
            if (sb.Length > 0 && sb.Length + 1 + w.Length > 20) break;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(w);
        }
        return sb.Length > 20 ? sb.ToString(0, 20).TrimEnd() : sb.ToString();
    }

    /// <summary>Lower-case slug of the suggested tag: "realtek", "g435", "redmi-buds-6-pro".</summary>
    public static string SuggestId(string adapter)
    {
        var tag = SuggestTag(adapter);
        var sb = new StringBuilder(tag.Length);
        foreach (var c in tag)
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "device" : slug;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRx();

    [GeneratedRegex(@"^\d+-\s+")]
    private static partial Regex PortPrefixRx();

    [GeneratedRegex(@"\((R|TM|C)\)|[®™©]", RegexOptions.IgnoreCase)]
    private static partial Regex TrademarkRx();
}
