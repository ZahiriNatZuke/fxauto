using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FxAuto.Core;
using FxAuto.Core.Config;
using FxAuto.Core.Devices;
using FxAuto.Core.FxSound;
using FxAuto.Core.Presets;

namespace FxAuto.Tests;

/// <summary>FxSound simulator for preset operations: built-in and user presets, modified flags, AutoSave (through
/// <see cref="FakePresetFileStore"/>), DSP values, outputs, and fault injection. Behaviour per research 03 §3.1 and
/// spike 04 (c).</summary>
internal sealed class FxSoundSim : IFxSoundPort
{
    public static readonly string[] BuiltInNames =
    [
        "General", "Movies", "TV", "Transcription", "Music", "Voice", "Volume Boost", "Gaming", "Classic Processing",
        "Light Processing", "Bass Boost", "Streaming Video", "Default",
    ];

    private sealed class Dsp
    {
        public double Clarity { get; set; } = 1.023622035980225;
        public double Master { get; set; } = -2.0;
        public double[] Bands { get; set; } = [1, -1, -1, 0, 0, 0, 0, 0, 0, 0];
        public Dsp Copy() => new() { Clarity = Clarity, Master = Master, Bands = (double[])Bands.Clone() };
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Dsp> _saved = new(StringComparer.Ordinal);
    private Dsp _current = new();
    private string? _json;
    private DateTimeOffset? _written;

    public FakePresetFileStore Store { get; } = new();
    public List<string> BuiltIn { get; } = [.. BuiltInNames];
    public List<string> User { get; } = [];
    public HashSet<string> Modified { get; } = new(StringComparer.Ordinal);
    public string Selected { get; set; } = "General";
    public bool Power { get; set; } = true;
    public bool MainRunning { get; set; } = true;
    public List<string> OutputDevices { get; } = ["Headset (G435 Wireless Gaming Headset)", "Speakers (Realtek(R) Audio)"];
    public string SelectedOutput { get; set; } = "Headset (G435 Wireless Gaming Headset)";
    public ConcurrentQueue<string> Sent { get; } = new();

    /// <summary>Commands for which FxSound exits 0 but does nothing.</summary>
    public Func<string, bool>? Ignore { get; set; }
    /// <summary>Commands that never return (SendAsync → null, nothing done).</summary>
    public Func<string, bool>? Hang { get; set; }
    /// <summary>--set_effect also makes FxSound auto-save the modified preset (the 60 s timer firing).</summary>
    public bool AutoSaveOnSetEffect { get; set; }
    /// <summary>The preset saved by --save_preset gets slightly different bands (a copy that would not sound the same).</summary>
    public bool CorruptSave { get; set; }

    public FxSoundSim(params string[] userPresets)
    {
        foreach (var b in BuiltIn)
        {
            _saved[b] = new Dsp();
            Store.Add(b, builtIn: true);
        }
        foreach (var u in userPresets) AddUser(u);
        _current = _saved[Selected].Copy();
    }

    public void AddUser(string name, double band0 = 2)
    {
        User.Add(name);
        var d = new Dsp();
        d.Bands[0] = band0;
        _saved[name] = d;
        Store.Add(name);
    }

    /// <summary>Commands other than --status, in order.</summary>
    public List<string> Commands => Sent.Where(a => a != FxCommand.Status).ToList();

    public bool IsMainRunning() => MainRunning;
    public bool? IsElevated() => false;
    public bool HasPendingLaunched => false;
    public Task<bool> StartMainMinimizedAsync(CancellationToken ct) => Task.FromResult(true);

    public (string? Json, DateTimeOffset? WrittenAt) ReadStatusFile()
    {
        lock (_gate) return (_json, _written);
    }

    public async Task<int?> SendAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        Sent.Enqueue(arguments);
        await Task.Delay(1, ct).ConfigureAwait(false);
        if (Hang?.Invoke(arguments) == true) return null;
        if (arguments == FxCommand.Status)
        {
            WriteStatus();
            return 0;
        }
        if (Ignore?.Invoke(arguments) == true) return 0;
        lock (_gate) Execute(arguments);
        return 0;
    }

