using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FxAuto.Core;
using FxAuto.Core.Config;
using FxAuto.Core.FxSound;
using FxAuto.Core.Presets;
using FxAuto.FxSound;

namespace FxAuto.Integration;

/// <summary>Real port with every launched command traced (begin/end), to check that an exclusive session and an
/// engine apply never interleave. Never starts FxSound.</summary>
internal sealed class TracingPort(IFxSoundPort inner, Action<string> trace) : IFxSoundPort
{
    public bool IsMainRunning() => inner.IsMainRunning();
    public bool? IsElevated() => inner.IsElevated();
    public Task<bool> StartMainMinimizedAsync(CancellationToken ct)
        => throw new InvalidOperationException("the integration run never starts FxSound");
    public async Task<int?> SendAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        trace("send-begin " + arguments);
        var r = await inner.SendAsync(arguments, timeout, ct).ConfigureAwait(false);
        trace("send-end " + arguments);
        return r;
    }
    public (string? Json, DateTimeOffset? WrittenAt) ReadStatusFile() => inner.ReadStatusFile();
    public bool HasPendingLaunched => inner.HasPendingLaunched;
}

/// <summary>Wraps the real port reporting FxSound as closed or elevated. Nothing reaches FxSound: every launch
/// attempt is only counted (expected 0).</summary>
internal sealed class SimulatedPort(IFxSoundPort inner, bool closed, bool elevated) : IFxSoundPort
{
    private int _sends;
    private int _starts;
    public int Sends => Volatile.Read(ref _sends);
    public int Starts => Volatile.Read(ref _starts);
    public bool IsMainRunning() => !closed && inner.IsMainRunning();
    public bool? IsElevated() => elevated ? true : inner.IsElevated();
    public Task<bool> StartMainMinimizedAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _starts);
        return Task.FromResult(false);
    }
    public Task<int?> SendAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        Interlocked.Increment(ref _sends);
        return Task.FromResult<int?>(null);
    }
    public (string? Json, DateTimeOffset? WrittenAt) ReadStatusFile() => inner.ReadStatusFile();
    public bool HasPendingLaunched => inner.HasPendingLaunched;
}

internal static class Program
{
    private const string TestName = "Test - Integration";
    private const string TestRenamed = "Test - Integration Renamed";

    private static StreamWriter? _w;
    private static readonly object Lk = new();
    private static readonly List<string> Trace = [];
    private static readonly List<(string Case, string Verdict, string Detail)> Results = [];

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

    private static void Log(string ev, object data)
    {
        Write($"[fx] {ev} {JsonSerializer.Serialize(data)}");
        if (ev == "exclusive")
        {
            var json = JsonSerializer.Serialize(data);
            AddTrace(json.Contains("\"begin\"", StringComparison.Ordinal) ? "exclusive-begin" : "exclusive-end");
        }
    }

    private static void AddTrace(string t)
    {
        lock (Lk) Trace.Add(t);
    }

    private static void Result(string c, string verdict, string detail)
    {
        lock (Lk) Results.Add((c, verdict, detail));
        Write($"=== RESULT {c}: {verdict} — {detail}");
    }

    private static FxStatus? ReadStatusDirect(IFxSoundPort port)
    {
        var (json, at) = port.ReadStatusFile();
        return json is null ? null : FxStatusParser.TryParse(json, at ?? DateTimeOffset.Now);
    }

    private static bool Modified(FxStatus st, string name) => st.ModifiedByPreset.TryGetValue(name, out var m) && m;

