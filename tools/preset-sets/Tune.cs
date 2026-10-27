using System.Globalization;
using System.Text.RegularExpressions;
using FxAuto.Core;
using FxAuto.Core.Config;
using FxAuto.Core.FxSound;
using FxAuto.FxSound;

namespace FxAuto.PresetSets;

/// <summary>"tune" subcommand: rewrites the 34 user presets with the values of the research tables
/// (.sdd/changes/fxauto-v2/research/05 for "G435 - X", 06 for "Realtek - X"), one preset at a time, and verifies
/// each one through FxSound: the .fac is rewritten in place while another preset is selected (FxSound re-reads the
/// file on every --preset, research 01 §3.5), then --preset=X must load it without "modified", with no AutoSave and
/// with the new effects/bands in status.json. Leveling goes to config.json (ConfigJson + ConfigValidator +
/// AtomicFile). Stops at the first failure. Never kills, closes or relaunches FxSound.
/// Usage: preset-sets tune &lt;logFile&gt; &lt;doc05&gt; &lt;doc06&gt; [apply] [only=&lt;preset name&gt;]</summary>
internal static partial class Program
{
    private static readonly double[] Freqs = [62.5, 116, 214, 397, 735, 1360, 2520, 4670, 8640, 16000];

    private sealed record Target(string Name, int[] Bands, int Clarity, int Surround, int DynBoost, int Bass, double Leveling);

    private static double Cell(string s)
    {
        var t = s.Trim().Replace('−', '-').Replace("+", "");
        var sp = t.IndexOf(' ');
        if (sp > 0) t = t[..sp];
        return double.Parse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static List<Target> ParseTable(string docPath, string prefix)
    {
        var lines = File.ReadAllLines(docPath);
        var h = Array.FindIndex(lines, l => l.StartsWith("| Preset | 62,5 |", StringComparison.Ordinal));
        if (h < 0) throw new InvalidOperationException($"table not found in {docPath}");
        static string[] Cells(string l) => l.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var head = Cells(lines[h]);
        int Col(string n, bool required = true)
        {
            var i = Array.IndexOf(head, n);
            if (i < 0 && required) throw new InvalidOperationException($"column \"{n}\" missing in {docPath}");
            return i;
        }
        var (cCl, cSu, cDy, cBa, cNi) = (Col("Clarity"), Col("Surround"), Col("Dyn. Boost"), Col("Bass Boost", false), Col("Nivelado"));
        if (Col("Ambience", false) >= 0) throw new InvalidOperationException("unexpected Ambience column");
        var res = new List<Target>();
        for (var i = h + 2; i < lines.Length && lines[i].StartsWith('|'); i++)
        {
            var c = Cells(lines[i]);
            var bands = Enumerable.Range(1, 10).Select(k => (int)Cell(c[k])).ToArray();
            res.Add(new Target(prefix + c[0], bands, (int)Cell(c[cCl]), (int)Cell(c[cSu]), (int)Cell(c[cDy]),
                cBa < 0 ? 0 : (int)Cell(c[cBa]), Cell(c[cNi])));
        }
        return res;
    }

    private static readonly Regex ValueLine = new(@"^(\s*)(-?[0-9.]+): (Main \d|Integer\[\d\]|Boost/Cut|CF)$");

    /// <summary>New .fac text: Main 0/1/3/4/5 = Clarity/Surround/0/DynBoost/Bass, Main 2 = 0, Integer[0..4] = 1,
    /// Integer[5..6] untouched, bands Boost/Cut by order (CF checked against the fixed frequencies).</summary>
    private static string Rewrite(string text, Target t)
    {
        var lines = text.Split("\r\n").ToList();
        int band = -1, mains = 0, ints = 0, cuts = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var m = ValueLine.Match(lines[i]);
            if (!m.Success) continue;
            var (ind, val, key) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            string? nv = null;
            if (key.StartsWith("Main ", StringComparison.Ordinal))
            {
                mains++;
                nv = key[5] switch { '0' => $"{t.Clarity}", '1' => $"{t.Surround}", '4' => $"{t.DynBoost}", '5' => $"{t.Bass}", _ => "0" };
            }
            else if (key.StartsWith("Integer[", StringComparison.Ordinal))
            {
                ints++;
                if (key[8] is >= '0' and <= '4') nv = "1";
            }
            else if (key == "CF")
            {
                band++;
                if (band >= 10 || double.Parse(val, CultureInfo.InvariantCulture) != Freqs[band])
                    throw new InvalidOperationException($"unexpected band {band + 1} CF {val}");
            }
            else
            {
                cuts++;
                nv = t.Bands[band].ToString(CultureInfo.InvariantCulture);
            }
            if (nv is not null) lines[i] = $"{ind}{nv}: {key}";
        }
        if (mains != 6 || ints != 7 || cuts != 10 || band != 9)
            throw new InvalidOperationException($"unexpected .fac layout (mains={mains} ints={ints} bands={cuts})");
        return string.Join("\r\n", lines);
    }

