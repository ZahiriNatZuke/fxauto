using System.Text.Json;

namespace FxAuto.Core.FxSound;

public static class FxStatusParser
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses status.json. Invalid or truncated JSON → null. Never throws.</summary>
    public static FxStatus? TryParse(string json, DateTimeOffset writtenAt)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, Options);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var version = Str(root, "version");
            var power = Bool(root, "power") ?? false;
            var selected = Str(root, "selected_preset");
            var output = Str(root, "selected_output");

            double? leveling = null;
            double? masterGain = null;
            var bands = new List<double>();
            var hasEq = root.TryGetProperty("equalizer", out var eq) && eq.ValueKind == JsonValueKind.Object;
            if (hasEq)
            {
                leveling = Num(eq, "volume_leveling");
                masterGain = Num(eq, "master_gain");
                ReadBands(eq, bands);
            }

            Dictionary<string, double>? effects = null;
            if (root.TryGetProperty("effects", out var fx) && fx.ValueKind == JsonValueKind.Object)
            {
                effects = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var e in fx.EnumerateObject())
                {
                    if (e.Value.ValueKind == JsonValueKind.Number && e.Value.TryGetDouble(out var d)) effects[e.Name] = d;
                }
            }

            var outputs = new List<string>();
            if (root.TryGetProperty("output_devices", out var outs) && outs.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in outs.EnumerateArray())
                {
                    if (o.ValueKind == JsonValueKind.String && o.GetString() is { Length: > 0 } name) outputs.Add(name);
                }
            }

            var builtIn = new List<string>();
            var user = new List<string>();
            bool? selectedModified = null;
            var modified = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (root.TryGetProperty("presets", out var presets) && presets.ValueKind == JsonValueKind.Object)
            {
                ReadList(presets, "built_in", builtIn, selected, ref selectedModified, modified);
                ReadList(presets, "user_defined", user, selected, ref selectedModified, modified);
            }

            var dsp = effects is null && bands.Count == 0 && masterGain is null ? null
                : new FxDsp(effects ?? new Dictionary<string, double>(StringComparer.Ordinal), bands) { MasterGain = masterGain };
            return new FxStatus(version, selected, power, selectedModified, leveling, output, user, builtIn, writtenAt)
            {
                OutputDevices = outputs,
                ModifiedByPreset = modified,
                Dsp = dsp,
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // equalizer.bands[] = [{index, frequency, gain}] → gains ordered by index (array order if index is missing)
    private static void ReadBands(JsonElement eq, List<double> bands)
    {
        if (!eq.TryGetProperty("bands", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        var list = new List<(double Index, double Gain)>();
        var i = 0;
        foreach (var b in arr.EnumerateArray())
        {
            double? gain = b.ValueKind switch
            {
                JsonValueKind.Object => Num(b, "gain"),
                JsonValueKind.Number when b.TryGetDouble(out var g) => g,
                _ => null,
            };
            if (gain is double gv) list.Add((b.ValueKind == JsonValueKind.Object ? Num(b, "index") ?? i : i, gv));
            i++;
        }
        bands.AddRange(list.OrderBy(x => x.Index).Select(x => x.Gain));
    }

    private static void ReadList(JsonElement presets, string prop, List<string> names, string? selected, ref bool? selectedModified,
        Dictionary<string, bool> modifiedByPreset)
    {
        if (!presets.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var item in arr.EnumerateArray())
        {
            string? name;
            bool? modified = null;
            if (item.ValueKind == JsonValueKind.String) name = item.GetString();
            else if (item.ValueKind == JsonValueKind.Object)
            {
                name = Str(item, "name");
                modified = Bool(item, "modified");
            }
            else continue;
            if (string.IsNullOrEmpty(name)) continue;
            names.Add(name);
            if (modified is not null)
                modifiedByPreset[name] = (modifiedByPreset.TryGetValue(name, out var prev) && prev) || modified.Value;
            if (selected is not null && string.Equals(name, selected, StringComparison.Ordinal) && modified is not null)
                selectedModified = (selectedModified ?? false) || modified.Value;
        }
    }

    private static string? Str(JsonElement o, string p)
        => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement o, string p)
    {
        if (!o.TryGetProperty(p, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when v.TryGetDouble(out var d) => d != 0,
            _ => null,
        };
    }

    private static double? Num(JsonElement o, string p)
        => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