    /// <summary>Volume leveling per preset, from FxAuto's config.json (empty if it cannot be read).</summary>
    private static Dictionary<string, double> ReadLeveling()
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FxAuto", "config.json");
            var (cfg, _) = ConfigJson.Parse(File.ReadAllText(path));
            if (cfg is not null)
                foreach (var (name, meta) in cfg.Presets) map[name] = meta.VolumeLeveling;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Write($"leveling not read: {ex.Message}");
        }
        return map;
    }

    private static Dictionary<string, string> HashFolder(string folder)
        => Directory.Exists(folder)
            ? Directory.GetFiles(folder).ToDictionary(f => Path.GetFileName(f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.OrdinalIgnoreCase)
            : [];

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: integration <logFile> [presetsBackupFolder]");
            return 2;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        _w = new StreamWriter(args[0], append: false);
        var backup = args.Length > 1 ? args[1] : null;
        Write($"FxAuto integration run, {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        if (Process.GetProcessesByName("FxAuto").Length > 0)
        {
            Write("ABORT: an FxAuto process is running; the run needs FxSound to itself.");
            return 3;
        }
        if (backup is null || !Directory.Exists(backup) || Directory.GetFiles(backup, "*.fac").Length == 0)
        {
            Write("ABORT: no presets backup folder given (copy %APPDATA%\\FxSound\\Presets first).");
            return 3;
        }

        using var all = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = all.Token;
        var clock = SystemClock.Instance;
        var real = new FxSoundPort(() => null, Log);
        var port = new TracingPort(real, AddTrace);
        var ctrl = new FxSoundController(port, clock, Log);
        var files = real.CreatePresetFileStore();
        var ops = new PresetOperations(ctrl, files, clock, Log);
        var leveling = ReadLeveling();
        Write($"leveling per preset read: {leveling.Count} entries");
        double Lev(string p) => leveling.TryGetValue(p, out var v) ? v : 0;

        Write($"real port: IsMainRunning={real.IsMainRunning()} IsElevated={real.IsElevated()?.ToString() ?? "unknown"} mainPids={real.MainPids().Count}");
        var st0 = await ctrl.QueryAsync(ct);
        if (st0 is null)
        {
            Write("ABORT: FxSound did not answer --status.");
            return 4;
        }
        var initialPreset = st0.SelectedPreset ?? "";
        var initialOutput = st0.SelectedOutput ?? "";
        var g435Out = st0.OutputDevices.FirstOrDefault(o => o.Contains("G435", StringComparison.Ordinal));
        var realtekOut = st0.OutputDevices.FirstOrDefault(o => o.Contains("Realtek", StringComparison.Ordinal));
        Write($"initial: preset=\"{initialPreset}\" output=\"{initialOutput}\" power={st0.Power} leveling={st0.VolumeLeveling} " +
              $"userPresets={st0.UserPresets.Count} outputs={st0.OutputDevices.Count}");
        var autoSave0 = files.AutoSaveNames();
        Write($"initial AutoSave entries: {autoSave0.Count}");
        var presetsHash0 = HashFolder(files.PresetsFolder);
        var backupHash = HashFolder(backup);
        Write($"Presets\\ files={presetsHash0.Count}, backup files={backupHash.Count}, backup identical={presetsHash0.Count == backupHash.Count && presetsHash0.All(kv => backupHash.TryGetValue(kv.Key, out var h) && h == kv.Value)}");
        if (!st0.Power || st0.SelectedModified == true || st0.ModifiedByPreset.Values.Any(m => m) || g435Out is null || realtekOut is null
            || !string.Equals(initialOutput, g435Out, StringComparison.Ordinal))
        {
            Write("ABORT: preconditions not met (power on, nothing modified, output on G435, G435 and Realtek listed).");
            return 4;
        }

        try
        {
            await Case1Apply16(ctrl, real, st0, Lev, ct);
            await Case2Output(ctrl, ops, real, g435Out, realtekOut, ct);
            await Case3And4(ctrl, ops, files, real, Lev, autoSave0, presetsHash0, ct);
            await Case5ClosedElevated(real, files, clock, g435Out, realtekOut, ct);
        }
        catch (Exception ex)
        {
            Write($"UNEXPECTED {ex.GetType().Name}: {ex.Message}");
            Result("run", "FAIL", "unexpected exception: " + ex.Message);
        }
        finally
        {
            // Always back on G435 with the initial preset (fresh token: the global one may have expired).
            using var fin = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var st = await ctrl.QueryAsync(fin.Token);
            if (st is not null && !string.Equals(st.SelectedOutput, g435Out, StringComparison.Ordinal))
            {
                var back = await ops.RunAsync(new PresetOpRequest(PresetOpKind.SwitchOutput, OutputName: g435Out), fin.Token);
                Write($"final: output back to G435 ok={back.Ok} error={back.Error}");
            }
            var ap = await ctrl.ApplyAsync(initialPreset, Lev(initialPreset), fin.Token);
            Write($"final: initial preset re-applied ok={ap.Ok} actual=\"{ap.Actual}\" reason={ap.FailureReason}");
            var fs = await ctrl.QueryAsync(fin.Token);
            var direct = ReadStatusDirect(real);
            Write($"final state: preset=\"{fs?.SelectedPreset}\" output=\"{fs?.SelectedOutput}\" power={fs?.Power} leveling={fs?.VolumeLeveling} " +
                  $"anyModified={fs?.ModifiedByPreset.Values.Any(m => m)} userPresets={fs?.UserPresets.Count} (status.json direct: preset=\"{direct?.SelectedPreset}\")");
            Write($"final AutoSave entries: {files.AutoSaveNames().Count}; FxSound main running={real.IsMainRunning()}; pending launched={real.HasPendingLaunched}");
        }

        Write("SUMMARY");
        foreach (var (c, v, d) in Results) Write($"  {v,-7} {c} — {d}");
        _w.Dispose();
        return Results.Any(r => r.Verdict == "FAIL") ? 1 : 0;
    }

    // 1. Apply + verify each G435 preset (engine path: --power=1 --preset --volume_leveling, verified via status.json)
    private static async Task Case1Apply16(FxSoundController ctrl, FxSoundPort real, FxStatus st0, Func<string, double> lev, CancellationToken ct)
    {
        Write("--- case 1: apply + verify each G435 preset");
        var names = st0.UserPresets.Where(p => p.StartsWith("G435_", StringComparison.Ordinal)).ToList();
        int ok = 0;
        var bad = new List<string>();
        foreach (var name in names)
        {
            var r = await ctrl.ApplyAsync(name, lev(name), ct);
            var direct = ReadStatusDirect(real);
            bool good = r.Ok && direct is not null && direct.SelectedPreset == name && !Modified(direct, name)
                        && direct.VolumeLeveling is { } l && Math.Abs(l - lev(name)) <= 0.05;
            Write($"apply \"{name}\" leveling={lev(name).ToString(CultureInfo.InvariantCulture)}: ok={r.Ok} attempts={r.Attempts} ms={r.Ms} " +
                  $"status.json preset=\"{direct?.SelectedPreset}\" leveling={direct?.VolumeLeveling} modified={(direct is null ? "?" : Modified(direct, name).ToString())} → {(good ? "PASS" : "FAIL")}");
            if (good) ok++; else bad.Add(name);
        }
        Result("1 apply 16 presets", ok == 16 && names.Count == 16 ? "PASS" : "FAIL",
            $"{ok}/{names.Count} applied and verified against status.json{(bad.Count > 0 ? "; failed: " + string.Join(", ", bad) : "")}");
    }

    // 2. --output round trip G435 → Realtek → G435 through PresetOperations.SwitchOutput (always ends on G435)
    private static async Task Case2Output(FxSoundController ctrl, PresetOperations ops, FxSoundPort real, string g435, string realtek, CancellationToken ct)
    {
        Write("--- case 2: --output round trip G435 → Realtek → G435");
        PresetOpResult? toR = null, toG = null;
        FxStatus? onR = null;
        try
        {
            toR = await ops.RunAsync(new PresetOpRequest(PresetOpKind.SwitchOutput, OutputName: realtek), ct);
            onR = ReadStatusDirect(real);
            Write($"→ Realtek: ok={toR.Ok} error={toR.Error} reapply={toR.ReapplyRequested} steps=[{string.Join(" | ", toR.Steps)}] status.json output is Realtek={onR?.SelectedOutput == realtek} preset on Realtek=\"{onR?.SelectedPreset}\"");
        }
        finally
        {
            toG = await ops.RunAsync(new PresetOpRequest(PresetOpKind.SwitchOutput, OutputName: g435), ct);
            var onG = ReadStatusDirect(real);
            Write($"→ G435: ok={toG.Ok} error={toG.Error} reapply={toG.ReapplyRequested} steps=[{string.Join(" | ", toG.Steps)}] status.json output is G435={onG?.SelectedOutput == g435} preset=\"{onG?.SelectedPreset}\"");
            bool pass = toR is { Ok: true } && onR?.SelectedOutput == realtek && toG.Ok && onG?.SelectedOutput == g435
                        && toR.ReapplyRequested && toG.ReapplyRequested;
            Result("2 output round trip", pass ? "PASS" : "FAIL",
                $"to Realtek ok={toR?.Ok}, back to G435 ok={toG.Ok}, both verified in status.json, re-apply requested both ways={toR?.ReapplyRequested == true && toG.ReapplyRequested}");
        }
    }

    // 3 + 4. Duplicate → rename (concurrent with an engine apply) → delete (with an engine apply in flight); no residue
    private static async Task Case3And4(FxSoundController ctrl, PresetOperations ops, PresetFileStore files, FxSoundPort real,
        Func<string, double> lev, IReadOnlyList<string> autoSave0, Dictionary<string, string> presetsHash0, CancellationToken ct)
    {
        Write("--- case 3: duplicate + rename + delete of a Test preset; case 4: exclusive session vs concurrent engine apply");
        const string source = "G435_Musica";
        bool dupOk = false, renOk = false, delOk = false, c4a = false, c4b = false;
        string c4aDetail = "", c4bDetail = "";
        try
        {
            var st = await ctrl.QueryAsync(ct);
            if (st is null || st.UserPresets.Contains(TestName) || st.UserPresets.Contains(TestRenamed))
            {
                Result("3 duplicate/rename/delete", "FAIL", "precondition: status unavailable or a Test preset already exists");
                return;
            }

            var dup = await ops.RunAsync(new PresetOpRequest(PresetOpKind.Duplicate, Source: source, Target: TestName), ct);
            var d = ReadStatusDirect(real);
            dupOk = dup.Ok && d is not null && d.UserPresets.Contains(TestName) && files.FindPresetFile(TestName) is not null
                    && !files.AutoSaveExists(source) && !files.AutoSaveExists(TestName) && !Modified(d, source) && !Modified(d, TestName);
            Write($"duplicate \"{source}\" → \"{TestName}\": ok={dup.Ok} error={dup.Error} steps={dup.Steps.Count} reapply={dup.ReapplyRequested} verified={dupOk}");
            foreach (var s in dup.Steps) Write($"    step: {s}");
            if (!dup.Ok) return;

            // 4a. rename (exclusive) and an engine apply requested at the same moment
            lock (Lk) Trace.Clear();
            var tOp = ops.RunAsync(new PresetOpRequest(PresetOpKind.Rename, Source: TestName, Target: TestRenamed), ct);
            var tAp = ctrl.ApplyAsync("G435_FPS", lev("G435_FPS"), ct);
            await Task.WhenAll(tOp, tAp);
            var ren = tOp.Result;
            var ap = tAp.Result;
            var r = ReadStatusDirect(real);
            renOk = ren.Ok && r is not null && r.UserPresets.Contains(TestRenamed) && !r.UserPresets.Contains(TestName)
                    && files.FindPresetFile(TestRenamed) is not null && files.FindPresetFile(TestName) is null;
            Write($"rename \"{TestName}\" → \"{TestRenamed}\": ok={ren.Ok} error={ren.Error} steps={ren.Steps.Count} verified={renOk}");
            foreach (var s in ren.Steps) Write($"    step: {s}");
            var (interleaved, order) = Check(Snapshot());
            c4a = renOk && ap.Ok && !interleaved && r?.SelectedPreset == "G435_FPS" && !Modified(r, "G435_FPS");
            c4aDetail = $"rename+apply requested together: order [{order}], interleaved={interleaved}, apply ok={ap.Ok} (\"{ap.Actual}\"), final preset=\"{r?.SelectedPreset}\"";
            Write("case 4a: " + c4aDetail);

            // 4b. engine apply already in flight, then delete (exclusive must wait for it)
            lock (Lk) Trace.Clear();
            var tAp2 = ctrl.ApplyAsync("G435_Cine", lev("G435_Cine"), ct);
            await Task.Delay(120, ct);
            var tDel = ops.RunAsync(new PresetOpRequest(PresetOpKind.Delete, Source: TestRenamed), ct);
            await Task.WhenAll(tAp2, tDel);
            var del = tDel.Result;
            var ap2 = tAp2.Result;
            var x = ReadStatusDirect(real);
            delOk = del.Ok && x is not null && !x.UserPresets.Contains(TestRenamed);
            Write($"delete \"{TestRenamed}\": ok={del.Ok} error={del.Error} steps={del.Steps.Count} reapply={del.ReapplyRequested} selected after delete=\"{x?.SelectedPreset}\"");
            foreach (var s in del.Steps) Write($"    step: {s}");
            var (interleaved2, order2) = Check(Snapshot());
            c4b = delOk && ap2.Ok && !interleaved2 && del.ReapplyRequested && x is not null
                  && x.SelectedPreset == del.Final?.SelectedPreset && !(x.SelectedPreset is { } sp && Modified(x, sp));
            c4bDetail = $"apply in flight then delete: order [{order2}], interleaved={interleaved2}, apply ok={ap2.Ok}, delete ok={del.Ok}, " +
                        $"final preset=\"{x?.SelectedPreset}\" equals op result={x?.SelectedPreset == del.Final?.SelectedPreset}, re-apply requested={del.ReapplyRequested}";
            Write("case 4b: " + c4bDetail);
        }
        finally
        {
            // never leave a Test preset behind
            var st = await ctrl.QueryAsync(ct);
            foreach (var n in new[] { TestName, TestRenamed })
            {
                if (st is not null && st.UserPresets.Contains(n))
                {
                    var cl = await ops.RunAsync(new PresetOpRequest(PresetOpKind.Delete, Source: n), ct);
                    Write($"cleanup delete \"{n}\": ok={cl.Ok} error={cl.Error}");
                }
            }
            var fin = await ctrl.QueryAsync(ct);
            var autoNow = files.AutoSaveNames();
            var hashNow = HashFolder(files.PresetsFolder);
            bool noStatus = fin is not null && !fin.UserPresets.Any(p => p.StartsWith("Test - ", StringComparison.Ordinal));
            bool noFile = files.FindPresetFile(TestName) is null && files.FindPresetFile(TestRenamed) is null;
            bool noAuto = !autoNow.Any(a => a.StartsWith("Test - ", StringComparison.Ordinal)) && autoNow.Count == autoSave0.Count;
            var changed = presetsHash0.Where(kv => !hashNow.TryGetValue(kv.Key, out var h) || h != kv.Value).Select(kv => kv.Key).ToList();
            var extra = hashNow.Keys.Except(presetsHash0.Keys, StringComparer.OrdinalIgnoreCase).ToList();
            Write($"residue: status.json Test presets={(noStatus ? "none" : "PRESENT")}, Presets\\ Test files={(noFile ? "none" : "PRESENT")}, " +
                  $"AutoSave entries={autoNow.Count} (start {autoSave0.Count}), Presets\\ files={hashNow.Count} (start {presetsHash0.Count}), " +
                  $"changed files=[{string.Join(", ", changed)}], extra files=[{string.Join(", ", extra)}]");
            bool clean = noStatus && noFile && noAuto && extra.Count == 0 && changed.Count == 0;
            Result("3 duplicate/rename/delete", dupOk && renOk && delOk && clean ? "PASS" : "FAIL",
                $"duplicate={dupOk}, rename={renOk}, delete={delOk}, no residue in Presets\\/AutoSave\\/status.json={clean}");
            if (c4aDetail.Length > 0 || c4bDetail.Length > 0)
                Result("4 exclusive vs concurrent apply", c4a && c4b ? "PASS" : "FAIL", $"4a {(c4a ? "ok" : "KO")}: {c4aDetail}; 4b {(c4b ? "ok" : "KO")}: {c4bDetail}");
            else
                Result("4 exclusive vs concurrent apply", "FAIL", "not reached (duplicate failed)");
        }
    }

    private static List<string> Snapshot()
    {
        lock (Lk) return [.. Trace];
    }

    /// <summary>An engine apply (--power=1 …) launched between exclusive-begin and exclusive-end = interleaved.</summary>
    private static (bool Interleaved, string Order) Check(List<string> trace)
    {
        bool inside = false, interleaved = false;
        var order = new List<string>();
        foreach (var t in trace)
        {
            if (t == "exclusive-begin") { inside = true; order.Add("session-begin"); }
            else if (t == "exclusive-end") { inside = false; order.Add("session-end"); }
            else if (t.StartsWith("send-begin --power=1", StringComparison.Ordinal))
            {
                order.Add("engine-apply");
                if (inside) interleaved = true;
            }
        }
        // collapse repeats for readability
        var compact = order.Where((o, i) => i == 0 || order[i - 1] != o);
        return (interleaved, string.Join(" → ", compact));
    }

    // 5. FxSound closed / elevated: the controller and the operations refuse to send (simulated port, real FxSound untouched)
    private static async Task Case5ClosedElevated(FxSoundPort real, PresetFileStore files, IClock clock, string g435, string realtek, CancellationToken ct)
    {
        Write("--- case 5: FxSound closed / elevated → nothing sent (simulated through a wrapper port; FxSound is not touched)");
        foreach (var (label, closed, elevated, reason) in new[]
                 {
                     ("closed", true, false, FxSoundController.ReasonClosed),
                     ("elevated", false, true, FxSoundController.ReasonElevated),
                 })
        {
            var sim = new SimulatedPort(real, closed, elevated);
            var c = new FxSoundController(sim, clock, Log);
            var o = new PresetOperations(c, files, clock, Log);
            var apply = await c.ApplyAsync("G435_General", 0, ct);
            var ensure = await c.EnsureAsync("G435_General", 0, ct);
            var query = await c.QueryAsync(ct);
            var sw = await o.RunAsync(new PresetOpRequest(PresetOpKind.SwitchOutput, OutputName: realtek), ct);
            var dup = await o.RunAsync(new PresetOpRequest(PresetOpKind.Duplicate, Source: "G435_General", Target: "Test - Never"), ct);
            bool pass = !apply.Ok && apply.FailureReason == reason && !ensure.Ok && ensure.FailureReason == reason && query is null
                        && !sw.Ok && !dup.Ok && sim.Sends == 0 && sim.Starts == 0;
            Write($"{label}: apply reason=\"{apply.FailureReason}\", ensure reason=\"{ensure.FailureReason}\", query null={query is null}, " +
                  $"switch output refused=\"{sw.Error}\", duplicate refused=\"{dup.Error}\", commands launched={sim.Sends}, starts={sim.Starts}");
            Result($"5 FxSound {label}", pass ? "PASS" : "FAIL",
                $"controller + operations send nothing ({sim.Sends} launches, {sim.Starts} starts) and report \"{reason}\" (simulated via wrapper port)");
        }
        Write($"real port detection on the live FxSound: IsMainRunning={real.IsMainRunning()}, IsElevated={real.IsElevated()?.ToString() ?? "unknown"} " +
              "(port detection with FxSound really closed / really elevated is left for a manual test)");
        _ = g435;
    }
}
