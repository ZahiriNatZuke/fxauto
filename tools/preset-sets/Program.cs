using System.Diagnostics;
using System.Text.Json;
using FxAuto.Core;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.FxSound;
using FxAuto.Core.Presets;
using FxAuto.FxSound;

namespace FxAuto.PresetSets;

/// <summary>Builds the per-device preset sets decided on 2026-10-26, one verified operation at a time:
/// Realtek - X from every G435 - X, Realtek - Live Stream redone from G435 - Video, G435 - Live Stream from
/// G435 - Video; then the config: Realtek column = G435 column with the Realtek prefix, Music on G435 restored,
/// Realtek default = Realtek - General, startFxSoundIfClosed off. Stops at the first failure. Never kills, closes
/// or relaunches FxSound. Idempotent: presets that already exist are skipped.</summary>
internal static partial class Program
{
    private const string G = "G435 - ";
    private const string R = "Realtek - ";
    private const string LiveSource = "G435 - Video";
    private static readonly string[] Suffixes =
    [
        "General", "Music", "Video", "Sports TV", "Movie", "Movie Night", "FPS", "TPS", "Racing", "Sports Game",
        "Strategy", "RPG", "Action", "Horror", "MOBA", "Voice",
    ];

    private static StreamWriter? _w;
    private static readonly object Lk = new();

    private static void Write(string line)
    {
        lock (Lk)
        {
            var l = $"{DateTimeOffset.Now:HH:mm:ss.fff} {line}";
            Console.WriteLine(l);
            _w?.WriteLine(l);
            _w?.Flush();
        }
    }

    private static void Log(string ev, object data) => Write($"[fx] {ev} {JsonSerializer.Serialize(data)}");

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FxAuto", "config.json");

    private static FxAutoConfig LoadConfig()
    {
        var text = File.ReadAllText(ConfigPath);
        var (cfg, issues) = ConfigJson.Parse(text);
        if (cfg is null || issues.Any(i => i.Level == IssueLevel.Error))
            throw new InvalidOperationException("config.json does not parse: " + string.Join("; ", issues.Select(i => i.Message)));
        return cfg;
    }

    private static void SaveConfig(FxAutoConfig cfg, string why)
    {
        var json = ConfigJson.Serialize(cfg);
        AtomicFile.WriteAllText(ConfigPath, json, Path.Combine(Path.GetDirectoryName(ConfigPath)!, "config.json.bak"));
        Write($"config saved ({why})");
    }

