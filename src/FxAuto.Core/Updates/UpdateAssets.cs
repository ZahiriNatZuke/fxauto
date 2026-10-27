using System.Security.Cryptography;

namespace FxAuto.Core.Updates;

/// <summary>Installer and checksum assets of a release: <c>FxAuto-Setup-X.Y.Z.exe</c> and
/// <c>FxAuto-Setup-X.Y.Z.exe.sha256</c> (<c>sha256sum</c> format, "&lt;hex&gt;  &lt;name&gt;").</summary>
public static class UpdateAssets
{
    public const string InstallerPrefix = "FxAuto-Setup-";
    public const string ChecksumSuffix = ".sha256";

    /// <summary>Expected installer name for a version (<c>FxAuto-Setup-1.2.0.exe</c>, pre-release included).</summary>
    public static string InstallerName(SemVersion v) => $"{InstallerPrefix}{v}.exe";

    /// <summary>Picks the installer (the exact name for <paramref name="version"/> first, else the only
    /// <c>FxAuto-Setup-*.exe</c>) and its <c>.sha256</c>. Either can be null when missing. Assets whose name is not a
    /// plain file name or whose URL is not a release download of <paramref name="owner"/>/<paramref name="repo"/>
    /// are ignored.</summary>
    public static (ReleaseAsset? Installer, ReleaseAsset? Checksum) Select(ReleaseInfo release, SemVersion version,
                                                                          string owner, string repo)
    {
        var usable = release.Assets.Where(a => IsPlainFileName(a.Name) && IsTrustedDownloadUrl(a.DownloadUrl, owner, repo)).ToList();
        var exact = InstallerName(version);
        var installer = usable.FirstOrDefault(a => a.Name.Equals(exact, StringComparison.OrdinalIgnoreCase));
        if (installer is null)
        {
            var candidates = usable.Where(a => a.Name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase)
                                               && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 1) installer = candidates[0];
        }
        if (installer is null) return (null, null);
        var checksum = usable.FirstOrDefault(a => a.Name.Equals(installer.Name + ChecksumSuffix, StringComparison.OrdinalIgnoreCase));
        return (installer, checksum);
    }

    /// <summary>True for a bare file name (no directory parts, no invalid characters, not "." or "..").</summary>
    public static bool IsPlainFileName(string name)
        => !string.IsNullOrWhiteSpace(name) && name is not ("." or "..")
           && name.IndexOfAny(['/', '\\', ':']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>Only <c>https://github.com/{owner}/{repo}/releases/download/...</c> is downloaded (GitHub then
    /// redirects to its own storage).</summary>
    public static bool IsTrustedDownloadUrl(string url, string owner, string repo)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
           && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
           && u.AbsolutePath.StartsWith($"/{owner}/{repo}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the hash for <paramref name="fileName"/> from a <c>.sha256</c> file: lines
    /// "&lt;hex&gt;  &lt;name&gt;" or "&lt;hex&gt; *&lt;name&gt;" (binary mode), or a single bare "&lt;hex&gt;".
    /// Returns the lower-case hex, or null when there is no valid line for that file.</summary>
    public static string? ParseChecksum(string text, string fileName)
    {
        string? bare = null;
        int lines = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] == '#') continue;
            lines++;
            int sp = line.IndexOfAny([' ', '\t']);
            var hex = sp < 0 ? line : line[..sp];
            if (!IsSha256Hex(hex)) continue;
            if (sp < 0)
            {
                bare = hex.ToLowerInvariant();
                continue;
            }
            var name = line[sp..].Trim();
            if (name.StartsWith('*')) name = name[1..];
            name = Path.GetFileName(name.Replace('\\', '/'));
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return hex.ToLowerInvariant();
        }
        return lines == 1 ? bare : null;
    }

    public static bool IsSha256Hex(string s) => s.Length == 64 && s.All(char.IsAsciiHexDigit);

    /// <summary>SHA-256 of a file as lower-case hex.</summary>
    public static string ComputeSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    /// <summary>True when the file's SHA-256 equals <paramref name="expectedHex"/> (case-insensitive).</summary>
    public static bool VerifySha256(string path, string expectedHex)
        => IsSha256Hex(expectedHex) && CryptographicOperations.FixedTimeEquals(
               Convert.FromHexString(ComputeSha256(path)), Convert.FromHexString(expectedHex));
}