    /// <summary>Re-reads the .fac and checks every value; null = OK.</summary>
    private static string? CheckFile(string file, Target t)
    {
        var text = File.ReadAllText(file);
        var expected = Rewrite(text, t);
        return expected == text ? null : "file on disk differs from the expected values";
    }

    private static string? CheckDsp(FxStatus st, Target t)
    {
        var d = st.Dsp;
        if (d is null) return "status.json has no DSP values";
        var fx = new (string, int)[] { ("clarity", t.Clarity), ("surround", t.Surround), ("ambience", 0), ("dynamicboost", t.DynBoost), ("bass", t.Bass) };
        foreach (var (n, raw) in fx)
            if (!d.Effects.TryGetValue(n, out var v) || Math.Abs(v - raw * 10.0 / 127.0) > 0.05)
                return $"effect {n} = {(d.Effects.TryGetValue(n, out var x) ? x : double.NaN)} (expected {raw * 10.0 / 127.0:0.###})";
        if (d.Bands.Count != 10) return $"{d.Bands.Count} bands";
        for (var i = 0; i < 10; i++)
            if (Math.Abs(d.Bands[i] - t.Bands[i]) > 0.05) return $"band {Freqs[i]} = {d.Bands[i]} (expected {t.Bands[i]})";
        return null;
    }