    private bool Exists(string n) => BuiltIn.Contains(n, StringComparer.Ordinal) || User.Contains(n, StringComparer.Ordinal);
    private bool ExistsCi(string n) => BuiltIn.Concat(User).Any(p => string.Equals(p, n, StringComparison.OrdinalIgnoreCase));

    /// <summary>FxSound saves another output name than the one requested (null = the requested one).</summary>
    public Func<string, string?>? OutputOverride { get; set; }

    private void Execute(string args)
    {
        foreach (System.Text.RegularExpressions.Match m in OptionRx.Matches(args))
        {
            var v = m.Groups[2].Success ? m.Groups[2].Value : null;
            if (v is { Length: >= 2 } && v[0] == '"' && v[^1] == '"') v = v[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
            ExecuteOne(m.Groups[1].Value, v);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex OptionRx =
        new(@"--(\w+)(?:=(""(?:[^""\\]|\\.)*""|\S+))?", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private void ExecuteOne(string opt, string? value)
    {
        switch (opt)
        {
            case "power":
                Power = value == "1";
                break;
            case "preset":
                if (value is null || !Exists(value)) return;
                if (Modified.Contains(Selected)) Store.AutoSaved.Add(Selected); // leaving a modified preset auto-saves it
                Selected = value;
                _current = _saved[value].Copy();
                break;
            case "set_effect":
                var parts = value!.Split(':');
                _current.Clarity = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                Modified.Add(Selected);
                if (AutoSaveOnSetEffect) Store.AutoSaved.Add(Selected);
                break;
            case "save_preset":
                if (!Modified.Contains(Selected) || value is null || ExistsCi(value) || User.Count >= Store.MaxPresets) return;
                User.Add(value);
                var copy = _current.Copy();
                if (CorruptSave) copy.Bands[3] += 1;
                _saved[value] = copy;
                Modified.Remove(Selected);
                Store.AutoSaved.Remove(Selected);
                Store.Add(value);
                Selected = value;
                _current = copy.Copy();
                break;
            case "rename_preset":
                if (value is null || !User.Contains(Selected) || ExistsCi(value)) return;
                User[User.IndexOf(Selected)] = value;
                _saved[value] = _saved[Selected];
                _saved.Remove(Selected);
                Selected = value;
                break;
            case "delete_preset":
                if (!User.Contains(Selected)) return;
                User.Remove(Selected);
                Modified.Remove(Selected);
                Selected = "General";
                _current = _saved["General"].Copy();
                break;
            case "undo_preset":
                Modified.Remove(Selected);
                Store.AutoSaved.Remove(Selected);
                _current = _saved[Selected].Copy();
                break;
            case "overwrite_preset":
                if (!User.Contains(Selected)) return;
                _saved[Selected] = _current.Copy();
                Modified.Remove(Selected);
                Store.AutoSaved.Remove(Selected);
                break;
            case "output":
                // FxSound saves the name even if no endpoint has it (the --output trap)
                SelectedOutput = OutputOverride?.Invoke(value!) ?? value!;
                break;
            case "edit":
                _current.Bands[5] += 1;
                Modified.Add(Selected);
                break;
        }
    }

    /// <summary>The user changes the sound in FxSound's window.</summary>
    public void UserEdits()
    {
        lock (_gate) Execute("--edit");
    }

    private void WriteStatus()
    {
        lock (_gate)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("version", "1.2.16.0");
                w.WriteBoolean("power", Power);
                w.WriteStartObject("presets");
                foreach (var (prop, list) in new[] { ("built_in", BuiltIn), ("user_defined", User) })
                {
                    w.WriteStartArray(prop);
                    foreach (var p in list)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", p);
                        w.WriteBoolean("modified", Modified.Contains(p));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                w.WriteString("selected_preset", Selected);
                w.WriteStartArray("output_devices");
                foreach (var o in OutputDevices) w.WriteStringValue(o);
                w.WriteEndArray();
                w.WriteString("selected_output", SelectedOutput);
                w.WriteStartObject("equalizer");
                w.WriteNumber("num_bands", _current.Bands.Length);
                w.WriteNumber("master_gain", _current.Master);
                w.WriteNumber("volume_leveling", 0);
                w.WriteStartArray("bands");
                for (var i = 0; i < _current.Bands.Length; i++)
                {
                    w.WriteStartObject();
                    w.WriteNumber("index", i);
                    w.WriteNumber("frequency", 62.5 * (1 << i));
                    w.WriteNumber("gain", _current.Bands[i]);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteStartObject("effects");
                w.WriteNumber("clarity", _current.Clarity);
                w.WriteNumber("ambience", 0);
                w.WriteNumber("surround", 0);
                w.WriteNumber("dynamicboost", 0);
                w.WriteNumber("bass", 0);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            var now = DateTimeOffset.Now;
            if (_written is DateTimeOffset prev && now <= prev) now = prev.AddTicks(1);
            _json = Encoding.UTF8.GetString(ms.ToArray());
            _written = now;
        }
    }
}

internal static class PresetOpsTests
{
    private static readonly FxControllerTimings Fast = new()
    {
        PerCommand = TimeSpan.FromSeconds(1),
        Total = TimeSpan.FromSeconds(5),
        StatusPoll = TimeSpan.FromMilliseconds(5),
        StatusWait = TimeSpan.FromMilliseconds(300),
        Retry1 = TimeSpan.FromMilliseconds(30),
        Retry2 = TimeSpan.FromMilliseconds(60),
        PendingPoll = TimeSpan.FromMilliseconds(20),
    };

    private sealed record Ctx(FxSoundSim Sim, FxSoundController Ctl, PresetOperations Ops, ConcurrentQueue<string> Log);

    private static Ctx Make(Func<FxAutoConfig?>? cfg = null, params string[] users)
    {
        var sim = new FxSoundSim(users.Length == 0 ? ["G435 - Music", "G435 - Video"] : users);
        var log = new ConcurrentQueue<string>();
        var ctl = new FxSoundController(sim, SystemClock.Instance, (ev, _) => log.Enqueue(ev), Fast);
        var ops = new PresetOperations(ctl, sim.Store, SystemClock.Instance, (ev, _) => log.Enqueue(ev))
        {
            CurrentConfig = cfg,
            OutputSettle = TimeSpan.Zero,
        };
        return new Ctx(sim, ctl, ops, log);
    }

    private static Task<PresetOpResult> Run(Ctx c, PresetOpRequest r) => c.Ops.RunAsync(r, CancellationToken.None);

    private static void Clean(Ctx c, params string[] presets)
    {
        foreach (var p in presets)
        {
            Assert.True(!c.Sim.Modified.Contains(p), $"\"{p}\" not modified");
            Assert.True(!c.Sim.Store.AutoSaved.Contains(p), $"no AutoSave of \"{p}\"");
        }
        Assert.True(c.Sim.Modified.Count == 0, "no preset modified at all: " + string.Join(", ", c.Sim.Modified));
    }

    private static string Q(string s) => FxCommand.Quote(s);

    internal static FxAutoConfig Config(params DeviceConfig[] devices) => new(2,
        new GeneralSettings(null, false, false, "info", false, 10, 5, new FxAuto.Core.Audio.SoundingTimings(), 60, 5000, 60000,
            FxAuto.Core.Arbitration.CallPolicy.Ignore, "keepCurrent", true, ["Audio playing"]),
        new Dictionary<string, PresetMeta>
        {
            ["G435 - Music"] = new(1.5, null),
            ["G435 - Video"] = new(2, null),
        },
        devices, [], [new Assignment("g435", "music", null, "G435 - Music")], [], [], []);

    internal static DeviceConfig Device(string id, string tag, string defaultPreset, params string[] adapters)
        => new(id, tag, tag, new DeviceMatchSpec(adapters, null, null, [], null), defaultPreset, true, null);

    public static void Register()
    {
        Program.Add("preset ops: duplicate happy path — exact step list, exact copy, nothing left modified", async () =>
        {
            var c = Make();
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music"));
            Assert.True(r.Ok, "ok: " + r.Error);
            string[] expected =
            [
                $"--preset={Q("G435 - Music")} --run_minimized",
                "--set_effect=clarity:1.023622 --run_minimized",
                $"--save_preset={Q("Realtek - Music")} --run_minimized",
                $"--preset={Q("Realtek - Music")} --run_minimized",
            ];
            Assert.Equal(string.Join(" | ", expected), string.Join(" | ", c.Sim.Commands), "commands");
            Assert.True(c.Sim.User.Contains("Realtek - Music"), "copy listed");
            Assert.Equal("Realtek - Music", c.Sim.Selected, "copy selected");
            Assert.True(c.Sim.Store.FindPresetFile("Realtek - Music") is not null, "copy file written (line 3)");
            Assert.Equal(1, r.Done.Count, "done");
            Assert.Equal(("G435 - Music", "Realtek - Music"), r.Done[0], "done pair");
            Assert.True(r.ReapplyRequested, "re-apply requested (active preset changed)");
            Clean(c, "G435 - Music", "Realtek - Music");
        });

        Program.Add("preset ops: duplicate of a built-in (file in Factsoft) works", async () =>
        {
            var c = Make();
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Duplicate, "Music", "G435 - Podcast"));
            Assert.True(r.Ok, "ok: " + r.Error);
            Assert.True(c.Sim.User.Contains("G435 - Podcast"), "copy listed");
            Clean(c, "Music", "G435 - Podcast");
        });

        Program.Add("preset ops: duplicate with an invalid name sends nothing but --status", async () =>
        {
            foreach (var bad in new[] { "G435: Music", "music", "General", "G435 - Música", new string('a', 65), "G435 - Music " })
            {
                var c = Make();
                var r = await Run(c, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Video", bad));
                Assert.True(!r.Ok, "refused: " + bad);
                Assert.Equal(0, c.Sim.Commands.Count, "no command for " + bad);
                Assert.True(!r.ReapplyRequested, "nothing to re-apply");
            }
        });

        Program.Add("preset ops: duplicate refused at the user preset limit", async () =>
        {
            var c = Make();
            c.Sim.Store.MaxPresets = 2;
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music"));
            Assert.True(!r.Ok && r.Error!.Contains("at most 2", StringComparison.Ordinal), "limit: " + r.Error);
            Assert.Equal(0, c.Sim.Commands.Count, "no command");
        });

        Program.Add("preset ops: duplicate refused when the source or the active preset has unsaved changes", async () =>
        {
            var c = Make();
            c.Sim.Modified.Add("G435 - Music");
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music"));
            Assert.True(!r.Ok, "source modified refused");
            Assert.Equal(0, c.Sim.Commands.Count, "no command");

            var c2 = Make();
            c2.Sim.Modified.Add("General"); // active preset
            var r2 = await Run(c2, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music"));
            Assert.True(!r2.Ok && r2.Error!.Contains("General", StringComparison.Ordinal), "active modified refused: " + r2.Error);
            Assert.Equal(0, c2.Sim.Commands.Count, "no command");
        });

        Program.Add("preset ops: duplicate refused with FxSound processing off or closed", async () =>
        {
            var c = Make();
            c.Sim.Power = false;
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music"));
            Assert.True(!r.Ok && r.Error!.Contains("off", StringComparison.Ordinal), "power off: " + r.Error);
            Assert.Equal(0, c.Sim.Commands.Count, "no command");

            var c2 = Make();
            c2.Sim.MainRunning = false;
            var r2 = await Run(c2, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music"));
            Assert.True(!r2.Ok, "closed refused");
            Assert.Equal(0, c2.Sim.Sent.Count, "nothing at all sent to a closed FxSound");
        });

        Program.Add("preset ops: duplicate — --set_effect does not take → recovery --preset=X + --undo_preset", async () =>
        {
            var c = Make();
            c.Sim.Ignore = a => a.StartsWith("--set_effect", StringComparison.Ordinal);
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music"));
            Assert.True(!r.Ok, "fails");
            var cmds = c.Sim.Commands;
            Assert.Equal($"--preset={Q("G435 - Music")} --run_minimized", cmds[^2], "recovery selects X");
            Assert.Equal("--undo_preset --run_minimized", cmds[^1], "recovery undoes");
            Assert.True(!c.Sim.User.Contains("Realtek - Music"), "no copy");
            Clean(c, "G435 - Music");
        });

        Program.Add("preset ops: duplicate — --save_preset does not take → exact recovery sequence, X clean", async () =>
        {
            var c = Make();
            c.Sim.Ignore = a => a.StartsWith("--save_preset", StringComparison.Ordinal);
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music"));
            Assert.True(!r.Ok, "fails");
            string[] expected =
            [
                $"--preset={Q("G435 - Music")} --run_minimized",
                "--set_effect=clarity:1.023622 --run_minimized",
                $"--save_preset={Q("Realtek - Music")} --run_minimized",
                $"--preset={Q("G435 - Music")} --run_minimized",
                "--undo_preset --run_minimized",
            ];
            Assert.Equal(string.Join(" | ", expected), string.Join(" | ", c.Sim.Commands), "commands");
            Assert.True(r.Steps.Any(s => s.StartsWith("recovery", StringComparison.Ordinal)), "recovery in steps");
            Assert.True(c.Log.Contains("preset-op-recovery"), "recovery logged");
            Clean(c, "G435 - Music");
        });

        Program.Add("preset ops: duplicate — AutoSave already born before the save → recovery clears it", async () =>
        {
            var c = Make();
            c.Sim.AutoSaveOnSetEffect = true;
            c.Sim.Hang = a => a.StartsWith("--save_preset", StringComparison.Ordinal);
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music"));
            Assert.True(!r.Ok, "fails");
            Assert.Equal("--undo_preset --run_minimized", c.Sim.Commands[^1], "undo last");
            Clean(c, "G435 - Music");
        });

        Program.Add("preset ops: duplicate — copy file cannot be written → the CLI copy is deleted, X clean", async () =>
        {
            var c = Make();
            c.Sim.Store.FailNextWrite = "disk full";
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music"));
            Assert.True(!r.Ok && r.Error!.Contains("disk full", StringComparison.Ordinal), "error: " + r.Error);
            Assert.True(!c.Sim.User.Contains("Realtek - Music"), "no residue");
            Assert.True(c.Sim.Commands.Contains("--delete_preset --run_minimized"), "cleanup delete sent");
            Assert.True(r.ReapplyRequested, "re-apply requested");
            Clean(c, "G435 - Music");
        });

        Program.Add("preset ops: duplicate — copy does not sound like the source → deleted, failure", async () =>
        {
            var c = Make();
            c.Sim.CorruptSave = true;
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music"));
            Assert.True(!r.Ok && r.Error!.Contains("band 3", StringComparison.Ordinal), "error: " + r.Error);
            Assert.True(!c.Sim.User.Contains("Realtek - Music"), "no residue");
            Clean(c, "G435 - Music");
        });

        Program.Add("preset ops: rename happy path and refusals (built-in, case only)", async () =>
        {
            var c = Make();
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Rename, "G435 - Video", "G435 - Movies"));
            Assert.True(r.Ok, "ok: " + r.Error);
            Assert.True(c.Sim.User.Contains("G435 - Movies") && !c.Sim.User.Contains("G435 - Video"), "renamed");
            Assert.Equal(("G435 - Video", "G435 - Movies"), r.Done[0], "done");
            Clean(c);

            var c2 = Make();
            var r2 = await Run(c2, new PresetOpRequest(PresetOpKind.Rename, "Music", "My Music"));
            Assert.True(!r2.Ok && r2.Error!.Contains("built-in", StringComparison.Ordinal), "built-in refused");
            var r3 = await Run(c2, new PresetOpRequest(PresetOpKind.Rename, "G435 - Video", "g435 - video"));
            Assert.True(!r3.Ok && r3.Error!.Contains("letter case", StringComparison.Ordinal), "case only refused: " + r3.Error);
            Assert.Equal(0, c2.Sim.Commands.Count, "no command");
        });

        Program.Add("preset ops: rename does not take → failure, nothing modified", async () =>
        {
            var c = Make();
            c.Sim.Ignore = a => a.StartsWith("--rename_preset", StringComparison.Ordinal);
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Rename, "G435 - Video", "G435 - Movies"));
            Assert.True(!r.Ok, "fails");
            Assert.True(r.Done.Count == 0, "nothing done");
            Clean(c);
        });

        Program.Add("preset ops: delete → FxSound selects General and the result requests a re-apply", async () =>
        {
            var c = Make();
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Delete, "G435 - Video"));
            Assert.True(r.Ok, "ok: " + r.Error);
            Assert.True(!c.Sim.User.Contains("G435 - Video"), "deleted");
            Assert.Equal("General", r.Final?.SelectedPreset, "FxSound selected General");
            Assert.True(r.ReapplyRequested, "re-apply requested");
            Assert.Equal(("G435 - Video", ""), r.Done[0], "done without replacement");
            string[] expected = [$"--preset={Q("G435 - Video")} --run_minimized", "--delete_preset --run_minimized"];
            Assert.Equal(string.Join(" | ", expected), string.Join(" | ", c.Sim.Commands), "commands");
        });

        Program.Add("preset ops: delete of a device default without replacement is refused before any command", async () =>
        {
            var cfg = Config(Device("g435", "G435", "G435 - Video", "G435 Wireless Gaming Headset"));
            var c = Make(() => cfg);
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Delete, "G435 - Video"));
            Assert.True(!r.Ok && r.Error!.Contains("default preset", StringComparison.Ordinal), "refused: " + r.Error);
            Assert.Equal(0, c.Sim.Sent.Count, "nothing sent");
            var r2 = await Run(c, new PresetOpRequest(PresetOpKind.Delete, "G435 - Video", Target: "G435 - Music"));
            Assert.True(r2.Ok, "with replacement ok: " + r2.Error);
            Assert.Equal(("G435 - Video", "G435 - Music"), r2.Done[0], "done with replacement");
            Assert.Null(PresetOperations.CheckDelete(cfg, "G435 - Music", null), "not a default → allowed");
            Assert.True(PresetOperations.CheckDelete(cfg, "G435 - Video", "G435 - Video") is not null, "self replacement refused");
        });

        Program.Add("preset ops: delete of a built-in or a missing preset is refused", async () =>
        {
            var c = Make();
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Delete, "General"));
            Assert.True(!r.Ok, "built-in refused");
            var r2 = await Run(c, new PresetOpRequest(PresetOpKind.Delete, "Nope"));
            Assert.True(!r2.Ok, "missing refused");
            Assert.Equal(0, c.Sim.Commands.Count, "no command");
        });

        Program.Add("preset ops: only config-changing ops are refused while the config can't be saved", () =>
        {
            Assert.True(PresetOperations.ChangesConfig(new PresetOpRequest(PresetOpKind.Duplicate, "a", "b")), "duplicate");
            Assert.True(PresetOperations.ChangesConfig(new PresetOpRequest(PresetOpKind.Rename, "a", "b")), "rename");
            Assert.True(PresetOperations.ChangesConfig(new PresetOpRequest(PresetOpKind.Delete, "a")), "delete");
            Assert.True(PresetOperations.ChangesConfig(new PresetOpRequest(PresetOpKind.EditFinish, "a", "b", Outcome: EditOutcome.SaveAsNew)), "save as new");
            Assert.True(!PresetOperations.ChangesConfig(new PresetOpRequest(PresetOpKind.EditFinish, "a", Outcome: EditOutcome.Save)), "save in place");
            Assert.True(!PresetOperations.ChangesConfig(new PresetOpRequest(PresetOpKind.EditFinish, "a", Outcome: EditOutcome.Discard)), "discard");
            Assert.True(!PresetOperations.ChangesConfig(new PresetOpRequest(PresetOpKind.EditBegin, "a")), "edit begin");
            Assert.True(!PresetOperations.ChangesConfig(new PresetOpRequest(PresetOpKind.SwitchOutput, OutputName: "x")), "switch output");
        });

        Program.Add("preset ops: config side — duplicate copies leveling; rename/delete go through ConfigEdits", () =>
        {
            var cfg = Config(Device("g435", "G435", "G435 - Video", "G435 Wireless Gaming Headset"));
            var dupReq = new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music");
            var dupRes = new PresetOpResult(true, null, [], null, [("G435 - Music", "Realtek - Music")]);
            var after = PresetOperations.ApplyToConfig(cfg, dupReq, dupRes);
            Assert.Equal(1.5, after.Presets["Realtek - Music"].VolumeLeveling, "leveling copied");
            Assert.True(ReferenceEquals(cfg, PresetOperations.ApplyToConfig(cfg, dupReq, dupRes with { Ok = false })), "failed op → unchanged");
            try
            {
                var ren = PresetOperations.ApplyToConfig(cfg, new PresetOpRequest(PresetOpKind.Rename, "G435 - Video", "G435 - Movies"),
                    new PresetOpResult(true, null, [], null, [("G435 - Video", "G435 - Movies")]));
                Assert.Equal("G435 - Movies", ren.Devices[0].DefaultPreset, "device default renamed");
                var del = PresetOperations.ApplyToConfig(cfg, new PresetOpRequest(PresetOpKind.Delete, "G435 - Music"),
                    new PresetOpResult(true, null, [], null, [("G435 - Music", "G435 - Video")]));
                Assert.True(!del.Presets.ContainsKey("G435 - Music"), "removed from presets");
                Assert.True(del.Assignments.All(a => a.Preset != "G435 - Music"), "assignments moved");
            }
            catch (NotImplementedException)
            {
                throw new SkipException("ConfigEdits (task 3a) not implemented yet");
            }
        });

        Program.Add("preset ops: --output with a name absent from output_devices is never sent", async () =>
        {
            var c = Make();
            var r = await Run(c, new PresetOpRequest(PresetOpKind.SwitchOutput, OutputName: "Speakers (Realtek(R) Audio) "));
            Assert.True(!r.Ok, "refused");
            Assert.True(!c.Sim.Sent.Any(a => a.StartsWith("--output", StringComparison.Ordinal)), "no --output sent");
            var r2 = await Run(c, new PresetOpRequest(PresetOpKind.SwitchOutput, OutputName: "speakers (realtek(r) audio)"));
            Assert.True(!r2.Ok, "case differs: refused (FxSound compares exactly)");
            Assert.Equal(0, c.Sim.Commands.Count, "no command");
        });

        Program.Add("preset ops: --output switch sent alone, verified, re-apply requested", async () =>
        {
            var c = Make();
            var r = await Run(c, new PresetOpRequest(PresetOpKind.SwitchOutput, OutputName: "Speakers (Realtek(R) Audio)"));
            Assert.True(r.Ok, "ok: " + r.Error);
            Assert.Equal($"--output={Q("Speakers (Realtek(R) Audio)")} --run_minimized", string.Join(" | ", c.Sim.Commands), "alone");
            Assert.True(r.ReapplyRequested, "engine re-decides for the new device");
            var again = await Run(c, new PresetOpRequest(PresetOpKind.SwitchOutput, OutputName: "Speakers (Realtek(R) Audio)"));
            Assert.True(again.Ok && c.Sim.Commands.Count == 1, "already selected → nothing sent");
        });

        Program.Add("preset ops: --output does not take → failure; another name saved → previous output restored", async () =>
        {
            var c = Make();
            c.Sim.Ignore = a => a.StartsWith("--output", StringComparison.Ordinal);
            var r = await Run(c, new PresetOpRequest(PresetOpKind.SwitchOutput, OutputName: "Speakers (Realtek(R) Audio)"));
            Assert.True(!r.Ok, "fails");
            Assert.Equal(1, c.Sim.Commands.Count, "output unchanged: nothing to restore");

            var c2 = Make();
            var once = true;
            c2.Sim.OutputOverride = v =>
            {
                if (!once) return null;
                once = false;
                return "Bogus output";
            };
            var r2 = await Run(c2, new PresetOpRequest(PresetOpKind.SwitchOutput, OutputName: "Speakers (Realtek(R) Audio)"));
            Assert.True(!r2.Ok, "fails");
            Assert.Equal($"--output={Q("Headset (G435 Wireless Gaming Headset)")} --run_minimized", c2.Sim.Commands[^1], "previous restored");
            Assert.Equal("Headset (G435 Wireless Gaming Headset)", c2.Sim.SelectedOutput, "restored");
        });

        Program.Add("preset ops: edit begin shows FxSound (no --run_minimized); finish discard / save / save as new", async () =>
        {
            var c = Make();
            var b = await Run(c, new PresetOpRequest(PresetOpKind.EditBegin, "G435 - Music"));
            Assert.True(b.Ok, "begin ok: " + b.Error);
            Assert.Equal($"--preset={Q("G435 - Music")} --view=2", c.Sim.Commands[^1], "edit command");
            c.Sim.UserEdits();
            var d = await Run(c, new PresetOpRequest(PresetOpKind.EditFinish, "G435 - Music", Outcome: EditOutcome.Discard));
            Assert.True(d.Ok, "discard ok: " + d.Error);
            Assert.Equal("--undo_preset --run_minimized", c.Sim.Commands[^1], "undo");
            Clean(c);

            c.Sim.UserEdits();
            var s = await Run(c, new PresetOpRequest(PresetOpKind.EditFinish, "G435 - Music", Outcome: EditOutcome.Save));
            Assert.True(s.Ok, "save ok: " + s.Error);
            Assert.Equal("--overwrite_preset --run_minimized", c.Sim.Commands[^1], "overwrite");
            Clean(c);

            c.Sim.UserEdits();
            var n = await Run(c, new PresetOpRequest(PresetOpKind.EditFinish, "G435 - Music", Target: "G435 - Music Night", Outcome: EditOutcome.SaveAsNew));
            Assert.True(n.Ok, "save as new ok: " + n.Error);
            Assert.True(c.Sim.User.Contains("G435 - Music Night"), "new preset");
            Assert.Equal(("G435 - Music", "G435 - Music Night"), n.Done[0], "done");
            Clean(c);
        });

        Program.Add("preset ops: edit finish — save over a built-in refused, changes kept (nothing discarded)", async () =>
        {
            var c = Make();
            await Run(c, new PresetOpRequest(PresetOpKind.EditBegin, "Music"));
            c.Sim.UserEdits();
            var r = await Run(c, new PresetOpRequest(PresetOpKind.EditFinish, "Music", Outcome: EditOutcome.Save));
            Assert.True(!r.Ok && r.Error!.Contains("Built-in", StringComparison.Ordinal), "refused: " + r.Error);
            Assert.True(c.Sim.Modified.Contains("Music"), "user's changes are not discarded behind their back");
        });

        Program.Add("preset ops: rename of a preset that is not active puts the active one back", async () =>
        {
            var c = Make();
            await c.Sim.SendAsync(FxCommand.SelectPreset("G435 - Music"), TimeSpan.FromSeconds(1), CancellationToken.None);
            var r = await Run(c, new PresetOpRequest(PresetOpKind.Rename, "G435 - Video", "G435 - Movies"));
            Assert.True(r.Ok, "ok: " + r.Error);
            Assert.Equal("G435 - Music", c.Sim.Selected, "active preset restored");
            Clean(c);
        });

        Program.Add("preset ops: an engine apply requested during a duplicate waits until the end", async () =>
        {
            var c = Make();
            var op = Run(c, new PresetOpRequest(PresetOpKind.Duplicate, "G435 - Music", "Realtek - Music"));
            await Task.Delay(3);
            var apply = c.Ctl.ApplyAsync("G435 - Video", 0, CancellationToken.None);
            var r = await op;
            var a = await apply;
            Assert.True(r.Ok, "op ok: " + r.Error);
            Assert.True(a.Ok, "apply ok afterwards: " + a.FailureReason);
            var cmds = c.Sim.Commands;
            var applyAt = cmds.FindIndex(x => x.StartsWith("--power=1", StringComparison.Ordinal));
            var lastOp = cmds.FindLastIndex(x => x.Contains("Realtek - Music", StringComparison.Ordinal));
            Assert.True(applyAt > lastOp, $"apply after the whole operation ({applyAt} > {lastOp})");
            Clean(c);
        });
    }
}
