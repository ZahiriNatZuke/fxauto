using System.Globalization;
using System.Text;
using System.Text.Json;
using FxAuto.Core.Config;

namespace FxAuto.Core.Updates;

/// <summary>Persisted update settings and check history (%APPDATA%\FxAuto\update-state.json). Every field is optional
/// on disk: a missing file, a missing field or a corrupt file gives the defaults (automatic checks on).
/// <para><see cref="CheckAutomatically"/> and <see cref="IncludePrereleases"/> are user settings: config.json
/// (<c>general.updates</c>) rules them and the app copies them in at start and on every config change. Older builds
/// kept them in this file; <see cref="SettingsInFile"/> says the file still holds them (they are migrated to
/// config.json once, then only the run state is written here).</para></summary>
public sealed record UpdateState
{
    public bool CheckAutomatically { get; init; } = true;
    public bool IncludePrereleases { get; init; }
    public DateTimeOffset? LastCheckAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public int ConsecutiveFailures { get; init; }
    /// <summary>End of a GitHub rate-limit window; no request is made before it.</summary>
    public DateTimeOffset? RetryNotBefore { get; init; }
    /// <summary>Latest version seen on GitHub (informative, for the UI).</summary>
    public string? LatestKnownVersion { get; init; }
    /// <summary>Last version announced with a tray balloon (one balloon per version).</summary>
    public string? NotifiedVersion { get; init; }
    /// <summary>True when the file holds <c>checkAutomatically</c>/<c>includePrereleases</c> (written by an older
    /// build, or kept until they can be migrated); only then does <see cref="UpdateStateJson.Serialize"/> write them.</summary>
    public bool SettingsInFile { get; init; }
}

public static class UpdateStateJson
{
    public const string FileName = "update-state.json";
    public const int CurrentSchemaVersion = 1;

    public static UpdateState Parse(string? json)
    {
        var s = new UpdateState();
        if (string.IsNullOrWhiteSpace(json)) return s;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return s;
            var auto = Bool(r, "checkAutomatically");
            var pre = Bool(r, "includePrereleases");
            return s with
            {
                CheckAutomatically = auto ?? true,
                IncludePrereleases = pre ?? false,
                SettingsInFile = auto is not null || pre is not null,
                LastCheckAt = Date(r, "lastCheckAt"),
                LastSuccessAt = Date(r, "lastSuccessAt"),
                ConsecutiveFailures = r.TryGetProperty("consecutiveFailures", out var f) && f.ValueKind == JsonValueKind.Number
                                      && f.TryGetInt32(out var n) && n >= 0 ? Math.Min(n, 1000) : 0,
                RetryNotBefore = Date(r, "retryNotBefore"),
                LatestKnownVersion = r.TryGetProperty("latestKnownVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null,
                NotifiedVersion = r.TryGetProperty("notifiedVersion", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() : null,
            };
        }
        catch (JsonException)
        {
            return s;
        }
    }

    public static string Serialize(UpdateState s)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", CurrentSchemaVersion);
            if (s.SettingsInFile)
            {
                w.WriteBoolean("checkAutomatically", s.CheckAutomatically);
                w.WriteBoolean("includePrereleases", s.IncludePrereleases);
            }
            WriteDate(w, "lastCheckAt", s.LastCheckAt);
            WriteDate(w, "lastSuccessAt", s.LastSuccessAt);
            w.WriteNumber("consecutiveFailures", s.ConsecutiveFailures);
            WriteDate(w, "retryNotBefore", s.RetryNotBefore);
            if (s.LatestKnownVersion is { } lv) w.WriteString("latestKnownVersion", lv);
            if (s.NotifiedVersion is { } nv) w.WriteString("notifiedVersion", nv);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    /// <summary>Loads the file; any read error gives the defaults.</summary>
    public static UpdateState Load(string path)
    {
        try { return Parse(AtomicFile.ReadAllTextOrNull(path)); }
        catch (IOException) { return new UpdateState(); }
        catch (UnauthorizedAccessException) { return new UpdateState(); }
    }

    public static void Save(string path, UpdateState s) => AtomicFile.WriteAllText(path, Serialize(s));

    private static bool? Bool(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static DateTimeOffset? Date(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;

    private static void WriteDate(Utf8JsonWriter w, string name, DateTimeOffset? d)
    {
        if (d is { } x) w.WriteString(name, x.ToString("O", CultureInfo.InvariantCulture));
    }
}
