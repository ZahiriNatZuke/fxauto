using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FxAuto.Core;
using FxAuto.Core.FxSound;
using FxAuto.Core.Presets;

namespace FxAuto.Tests;

/// <summary>Manual clock for the engine.</summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    public void Advance(int ms) => Now = Now.AddMilliseconds(ms);
}

/// <summary>Fake FxSound: applies presets, writes an in-memory status.json and counts simultaneous commands.</summary>
internal sealed partial class FakePort : IFxSoundPort
{
    public static readonly string[] G435 =
    [
        "G435_General", "G435_Musica", "G435_Video", "G435_DeporteTV", "G435_Cine", "G435_CineNoche",
        "G435_FPS", "G435_TPS", "G435_Carreras", "G435_JuegoDeporte", "G435_Estrategia", "G435_RPG",
        "G435_Accion", "G435_Terror", "G435_MOBA", "G435_Voz",
    ];

    private readonly object _gate = new();
    private int _concurrent;
    private string? _json;
    private DateTimeOffset? _written;

    public bool MainRunning { get; set; } = true;
    public bool? Elevated { get; set; } = false;
    public string Selected { get; set; } = "G435_General";
    public double Leveling { get; set; }
    public bool Power { get; set; } = true;
    /// <summary>FxSound receives the command but does not switch preset.</summary>
    public bool IgnorePresetCommands { get; set; }
    /// <summary>FxSound switches preset but does not apply the requested leveling.</summary>
    public bool IgnoreLeveling { get; set; }
    /// <summary>--status exits fine but does not rewrite the file.</summary>
    public bool StatusNeverUpdates { get; set; }
    /// <summary>The next command hangs: SendAsync returns null and a pending process remains.</summary>
    public bool HangNext { get; set; }
    public TimeSpan CommandDelay { get; set; } = TimeSpan.FromMilliseconds(5);
    public bool HasPendingLaunched { get; set; }
    public int MaxConcurrent { get; private set; }
    public ConcurrentQueue<string> Sent { get; } = new();
    public int StartCalls { get; private set; }
    /// <summary>status.json selected_output.</summary>
    public string SelectedOutput { get; set; } = "G435";
    /// <summary>status.json output_devices[] (exact names --output accepts).</summary>
    public List<string> OutputDevices { get; } = ["Speakers (Realtek(R) Audio)", "Headphones (G435 Wireless Gaming Headset)"];
    /// <summary>Per-preset "modified" flags written to status.json (missing = false).</summary>
    public ConcurrentDictionary<string, bool> Modified { get; } = new(StringComparer.Ordinal);

    public bool IsMainRunning() => MainRunning;
    public bool? IsElevated() => Elevated;

    public Task<bool> StartMainMinimizedAsync(CancellationToken ct)
    {
        StartCalls++;
        MainRunning = true;
        return Task.FromResult(true);
    }

    public IReadOnlyList<string> SentPresets()
        => Sent.Select(a => PresetRx().Match(a)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();

    public async Task<int?> SendAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var c = Interlocked.Increment(ref _concurrent);
        lock (_gate) MaxConcurrent = Math.Max(MaxConcurrent, c);
        try
        {
            Sent.Enqueue(arguments);
            await Task.Delay(CommandDelay, ct).ConfigureAwait(false);
            if (HangNext)
            {
                HangNext = false;
                HasPendingLaunched = true;
                return null;
            }
            if (arguments == FxCommand.Status)
            {
                if (!StatusNeverUpdates) WriteStatus();
                return 0;
            }
            var m = PresetRx().Match(arguments);
            if (m.Success && !IgnorePresetCommands && G435.Contains(m.Groups[1].Value))
            {
                Selected = m.Groups[1].Value;
                if (arguments.Contains("--power=1", StringComparison.Ordinal)) Power = true;
                var lv = LevelingRx().Match(arguments);
                if (lv.Success && !IgnoreLeveling) Leveling = double.Parse(lv.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            return 0;
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public (string? Json, DateTimeOffset? WrittenAt) ReadStatusFile()
    {
        lock (_gate) return (_json, _written);
    }

    private void WriteStatus()
    {
        var sb = new StringBuilder();
        sb.Append("{\"version\":\"1.2.16.0\",\"power\":").Append(Power ? "true" : "false");
        sb.Append(",\"presets\":{\"built_in\":[{\"name\":\"General\",\"modified\":false}],\"user_defined\":[");
        sb.Append(string.Join(',', G435.Select(p => $"{{\"name\":\"{p}\",\"modified\":{(IsModified(p) ? "true" : "false")}}}")));
        sb.Append("]},\"selected_preset\":\"").Append(Selected).Append("\",\"selected_output\":\"").Append(SelectedOutput).Append("\",");
        sb.Append("\"output_devices\":[").Append(string.Join(',', OutputDevices.Select(o => $"\"{o}\""))).Append("],");
        sb.Append("\"equalizer\":{\"volume_leveling\":").Append(Leveling.ToString(CultureInfo.InvariantCulture)).Append("}}");
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            if (_written is DateTimeOffset prev && now <= prev) now = prev.AddTicks(1);
            _json = sb.ToString();
            _written = now;
        }
    }

    private bool IsModified(string preset) => Modified.TryGetValue(preset, out var m) && m;

    [GeneratedRegex("--preset=\"([^\"]*)\"")]
    private static partial Regex PresetRx();

    [GeneratedRegex("--volume_leveling=([0-9.]+)")]
    private static partial Regex LevelingRx();
}

/// <summary>In-memory FxSound preset files (.fac): file path → content, where line 3 is the preset name. Simulates
/// Presets\ (user), Factsoft\ (built-in) and AutoSave\.</summary>
internal sealed class FakePresetFileStore : IPresetFileStore
{
    public const string PresetsDir = @"C:\Fake\FxSound\Presets\";
    public const string FactsoftDir = @"C:\Fake\FxSound\Factsoft\";

    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AutoSaved { get; } = new(StringComparer.Ordinal);
    public int MaxPresets { get; set; } = PresetNameRules.DefaultMaxUserPresets;
    /// <summary>Next WriteExactCopy fails with this error.</summary>
    public string? FailNextWrite { get; set; }

    /// <summary>Adds a .fac whose line 3 is <paramref name="name"/>.</summary>
    public string Add(string name, bool builtIn = false, string body = "bands=0,0,0,0,0,0,0,0,0,0")
    {
        var path = (builtIn ? FactsoftDir : PresetsDir) + name + ".fac";
        Files[path] = $"FxSound preset\nversion=1\n{name}\n{body}\n";
        return path;
    }

    public static string? NameOf(string content)
    {
        var lines = content.Split('\n');
        return lines.Length >= 3 ? lines[2] : null;
    }

    public string? FindPresetFile(string presetName)
    {
        foreach (var dir in new[] { PresetsDir, FactsoftDir })
        {
            foreach (var (path, content) in Files)
            {
                if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && NameOf(content) == presetName)
                    return path;
            }
        }
        return null;
    }

    public string? WriteExactCopy(string sourceFile, string newName)
    {
        if (FailNextWrite is { } err)
        {
            FailNextWrite = null;
            return err;
        }
        if (!Files.TryGetValue(sourceFile, out var content)) return "source file not found: " + sourceFile;
        var lines = content.Split('\n');
        if (lines.Length < 3) return "source file has no name line";
        lines[2] = newName;
        Files[PresetsDir + newName + ".fac"] = string.Join('\n', lines);
        return null;
    }

    public bool AutoSaveExists(string presetName) => AutoSaved.Contains(presetName);

    public int MaxUserPresets() => MaxPresets;
}