    private static async Task<int> TuneAsync(string[] args)
    {
        // args: tune <log> <doc05> <doc06> [apply] [only=<name>]
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: preset-sets tune <logFile> <doc05> <doc06> [apply] [only=<name>]");
            return 2;
        }
        _w = new StreamWriter(args[1], append: true);
        var apply = args.Skip(4).Contains("apply");
        var only = args.Skip(4).FirstOrDefault(a => a.StartsWith("only=", StringComparison.Ordinal))?[5..];
        Write($"preset-sets tune {(apply ? "APPLY" : "plan only")} only={only ?? "-"} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        if (System.Diagnostics.Process.GetProcessesByName("FxAuto").Length > 0)
        {
            Write("ABORT: an FxAuto process is running.");
            return 3;
        }

        var targets = ParseTable(args[2], G).Concat(ParseTable(args[3], R)).ToList();
        Write($"targets: {targets.Count}");
        foreach (var t in targets)
            Write($"  {t.Name}: bands=[{string.Join(" ", t.Bands)}] clarity={t.Clarity} surround={t.Surround} dyn={t.DynBoost} bass={t.Bass} leveling={t.Leveling}");
        if (targets.Count != 34 || targets.Select(t => t.Name).Distinct().Count() != 34)
        {
            Write("ABORT: expected 34 distinct presets.");
            return 4;
        }
        if (only is not null) targets = targets.Where(t => t.Name == only).ToList();
        if (targets.Count == 0)
        {
            Write("ABORT: nothing selected.");
            return 4;
        }

        using var all = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = all.Token;
        var port = new FxSoundPort(() => null, Log);
        var ctrl = new FxSoundController(port, SystemClock.Instance, Log);
        var files = port.CreatePresetFileStore();

        var st0 = await ctrl.QueryAsync(ct);
        if (st0 is null) { Write("ABORT: FxSound did not answer --status."); return 4; }
        var auto0 = files.AutoSaveNames();
        var initial = st0.SelectedPreset;
        Write($"initial: preset=\"{initial}\" power={st0.Power} users={st0.UserPresets.Count} " +
              $"modified=[{string.Join(", ", st0.ModifiedByPreset.Where(kv => kv.Value).Select(kv => kv.Key))}] autoSave=[{string.Join(", ", auto0)}]");
        if (!st0.Power || st0.SelectedModified == true || st0.ModifiedByPreset.Values.Any(m => m) || auto0.Count > 0)
        {
            Write("ABORT: preconditions not met (power on, nothing modified, AutoSave empty).");
            return 4;
        }
        foreach (var t in targets)
            if (!st0.UserPresets.Contains(t.Name) || files.FindPresetFile(t.Name) is null)
            {
                Write($"ABORT: \"{t.Name}\" missing in FxSound or on disk.");
                return 4;
            }
        LoadConfig();
        if (!apply) return 0;

        var done = new List<Target>();
        foreach (var t in targets)
        {
            Write($"── {t.Name}");
            var file = files.FindPresetFile(t.Name)!;
            var err = await ctrl.RunExclusiveAsync<string?>(async s =>
            {
                var st = await s.QueryAsync(ct);
                if (st is null) return "no --status";
                if (st.SelectedPreset == t.Name)
                {
                    // park on another preset so the next --preset really re-reads the file
                    var park = initial is not null && initial != t.Name ? initial : st.UserPresets.First(u => u != t.Name);
                    await s.SendAsync(FxCommand.SelectPreset(park), ct);
                    st = await s.QueryAsync(ct);
                    if (st is null || st.SelectedPreset != park) return $"could not park on \"{park}\"";
                    Write($"   parked on \"{park}\"");
                }
                var old = File.ReadAllText(file);
                AtomicFile.WriteAllText(file, Rewrite(old, t));
                var fileErr = CheckFile(file, t);
                if (fileErr is not null) return fileErr;
                Write($"   written {file}");
                await s.SendAsync(FxCommand.SelectPreset(t.Name), ct);
                st = await s.QueryAsync(ct);
                if (st is null) return "no --status after --preset";
                var mod = st.ModifiedByPreset.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
                var auto = files.AutoSaveNames();
                Write($"   status: selected=\"{st.SelectedPreset}\" modified=[{string.Join(", ", mod)}] autoSave=[{string.Join(", ", auto)}] " +
                      $"effects={{{string.Join(", ", st.Dsp?.Effects.Select(kv => $"{kv.Key}={kv.Value:0.###}") ?? [])}}} bands=[{string.Join(" ", st.Dsp?.Bands ?? [])}]");
                if (st.SelectedPreset != t.Name) return "not selected";
                if (st.SelectedModified == true || mod.Count > 0) return "modified after load";
                if (auto.Count > 0) return "AutoSave not empty";
                return CheckDsp(st, t) ?? CheckFile(file, t);
            }, TimeSpan.FromSeconds(90), ct);
            Write($"   check={(err is null ? "OK" : "FAIL: " + err)}");
            if (err is not null)
            {
                Write("STOP: verification failed; nothing else is run.");
                return 5;
            }
            done.Add(t);
        }

        // ── leveling in config.json ──
        var cfg = LoadConfig();
        var presets = cfg.Presets.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var t in done)
        {
            var before = presets.TryGetValue(t.Name, out var m) ? m : null;
            presets[t.Name] = before is null ? new PresetMeta(t.Leveling, null) : before with { VolumeLeveling = t.Leveling };
            if (before?.VolumeLeveling != t.Leveling) Write($"   leveling {t.Name}: {before?.VolumeLeveling} -> {t.Leveling}");
        }
        cfg = cfg with { Presets = presets };
        var stF = await ctrl.QueryAsync(ct);
        var issues = ConfigValidator.Validate(cfg, stF);
        foreach (var i in issues) Write($"   issue {i.Level} {i.Path}: {i.Message}");
        var errors = issues.Count(i => i.Level == IssueLevel.Error);
        var warnings = issues.Count(i => i.Level == IssueLevel.Warning);
        Write($"validator: errors={errors} warnings={warnings}");
        if (errors > 0 || warnings > 0) { Write("STOP: config not saved (validator)."); return 7; }
        SaveConfig(cfg, "leveling");
        var (re, reIssues) = ConfigJson.Parse(File.ReadAllText(ConfigPath));
        var reVal = re is null ? [] : ConfigValidator.Validate(re, stF);
        Write($"re-read: parse issues={reIssues.Count} validator errors={reVal.Count(i => i.Level == IssueLevel.Error)} warnings={reVal.Count(i => i.Level == IssueLevel.Warning)}");
        foreach (var t in done)
            if (re is null || !re.Presets.TryGetValue(t.Name, out var mm) || mm.VolumeLeveling != t.Leveling)
            {
                Write($"STOP: leveling of {t.Name} not saved.");
                return 8;
            }

        // ── put back the initial selection ──
        if (initial is not null)
        {
            var stR = await ctrl.RunExclusiveAsync(async s =>
            {
                await s.SendAsync(FxCommand.SelectPreset(initial), ct);
                return await s.QueryAsync(ct);
            }, TimeSpan.FromSeconds(30), ct);
            Write($"restored selection: \"{stR?.SelectedPreset}\" (expected \"{initial}\") modified={stR?.SelectedModified} autoSave=[{string.Join(", ", files.AutoSaveNames())}]");
            if (stR?.SelectedPreset != initial) return 9;
        }
        Write($"done: {done.Count} presets tuned and verified");
        return 0;
    }
}
