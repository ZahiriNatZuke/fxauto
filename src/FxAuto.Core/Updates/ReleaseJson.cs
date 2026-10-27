using System.Text.Json;

namespace FxAuto.Core.Updates;

/// <summary>One asset of a GitHub release (<c>assets[]</c>).</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>The fields FxAuto reads from a GitHub release.</summary>
public sealed record ReleaseInfo(string TagName, string? Name, string? Body, string? HtmlUrl, bool Draft, bool Prerelease,
                                 IReadOnlyList<ReleaseAsset> Assets);

/// <summary>Reads the JSON of <c>GET /repos/{owner}/{repo}/releases/latest</c> (one object) and of
/// <c>GET /repos/{owner}/{repo}/releases</c> (an array). Unknown fields are ignored; malformed assets are skipped.</summary>
public static class ReleaseJson
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, MaxDepth = 64 };

    /// <summary>Parses one release. Returns null with <paramref name="error"/> when the JSON is not a release.</summary>
    public static ReleaseInfo? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json, Options);
            var r = Read(doc.RootElement, out error);
            return r;
        }
        catch (JsonException ex)
        {
            error = "invalid JSON: " + ex.Message;
            return null;
        }
    }

    /// <summary>Parses a list of releases; entries that are not releases are skipped.</summary>
    public static IReadOnlyList<ReleaseInfo>? ParseList(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json, Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "the root is not an array";
                return null;
            }
            var list = new List<ReleaseInfo>();
            foreach (var e in doc.RootElement.EnumerateArray())
                if (Read(e, out _) is { } r) list.Add(r);
            return list;
        }
        catch (JsonException ex)
        {
            error = "invalid JSON: " + ex.Message;
            return null;
        }
    }

    private static ReleaseInfo? Read(JsonElement root, out string? error)
    {
        error = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "the root is not an object";
            return null;
        }
        var tag = Str(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tag))
        {
            error = "tag_name is missing";
            return null;
        }
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var name = Str(a, "name");
                var url = Str(a, "browser_download_url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                long size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0;
                assets.Add(new ReleaseAsset(name, url, size));
            }
        }
        return new ReleaseInfo(tag.Trim(), Str(root, "name"), Str(root, "body"), Str(root, "html_url"),
                               Bool(root, "draft"), Bool(root, "prerelease"), assets);
    }

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