    /// <summary>The .fac content without its name line (line 3), to tell whether a preset is a copy of another.</summary>
    private static string? Body(PresetFileStore files, string name)
    {
        var f = files.FindPresetFile(name);
        if (f is null) return null;
        var lines = File.ReadAllLines(f).ToList();
        if (lines.Count >= 3) lines.RemoveAt(2);
        return string.Join("\n", lines);
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "tune") return await TuneAsync(args);
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: preset-sets <logFile> [apply]");
            return 2;
        }
        _w = new StreamWriter(args[0], append: true);
        var apply = args.Length > 1 && args[1] == "apply";
        Write($"preset-sets {(apply ? "APPLY" : "plan only")} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        if (Process.GetProcessesByName("FxAuto").Length > 0)
        {
            Write("ABORT: an FxAuto process is running.");
            return 3;
        }

        using var all = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = all.Token;
        var clock = SystemClock.Instance;
        var port = new FxSoundPort(() => null, Log);
        var ctrl = new FxSoundController(port, clock, Log);
        var files = port.CreatePresetFileStore();
        var ops = new PresetOperations(ctrl, files, clock, Log) { CurrentConfig = () => LoadConfig() };

        var st0 = await ctrl.QueryAsync(ct);
        if (st0 is null)
        {
            Write("ABORT: FxSound did not answer --status.");
            return 4;
        }
        var autoSave0 = files.AutoSaveNames();
        Write($"initial: preset=\"{st0.SelectedPreset}\" output=\"{st0.SelectedOutput}\" power={st0.Power} " +
              $"userPresets={st0.UserPresets.Count} modified=[{string.Join(", ", st0.ModifiedByPreset.Where(kv => kv.Value).Select(kv => kv.Key))}] " +
              $"autoSave=[{string.Join(", ", autoSave0)}]");
        if (!st0.Power || st0.SelectedModified == true || st0.ModifiedByPreset.Values.Any(m => m) || autoSave0.Count > 0)
        {
            Write("ABORT: preconditions not met (power on, nothing modified, AutoSave empty).");
            return 4;
        }
        foreach (var s in Suffixes)
            if (!st0.UserPresets.Contains(G + s))
            {
                Write($"ABORT: \"{G + s}\" is missing in FxSound.");
                return 4;
            }

        var cfg = LoadConfig();
        var plan = new List<PresetOpRequest>();
        var users = st0.UserPresets.ToHashSet(StringComparer.Ordinal);
        foreach (var s in Suffixes)
            if (!users.Contains(R + s)) plan.Add(new(PresetOpKind.Duplicate, Source: G + s, Target: R + s));
        var liveR = R + "Live Stream";
        if (users.Contains(liveR) && Body(files, liveR) != Body(files, LiveSource))
        {
            plan.Add(new(PresetOpKind.Delete, Source: liveR));
            plan.Add(new(PresetOpKind.Duplicate, Source: LiveSource, Target: liveR));
        }
        else if (!users.Contains(liveR)) plan.Add(new(PresetOpKind.Duplicate, Source: LiveSource, Target: liveR));
        if (!users.Contains(G + "Live Stream")) plan.Add(new(PresetOpKind.Duplicate, Source: LiveSource, Target: G + "Live Stream"));

        Write($"plan: {plan.Count} operations");
        foreach (var p in plan) Write($"  {p.Kind} {p.Source} -> {p.Target}");
        if (!apply) return 0;

        // ── presets, one verified operation at a time ──
        var n = 0;
        foreach (var req in plan)
        {
            n++;
            Write($"── op {n}/{plan.Count}: {req.Kind} \"{req.Source}\" -> \"{req.Target}\"");
            var res = await ops.RunAsync(req, ct);
            Write($"   ok={res.Ok} error={res.Error ?? "-"} steps={res.Steps.Count} done=[{string.Join(", ", res.Done.Select(d => d.From + "→" + d.To))}]");
            if (!res.Ok)
            {
                Write("STOP: operation failed; nothing else is run.");
                return 5;
            }
            cfg = PresetOperations.ApplyToConfig(cfg, req, res);
            SaveConfig(cfg, $"op {n}");

            var st = await ctrl.QueryAsync(ct);
            var mod = st?.ModifiedByPreset.Where(kv => kv.Value).Select(kv => kv.Key).ToList() ?? [];
            var auto = files.AutoSaveNames();
            var present = req.Kind == PresetOpKind.Delete
                ? st is not null && !st.UserPresets.Contains(req.Source!)
                : st is not null && st.UserPresets.Contains(req.Target!);
            Write($"   status: selected=\"{st?.SelectedPreset}\" users={st?.UserPresets.Count} modified=[{string.Join(", ", mod)}] autoSave=[{string.Join(", ", auto)}] check={(present ? "OK" : "FAIL")}");
            if (st is null || !present || mod.Count > 0 || auto.Count > 0)
            {
                Write("STOP: verification after the operation failed.");
                return 6;
            }
        }

        // ── config: assignments, defaults, general ──
        cfg = LoadConfig();
        var sourceAssign = cfg.Assignments.Where(a => a.Source is not null).ToList();
        var g435 = cfg.Assignments
            .Where(a => a.Source is null && a.Device == "g435" && a.Category is not null
                        && a.Category != Categories.Music && a.Category != Categories.LiveStream)
            .ToList();
        g435.Insert(0, new Assignment("g435", Categories.Music, null, G + "Music"));
        g435.Add(new Assignment("g435", Categories.LiveStream, null, G + "Live Stream"));
        var realtek = g435.Select(a => a with { Device = "realtek", Preset = R + a.Preset[G.Length..] }).ToList();
        var others = cfg.Assignments.Where(a => a.Source is null && a.Device is not ("g435" or "realtek")).ToList();
        cfg = cfg with
        {
            General = cfg.General with { StartFxSoundIfClosed = false },
            Devices = [.. cfg.Devices.Select(d => d.Id == "realtek" ? d with { DefaultPreset = R + "General" } : d)],
            Assignments = [.. g435, .. realtek, .. others, .. sourceAssign],
        };
        var stF = await ctrl.QueryAsync(ct);
        var issues = ConfigValidator.Validate(cfg, stF);
        foreach (var i in issues) Write($"   issue {i.Level} {i.Path}: {i.Message}");
        var errors = issues.Count(i => i.Level == IssueLevel.Error);
        var warnings = issues.Count(i => i.Level == IssueLevel.Warning);
        Write($"validator: errors={errors} warnings={warnings}");
        if (errors > 0 || warnings > 0)
        {
            Write("STOP: config not saved (validator).");
            return 7;
        }
        SaveConfig(cfg, "assignments + defaults + general");
        var (re, reIssues) = ConfigJson.Parse(File.ReadAllText(ConfigPath));
        var reVal = re is null ? [] : ConfigValidator.Validate(re, stF);
        Write($"re-read: parse issues={reIssues.Count} validator errors={reVal.Count(i => i.Level == IssueLevel.Error)} warnings={reVal.Count(i => i.Level == IssueLevel.Warning)}");

        // ── final picture ──
        Write("table (category: G435 | Realtek):");
        foreach (var a in g435)
            Write($"   {a.Category}: {a.Preset} | {realtek.First(r => r.Category == a.Category).Preset}");
        var userF = stF?.UserPresets ?? [];
        var modF = stF?.ModifiedByPreset.Where(kv => kv.Value).Select(kv => kv.Key).ToList() ?? [];
        Write($"final: userPresets={userF.Count} (G435={userF.Count(u => u.StartsWith(G, StringComparison.Ordinal))}, Realtek={userF.Count(u => u.StartsWith(R, StringComparison.Ordinal))}) " +
              $"modified=[{string.Join(", ", modF)}] autoSave=[{string.Join(", ", files.AutoSaveNames())}] selected=\"{stF?.SelectedPreset}\"");
        Write("leveling: " + string.Join(", ", re!.Presets.OrderBy(k => k.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value.VolumeLeveling}")));
        return userF.Count == 34 && modF.Count == 0 ? 0 : 8;
    }
}
