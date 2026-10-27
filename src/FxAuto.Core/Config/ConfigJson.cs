using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FxAuto.Core.Arbitration;
using FxAuto.Core.Audio;
using FxAuto.Core.Classification;
using FxAuto.Core.Devices;

namespace FxAuto.Core.Config;

/// <summary>config.json reading and writing: strict JSON written, comments and trailing commas accepted on read.
/// Keys are camelCase English; enums are camelCase strings ("headset", "game", "ignore").</summary>
public static class ConfigJson
{
    /// <summary>Schema version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 2;

    internal static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 32,
    };

    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        // The file is meant to be readable and hand-editable: keep "á", "+", "'" as they are.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Parses config.json. Config is null when the file cannot be used at all (invalid JSON, wrong types,
    /// missing <c>schemaVersion</c>). Otherwise the issues hold the reading notes (unknown keys) plus the full
    /// <see cref="ConfigValidator.Validate"/> result; a <c>schemaVersion</c> above <see cref="CurrentSchemaVersion"/>
    /// gives a best-effort config with an Error issue (open it read-only, never write it back).</summary>
    public static (FxAutoConfig? Config, IReadOnlyList<ConfigIssue> Issues) Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json ?? "", DocOptions);
        }
        catch (JsonException ex)
        {
            return (null, [ConfigIssue.Of(IssueLevel.Error, "", "Issue.Json.01", ex.Message)]);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, [ConfigIssue.Of(IssueLevel.Error, "", "Issue.Json.02")]);
            var reader = new ConfigReader();
            var cfg = reader.ReadConfig(root);
            if (cfg is null || reader.Fatal) return (null, reader.Issues);
            return (cfg, [.. reader.Issues, .. ConfigValidator.Validate(cfg)]);
        }
    }

    /// <summary>Serializes to indented strict JSON with English keys. Refuses (InvalidOperationException) a
    /// <c>schemaVersion</c> above <see cref="CurrentSchemaVersion"/>: an older FxAuto never overwrites a newer file.</summary>
    public static string Serialize(FxAutoConfig c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"schemaVersion {c.SchemaVersion} is newer than this FxAuto ({CurrentSchemaVersion}); the file is read-only.");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", c.SchemaVersion);
            WriteGeneral(w, c.General);

            w.WriteStartObject("presets");
            foreach (var (name, meta) in c.Presets)
            {
                w.WriteStartObject(name);
                w.WriteNumber("volumeLeveling", meta.VolumeLeveling);
                if (meta.Note is not null) w.WriteString("note", meta.Note);
                w.WriteEndObject();
            }
            w.WriteEndObject();

            w.WriteStartArray("devices");
            foreach (var d in c.Devices) WriteDevice(w, d);
            w.WriteEndArray();

            w.WriteStartArray("sources");
            foreach (var s in c.Sources) WriteSource(w, s);
            w.WriteEndArray();

            w.WriteStartArray("assignments");
            foreach (var a in c.Assignments)
            {
                w.WriteStartObject();
                w.WriteString("device", a.Device);
                if (a.Category is not null) w.WriteString("category", a.Category);
                if (a.Source is not null) w.WriteString("source", a.Source);
                w.WriteString("preset", a.Preset);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            WriteList(w, "ignoredApps", c.IgnoredApps);
            WriteList(w, "launchers", c.Launchers);
            WriteList(w, "gameFolderExclusions", c.GameFolderExclusions);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + Environment.NewLine;
    }

    /// <summary>camelCase name of an enum value ("UnknownDigitalPassthrough" → "unknownDigitalPassthrough").</summary>
    internal static string EnumName<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static void WriteGeneral(Utf8JsonWriter w, GeneralSettings g)
    {
        w.WriteStartObject("general");
        if (g.FxSoundPath is not null) w.WriteString("fxSoundPath", g.FxSoundPath);
        w.WriteBoolean("startWithWindows", g.StartWithWindows);
        w.WriteBoolean("startFxSoundIfClosed", g.StartFxSoundIfClosed);
        w.WriteString("logLevel", g.LogLevel);
        w.WriteBoolean("logTitles", g.LogTitles);
        w.WriteNumber("startupGraceSeconds", g.StartupGraceSeconds);
        w.WriteNumber("checkIntervalSeconds", g.CheckIntervalSeconds);
        var t = g.Detection;
        w.WriteStartObject("detection");
        w.WriteNumber("pollMs", t.PollMs);
        w.WriteNumber("peakThreshold", t.PeakThreshold);
        w.WriteNumber("attackRatio", t.AttackRatio);
        w.WriteNumber("attackMs", t.AttackMs);
        w.WriteNumber("burstIgnoreMs", t.BurstIgnoreMs);
        w.WriteNumber("releaseMs", t.ReleaseMs);
        w.WriteNumber("gameReleaseMs", t.GameReleaseMs);
        w.WriteEndObject();
        w.WriteNumber("idleGraceSeconds", g.IdleGraceSeconds);
        w.WriteNumber("minDwellMs", g.MinDwellMs);
        w.WriteNumber("gameLoadingMaxMs", g.GameLoadingMaxMs);
        w.WriteString("callPolicy", EnumName(g.CallPolicy));
        w.WriteString("unknownDevice", g.UnknownDevice);
        w.WriteBoolean("readBrowserTabs", g.ReadBrowserTabs);
        WriteList(w, "audibleTabMarkers", g.AudibleTabMarkers);
        w.WriteString("language", g.Language);
        w.WriteString("theme", g.Theme);
        if (g.Updates is { } u)
        {
            w.WriteStartObject("updates");
            w.WriteBoolean("checkAutomatically", u.CheckAutomatically);
            w.WriteBoolean("includePrereleases", u.IncludePrereleases);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static void WriteDevice(Utf8JsonWriter w, DeviceConfig d)
    {
        w.WriteStartObject();
        w.WriteString("id", d.Id);
        w.WriteString("displayName", d.DisplayName);
        w.WriteString("tag", d.Tag);
        w.WriteStartObject("match");
        WriteList(w, "adapters", d.Match.Adapters);
        if (d.Match.FormFactor is { } ff) w.WriteString("formFactor", EnumName(ff));
        if (d.Match.EndpointDesc is not null) w.WriteString("endpointDesc", d.Match.EndpointDesc);
        if (d.Match.PinnedEndpointIds.Count > 0) WriteList(w, "pinnedEndpointIds", d.Match.PinnedEndpointIds);
        if (d.Match.ContainerId is not null) w.WriteString("containerId", d.Match.ContainerId);
        w.WriteEndObject();
        w.WriteString("defaultPreset", d.DefaultPreset);
        w.WriteBoolean("enabled", d.Enabled);
        if (d.LastSeenOutputName is not null) w.WriteString("lastSeenOutputName", d.LastSeenOutputName);
        w.WriteEndObject();
    }

    private static void WriteSource(Utf8JsonWriter w, SourceConfig s)
    {
        w.WriteStartObject();
        w.WriteString("id", s.Id);
        w.WriteString("name", s.Name);
        w.WriteString("kind", EnumName(s.Kind));
        w.WriteStartObject("match");
        if (s.Match.Exe.Count > 0) WriteList(w, "exe", s.Match.Exe);
        if (s.Match.Aumid.Count > 0) WriteList(w, "aumid", s.Match.Aumid);
        if (s.Match.Folder.Count > 0) WriteList(w, "folder", s.Match.Folder);
        if (s.Match.Site.Count > 0) WriteList(w, "site", s.Match.Site);
        if (s.Match.TitleRegex is not null) w.WriteString("titleRegex", s.Match.TitleRegex);
        w.WriteEndObject();
        w.WriteString("category", s.Category);
        if (s.Fallback) w.WriteBoolean("fallback", true);
        w.WriteString("origin", s.Origin);
        w.WriteEndObject();
    }

    private static void WriteList(Utf8JsonWriter w, string name, IReadOnlyList<string> items)
    {
        w.WriteStartArray(name);
        foreach (var i in items) w.WriteStringValue(i);
        w.WriteEndArray();
    }
}

/// <summary>Typed reading of config.json with JSON-path issues. A type error is fatal (the config is not used); an
/// unknown key is a warning; a missing key takes the default value.</summary>
internal sealed class ConfigReader
{
    private readonly List<ConfigIssue> _issues = [];
    public IReadOnlyList<ConfigIssue> Issues => _issues;
    public bool Fatal { get; private set; }

    private static readonly string[] RootKeys = ["schemaVersion", "general", "presets", "devices", "sources",
        "assignments", "ignoredApps", "launchers", "gameFolderExclusions"];
    private static readonly string[] GeneralKeys = ["fxSoundPath", "startWithWindows", "startFxSoundIfClosed", "logLevel",
        "logTitles", "startupGraceSeconds", "checkIntervalSeconds", "detection", "idleGraceSeconds", "minDwellMs",
        "gameLoadingMaxMs", "callPolicy", "unknownDevice", "readBrowserTabs", "audibleTabMarkers", "language", "theme", "updates"];
    private static readonly string[] UpdatesKeys = ["checkAutomatically", "includePrereleases"];
    private static readonly string[] DetectionKeys = ["pollMs", "peakThreshold", "attackRatio", "attackMs", "burstIgnoreMs",
        "releaseMs", "gameReleaseMs"];
    private static readonly string[] PresetKeys = ["volumeLeveling", "note"];
    private static readonly string[] DeviceKeys = ["id", "displayName", "tag", "match", "defaultPreset", "enabled", "lastSeenOutputName"];
    private static readonly string[] DeviceMatchKeys = ["adapters", "formFactor", "endpointDesc", "pinnedEndpointIds", "containerId"];
    private static readonly string[] SourceKeys = ["id", "name", "kind", "match", "category", "fallback", "origin"];
    private static readonly string[] SourceMatchKeys = ["exe", "aumid", "folder", "site", "titleRegex"];
    private static readonly string[] AssignmentKeys = ["device", "category", "source", "preset"];

    public FxAutoConfig? ReadConfig(JsonElement root)
    {
        CheckKeys(root, "", RootKeys);
        if (!root.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.Number || !sv.TryGetInt32(out var version))
        {
            Error("schemaVersion", "Issue.Json.E01");
            return null;
        }
        var d = DefaultConfig.CreateGeneral();
        var general = root.TryGetProperty("general", out var g) && g.ValueKind != JsonValueKind.Null
            ? ReadGeneral(g, d) : d;

        var presets = new Dictionary<string, PresetMeta>(StringComparer.Ordinal);
        if (Obj(root, "presets", "presets") is { } po)
        {
            foreach (var p in po.EnumerateObject())
            {
                var path = $"presets[\"{p.Name}\"]";
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var lvOnly))
                {
                    presets[p.Name] = new PresetMeta(lvOnly, null);   // shorthand: "name": leveling
                    continue;
                }
                if (p.Value.ValueKind != JsonValueKind.Object)
                {
                    Error(path, "Issue.Json.E02");
                    continue;
                }
                CheckKeys(p.Value, path, PresetKeys);
                presets[p.Name] = new PresetMeta(Dbl(p.Value, "volumeLeveling", path, 0), Str(p.Value, "note", path, null));
            }
        }

        var devices = ReadArray(root, "devices", "devices", ReadDevice);
        var sources = ReadArray(root, "sources", "sources", ReadSource);
        var assignments = ReadArray(root, "assignments", "assignments", ReadAssignment);
        var ignored = StrList(root, "ignoredApps", "");
        var launchers = StrList(root, "launchers", "");
        var exclusions = StrList(root, "gameFolderExclusions", "");
        if (Fatal) return null;
        return new FxAutoConfig(version, general, presets, devices, sources, assignments, ignored, launchers, exclusions);
    }

    private GeneralSettings ReadGeneral(JsonElement g, GeneralSettings d)
    {
        const string P = "general";
        if (g.ValueKind != JsonValueKind.Object)
        {
            Error(P, "Issue.Json.E03");
            return d;
        }
        CheckKeys(g, P, GeneralKeys);
        var t = d.Detection;
        if (Obj(g, "detection", P + ".detection") is { } det)
        {
            const string D = P + ".detection";
            CheckKeys(det, D, DetectionKeys);
            t = new SoundingTimings(
                PollMs: Int(det, "pollMs", D, t.PollMs),
                PeakThreshold: Dbl(det, "peakThreshold", D, t.PeakThreshold),
                AttackRatio: Dbl(det, "attackRatio", D, t.AttackRatio),
                AttackMs: Int(det, "attackMs", D, t.AttackMs),
                BurstIgnoreMs: Int(det, "burstIgnoreMs", D, t.BurstIgnoreMs),
                ReleaseMs: Int(det, "releaseMs", D, t.ReleaseMs),
                GameReleaseMs: Int(det, "gameReleaseMs", D, t.GameReleaseMs));
        }
        return new GeneralSettings(
            FxSoundPath: Str(g, "fxSoundPath", P, d.FxSoundPath),
            StartWithWindows: Bool(g, "startWithWindows", P, d.StartWithWindows),
            StartFxSoundIfClosed: Bool(g, "startFxSoundIfClosed", P, d.StartFxSoundIfClosed),
            LogLevel: Str(g, "logLevel", P, d.LogLevel) ?? d.LogLevel,
            LogTitles: Bool(g, "logTitles", P, d.LogTitles),
            StartupGraceSeconds: Int(g, "startupGraceSeconds", P, d.StartupGraceSeconds),
            CheckIntervalSeconds: Int(g, "checkIntervalSeconds", P, d.CheckIntervalSeconds),
            Detection: t,
            IdleGraceSeconds: Int(g, "idleGraceSeconds", P, d.IdleGraceSeconds),
            MinDwellMs: Int(g, "minDwellMs", P, d.MinDwellMs),
            GameLoadingMaxMs: Int(g, "gameLoadingMaxMs", P, d.GameLoadingMaxMs),
            CallPolicy: ReadEnum(g, "callPolicy", P, d.CallPolicy),
            UnknownDevice: Str(g, "unknownDevice", P, d.UnknownDevice) ?? d.UnknownDevice,
            ReadBrowserTabs: Bool(g, "readBrowserTabs", P, d.ReadBrowserTabs),
            AudibleTabMarkers: g.TryGetProperty("audibleTabMarkers", out _) ? StrList(g, "audibleTabMarkers", P) : d.AudibleTabMarkers)
        {
            // optional keys added after schemaVersion 2 shipped: absent = "system"
            Language = Str(g, "language", P, d.Language) ?? d.Language,
            Theme = Str(g, "theme", P, d.Theme) ?? d.Theme,
            Updates = ReadUpdates(g, d.Updates),
        };
    }

    /// <summary>Optional <c>general.updates</c>: absent (or null) keeps <paramref name="d"/>; each key inside is
    /// optional too and falls back to <see cref="UpdatePreferences.Default"/>.</summary>
    private UpdatePreferences? ReadUpdates(JsonElement g, UpdatePreferences? d)
    {
        const string U = "general.updates";
        if (Obj(g, "updates", U) is not { } u) return d;
        CheckKeys(u, U, UpdatesKeys);
        var def = d ?? UpdatePreferences.Default;
        return new UpdatePreferences(Bool(u, "checkAutomatically", U, def.CheckAutomatically),
                                     Bool(u, "includePrereleases", U, def.IncludePrereleases));
    }

    private DeviceConfig? ReadDevice(JsonElement e, string path)
    {
        CheckKeys(e, path, DeviceKeys);
        var mp = path + ".match";
        var match = new DeviceMatchSpec([], null, null, [], null);
        if (Obj(e, "match", mp) is { } m)
        {
            CheckKeys(m, mp, DeviceMatchKeys);
            FormFactor? ff = m.TryGetProperty("formFactor", out var ffe) && ffe.ValueKind != JsonValueKind.Null
                 ? ReadEnum(m, "formFactor", mp, FormFactor.Unknown) : null;
            match = new DeviceMatchSpec(StrList(m, "adapters", mp), ff, Str(m, "endpointDesc", mp, null),
                StrList(m, "pinnedEndpointIds", mp), Str(m, "containerId", mp, null));
        }
        return new DeviceConfig(Str(e, "id", path, null) ?? "", Str(e, "displayName", path, null) ?? "",
            Str(e, "tag", path, null) ?? "", match, Str(e, "defaultPreset", path, null) ?? "",
            Bool(e, "enabled", path, true), Str(e, "lastSeenOutputName", path, null));
    }

    private SourceConfig? ReadSource(JsonElement e, string path)
    {
        CheckKeys(e, path, SourceKeys);
        var mp = path + ".match";
        var match = new SourceMatch([], [], [], [], null);
        if (Obj(e, "match", mp) is { } m)
        {
            CheckKeys(m, mp, SourceMatchKeys);
            match = new SourceMatch(StrList(m, "exe", mp), StrList(m, "aumid", mp), StrList(m, "folder", mp),
                StrList(m, "site", mp), Str(m, "titleRegex", mp, null));
        }
        if (!e.TryGetProperty("kind", out _)) Error(path + ".kind", "Issue.Json.E04");
        return new SourceConfig(Str(e, "id", path, null) ?? "", Str(e, "name", path, null) ?? "",
            ReadEnum(e, "kind", path, SourceKind.App), match, Str(e, "category", path, null) ?? "",
            Bool(e, "fallback", path, false), Str(e, "origin", path, null) ?? DefaultConfig.OriginUser);
    }

    private Assignment? ReadAssignment(JsonElement e, string path)
    {
        CheckKeys(e, path, AssignmentKeys);
        return new Assignment(Str(e, "device", path, null) ?? "", Str(e, "category", path, null),
            Str(e, "source", path, null), Str(e, "preset", path, null) ?? "");
    }

    // ── primitives ──

    private void Error(string path, string key, params object?[] args)
    {
        _issues.Add(ConfigIssue.Of(IssueLevel.Error, path, key, args));
        Fatal = true;
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    private void CheckKeys(JsonElement o, string path, string[] known)
    {
        foreach (var p in o.EnumerateObject())
        {
            if (!known.Contains(p.Name, StringComparer.Ordinal))
                _issues.Add(ConfigIssue.Of(IssueLevel.Warning, Join(path, p.Name), "Issue.Json.03", p.Name));
        }
    }

    private JsonElement? Obj(JsonElement o, string key, string path)
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Object) return v;
        Error(path, "Issue.Json.E05", key);
        return null;
    }

    private List<T> ReadArray<T>(JsonElement o, string key, string path, Func<JsonElement, string, T?> read) where T : class
    {
        var list = new List<T>();
        if (!o.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return list;
        if (v.ValueKind != JsonValueKind.Array)
        {
            Error(path, "Issue.Json.E06", key);
            return list;
        }
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            var p = $"{path}[{i++}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                Error(p, "Issue.Json.E07");
                continue;
            }
            if (read(item, p) is { } x) list.Add(x);
        }
        return list;
    }

    private string? Str(JsonElement o, string key, string path, string? def)
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return def;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        Error(Join(path, key), "Issue.Json.E08", key);
        return def;
    }

    private bool Bool(JsonElement o, string key, string path, bool def)
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return def;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        Error(Join(path, key), "Issue.Json.E09", key);
        return def;
    }

    private int Int(JsonElement o, string key, string path, int def)
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        Error(Join(path, key), "Issue.Json.E10", key);
        return def;
    }

    private double Dbl(JsonElement o, string key, string path, double def)
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return n;
        Error(Join(path, key), "Issue.Json.E11", key);
        return def;
    }

    private T ReadEnum<T>(JsonElement o, string key, string path, T def) where T : struct, System.Enum
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return def;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            foreach (var value in System.Enum.GetValues<T>())
            {
                if (string.Equals(ConfigJson.EnumName(value), s, StringComparison.OrdinalIgnoreCase)) return value;
            }
        }
        var allowed = string.Join(", ", System.Enum.GetValues<T>().Select(x => ConfigJson.EnumName(x)));
        Error(Join(path, key), "Issue.Json.E12", key, allowed);
        return def;
    }

    /// <summary>A list of strings; a single string is accepted as a one-item list (hand edits).</summary>
    private List<string> StrList(JsonElement o, string key, string path)
    {
        var list = new List<string>();
        if (!o.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return list;
        var p = Join(path, key);
        if (v.ValueKind == JsonValueKind.String)
        {
            list.Add(v.GetString()!);
            return list;
        }
        if (v.ValueKind != JsonValueKind.Array)
        {
            Error(p, "Issue.Json.E13", key);
            return list;
        }
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
            else Error($"{p}[{i}]", "Issue.Json.E14");
            i++;
        }
        return list;
    }
}
