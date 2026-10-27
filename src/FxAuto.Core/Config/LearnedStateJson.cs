using System.Text;
using System.Text.Json;

namespace FxAuto.Core.Config;

/// <summary>runtime-state.json reading/writing and learning of endpoint ids. The file is runtime state written only by
/// the coordinator; a corrupt file never blocks start-up (empty state plus a warning for Status).</summary>
public static class LearnedStateJson
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Learned endpoint ids kept per device (most recent last).</summary>
    public const int MaxEndpointsPerDevice = 16;

    /// <summary>File name inside %APPDATA%\FxAuto.</summary>
    public const string FileName = "runtime-state.json";

    public static LearnedState Empty() => new(CurrentSchemaVersion,
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), [], null);

    /// <summary>Parses runtime-state.json. Empty text gives an empty state without issues (first run); a corrupt file
    /// gives an empty state plus a warning issue.</summary>
    public static (LearnedState State, IReadOnlyList<ConfigIssue> Issues) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (Empty(), []);
        try
        {
            using var doc = JsonDocument.Parse(json, ConfigJson.DocOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Corrupt("the root is not an object");
            var issues = new List<ConfigIssue>();

            var version = CurrentSchemaVersion;
            if (root.TryGetProperty("schemaVersion", out var sv))
            {
                if (sv.ValueKind != JsonValueKind.Number || !sv.TryGetInt32(out version)) return Corrupt("schemaVersion is not an integer");
                if (version > CurrentSchemaVersion)
                    issues.Add(ConfigIssue.Of(IssueLevel.Warning, "schemaVersion", "Issue.State.01", FileName, version));
            }

            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("endpointIdsByDevice", out var eps) && eps.ValueKind != JsonValueKind.Null)
            {
                if (eps.ValueKind != JsonValueKind.Object) return Corrupt("endpointIdsByDevice is not an object");
                foreach (var p in eps.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Array) return Corrupt($"endpointIdsByDevice.{p.Name} is not a list");
                    var ids = new List<string>();
                    foreach (var x in p.Value.EnumerateArray())
                    {
                        if (x.ValueKind != JsonValueKind.String) return Corrupt($"endpointIdsByDevice.{p.Name} holds a non-text value");
                        ids.Add(x.GetString()!);
                    }
                    map[p.Name] = ids;
                }
            }

            var notified = new List<string>();
            if (root.TryGetProperty("notifiedUnknownDeviceKeys", out var nk) && nk.ValueKind != JsonValueKind.Null)
            {
                if (nk.ValueKind != JsonValueKind.Array) return Corrupt("notifiedUnknownDeviceKeys is not a list");
                foreach (var x in nk.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.String) return Corrupt("notifiedUnknownDeviceKeys holds a non-text value");
                    notified.Add(x.GetString()!);
                }
            }

            string? edit = null;
            if (root.TryGetProperty("editPendingPreset", out var ep) && ep.ValueKind != JsonValueKind.Null)
            {
                if (ep.ValueKind != JsonValueKind.String) return Corrupt("editPendingPreset is not text");
                edit = ep.GetString();
            }
            var userMode = ParseUserMode(root, issues);
            return (new LearnedState(version, map, notified, edit) { UserMode = userMode }, issues);
        }
        catch (JsonException ex)
        {
            return Corrupt(ex.Message);
        }
    }

    public static string Serialize(LearnedState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, ConfigJson.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", CurrentSchemaVersion);
            w.WriteStartObject("endpointIdsByDevice");
            foreach (var (device, ids) in s.EndpointIdsByDevice)
            {
                w.WriteStartArray(device);
                foreach (var id in ids) w.WriteStringValue(id);
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteStartArray("notifiedUnknownDeviceKeys");
            foreach (var k in s.NotifiedUnknownDeviceKeys) w.WriteStringValue(k);
            w.WriteEndArray();
            if (s.EditPendingPreset is null) w.WriteNull("editPendingPreset");
            else w.WriteString("editPendingPreset", s.EditPendingPreset);
            if (s.UserMode is not { } um) w.WriteNull("userMode");
            else
            {
                w.WriteStartObject("userMode");
                w.WriteString("mode", um.Mode.ToString());
                if (um.LockedPreset is null) w.WriteNull("lockedPreset");
                else w.WriteString("lockedPreset", um.LockedPreset);
                if (um.PausedUntil is not { } until) w.WriteNull("pausedUntil");
                else w.WriteString("pausedUntil", until);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + Environment.NewLine;
    }

    /// <summary>Returns a state where <paramref name="endpointId"/> is learned for <paramref name="deviceId"/>: moved
    /// to the end of that device's list (most recent), removed from any other device, list capped at
    /// <see cref="MaxEndpointsPerDevice"/>. Returns the same instance when nothing changes.</summary>
    public static LearnedState Learn(LearnedState s, string deviceId, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        if (s.EndpointIdsByDevice.TryGetValue(deviceId, out var current) && current.Count > 0
            && string.Equals(current[^1], endpointId, StringComparison.OrdinalIgnoreCase)
            && !s.EndpointIdsByDevice.Any(kv => !string.Equals(kv.Key, deviceId, StringComparison.OrdinalIgnoreCase)
                && kv.Value.Contains(endpointId, StringComparer.OrdinalIgnoreCase)))
            return s;

        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dev, ids) in s.EndpointIdsByDevice)
        {
            var kept = ids.Where(x => !string.Equals(x, endpointId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count > 0 || string.Equals(dev, deviceId, StringComparison.OrdinalIgnoreCase)) map[dev] = kept;
        }
        var list = map.TryGetValue(deviceId, out var l) ? l.ToList() : [];
        list.Add(endpointId);
        if (list.Count > MaxEndpointsPerDevice) list.RemoveRange(0, list.Count - MaxEndpointsPerDevice);
        map[deviceId] = list;
        return s with { EndpointIdsByDevice = map };
    }

    /// <summary>Reads runtime-state.json from <paramref name="path"/>: missing → empty state; unreadable or corrupt →
    /// empty state plus a warning.</summary>
    public static (LearnedState State, IReadOnlyList<ConfigIssue> Issues) Load(string path)
    {
        try
        {
            var text = AtomicFile.ReadAllTextOrNull(path);
            return text is null ? (Empty(), []) : Parse(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Empty(), [ConfigIssue.Of(IssueLevel.Warning, FileName, "Issue.State.02", FileName, ex.Message)]);
        }
    }

    /// <summary>Writes runtime-state.json atomically (.tmp + File.Replace).</summary>
    public static void Save(string path, LearnedState s) => AtomicFile.WriteAllText(path, Serialize(s));

    /// <summary>Reads the optional <c>userMode</c> object. Missing or null → null (Auto). An invalid value never makes
    /// the whole file corrupt (the learned endpoints are kept): it is ignored with a warning.</summary>
    private static SavedMode? ParseUserMode(JsonElement root, List<ConfigIssue> issues)
    {
        if (!root.TryGetProperty("userMode", out var um) || um.ValueKind == JsonValueKind.Null) return null;
        SavedMode? Invalid(string why)
        {
            issues.Add(ConfigIssue.Of(IssueLevel.Warning, "userMode", "Issue.State.03", FileName, why));
            return null;
        }
        if (um.ValueKind != JsonValueKind.Object) return Invalid("not an object");
        if (!um.TryGetProperty("mode", out var m) || m.ValueKind != JsonValueKind.String
            || !Enum.TryParse<Mode>(m.GetString(), ignoreCase: false, out var mode) || !Enum.IsDefined(mode))
            return Invalid("unknown mode");
        string? locked = null;
        if (um.TryGetProperty("lockedPreset", out var lp) && lp.ValueKind != JsonValueKind.Null)
        {
            if (lp.ValueKind != JsonValueKind.String) return Invalid("lockedPreset is not text");
            locked = lp.GetString();
        }
        DateTimeOffset? until = null;
        if (um.TryGetProperty("pausedUntil", out var pu) && pu.ValueKind != JsonValueKind.Null)
        {
            if (pu.ValueKind != JsonValueKind.String || !pu.TryGetDateTimeOffset(out var u)) return Invalid("pausedUntil is not a date");
            until = u;
        }
        return mode switch
        {
            Mode.Auto => null,
            Mode.Locked when !string.IsNullOrEmpty(locked) => new SavedMode(Mode.Locked, locked, null),
            Mode.Paused when until is not null => new SavedMode(Mode.Paused, null, until),
            _ => Invalid($"{mode} without its {(mode == Mode.Locked ? "preset" : mode == Mode.Paused ? "end" : "data")}"),
        };
    }

    private static (LearnedState, IReadOnlyList<ConfigIssue>) Corrupt(string why)
        => (Empty(), [ConfigIssue.Of(IssueLevel.Warning, FileName, "Issue.State.04", FileName, why)]);
}
