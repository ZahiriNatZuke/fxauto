using FxAuto.App;
using FxAuto.Core;
using FxAuto.Core.Audio;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Devices;
using FxAuto.Core.FxSound;
using FxAuto.Core.Presets;

namespace FxAuto.Tests;

/// <summary>Task 5: pure helpers of the coordinator (CoordinatorLogic.cs, linked) and the config store
/// (ConfigStore.cs, linked; runs against a temporary folder).</summary>
internal static class CoordinatorLogicTests
{
    private static FxStatus Status(string selected, bool modified = false, double leveling = 0, bool power = true)
        => new("1.2.16.0", selected, power, modified, leveling, "G435", ["G435 - Music", "G435 - FPS"], ["General"], DateTimeOffset.Now);

    private static SourceClass Cls(string category, bool browser = false)
        => new(category, null, null, "test", Categories.IsGame(category), browser);

    public static void Register()
    {
        Program.Add("coordinator: gate emits once, waits for the verification, stops when applied", () =>
        {
            var clock = new FakeClock();
            var g = new ApplyGate(clock);
            Assert.Null(g.TryEmit(), "nothing expected");
            g.SetExpected(new PresetTarget("G435 - Music", 0.5, "category"));
            Assert.Equal("G435 - Music", g.TryEmit()?.Preset, "first emit");
            Assert.Null(g.TryEmit(), "in flight blocks");
            g.MarkApplied("G435 - Music");
            Assert.Equal("G435 - Music", g.Verified, "verified");
            Assert.Null(g.TryEmit(), "verified: no command");
            g.SetExpected(new PresetTarget("G435 - Music", 0.5, "another reason"));
            Assert.Null(g.TryEmit(), "same preset, new reason: no command");
            g.SetExpected(new PresetTarget("G435 - FPS", 0, "game"));
            Assert.Equal("G435 - FPS", g.TryEmit()?.Preset, "new preset emits");
        });

        Program.Add("coordinator: gate backs off 5 s → 30 s after failures and in-flight expires after 30 s", () =>
        {
            var clock = new FakeClock();
            var g = new ApplyGate(clock);
            g.SetExpected(new PresetTarget("G435 - FPS", 0, "game"));
            Assert.True(g.TryEmit() is not null, "emit");
            g.MarkFailed("G435 - FPS", "FxSound closed");
            Assert.Equal("FxSound closed", g.LastFailure, "failure kept");
            Assert.Null(g.TryEmit(), "waiting after failure");
            Assert.Equal(clock.Now.AddSeconds(5), g.NextDueAt, "retry at 5 s");
            clock.Advance(5000);
            Assert.True(g.TryEmit() is not null, "retry");
            g.MarkFailed("G435 - FPS", "again");
            clock.Advance(29_000);
            Assert.Null(g.TryEmit(), "second wait is 30 s");
            clock.Advance(1000);
            Assert.True(g.TryEmit() is not null, "retry after 30 s");
            clock.Advance(30_000);
            Assert.True(g.TryEmit() is not null, "in-flight expired after 30 s");
            g.MarkFailed("G435 - Music", "old command");
            Assert.Equal("again", g.LastFailure, "failure of another preset does not touch the expected one's failure");
        });

        Program.Add("coordinator: gate trusts status.json only for the expected preset, on, unmodified, same leveling", () =>
        {
            var clock = new FakeClock();
            var g = new ApplyGate(clock);
            g.SetExpected(new PresetTarget("G435 - Music", 0.5, "category"));
            Assert.True(!g.ObserveStatus(Status("G435 - Music", modified: true, leveling: 0.5)), "modified does not count");
            Assert.True(!g.ObserveStatus(Status("G435 - Music", leveling: 1)), "other leveling does not count");
            Assert.True(!g.ObserveStatus(Status("G435 - Music", leveling: 0.5, power: false)), "power off does not count");
            Assert.True(g.ObserveStatus(Status("G435 - Music", leveling: 0.5)), "verified from status");
            Assert.Null(g.TryEmit(), "no command needed");
            Assert.True(!g.ObserveStatus(Status("General")), "someone selected General");
            Assert.Equal("G435 - Music", g.TryEmit()?.Preset, "re-imposed");
            g.MarkApplied("G435 - Music");
            g.ResetApplied();
            Assert.Equal("G435 - Music", g.TryEmit()?.Preset, "reset (preset deleted / FxSound restarted) re-applies");
            Assert.True(!g.ObserveStatus(Status("G435 - Music", leveling: 0.5)), "in flight: status may be stale");
        });

        Program.Add("coordinator: gate follows a renamed preset and forgets the old name's failure", () =>
        {
            var clock = new FakeClock();
            var g = new ApplyGate(clock);
            g.SetExpected(new PresetTarget("G435_Video", 0, "locked by the user"));
            Assert.True(g.TryEmit() is not null, "emit");
            g.MarkFailed("G435_Video", "preset does not exist in FxSound");
            g.RenamePreset("G435_Video", "G435 - Video");
            Assert.Equal("G435 - Video", g.Expected?.Preset, "expected renamed");
            Assert.Null(g.LastFailure, "old failure forgotten");
            Assert.Equal("G435 - Video", g.TryEmit()?.Preset, "emits the new name right away");
            g.MarkApplied("G435 - Video");
            g.RenamePreset("G435 - Video", "G435 - Movies");
            Assert.Equal("G435 - Movies", g.Verified, "verified renamed");
            Assert.Null(g.TryEmit(), "still verified: no command");
            g.RenamePreset("Other", "Else");
            Assert.Equal("G435 - Movies", g.Expected?.Preset, "unrelated rename changes nothing");
        });

        Program.Add("coordinator: lock and edit follow a rename", () =>
        {
            var cur = new ModeNames(Mode.Locked, Mode.Auto, "G435 - Video", null, null);
            var (n1, end1) = CoordinatorLogic.FollowPresetOp(cur, PresetOpKind.Rename, [("G435 - Video", "G435 - Clips")]);
            Assert.Equal("G435 - Clips", n1.Locked, "single rename");
            Assert.Equal(Mode.Locked, n1.Mode, "still locked");
            Assert.Null(end1, "lock kept");

            var other = new ModeNames(Mode.Locked, Mode.Auto, "General", null, null);
            Assert.Equal(other, CoordinatorLogic.FollowPresetOp(other, PresetOpKind.Rename, [("G435 - Video", "G435 - Clips")]).Next,
                "lock not renamed: unchanged");

            var edit = new ModeNames(Mode.Editing, Mode.Locked, "G435 - Video", "G435 - Music", "G435 - Music");
            var (n3, _) = CoordinatorLogic.FollowPresetOp(edit, PresetOpKind.Rename, [("G435 - Music", "G435 - Songs")]);
            Assert.Equal("G435 - Songs", n3.Editing, "edited preset renamed");
            Assert.Equal("G435 - Songs", n3.EditPending, "persisted edit-pending preset renamed");
            Assert.Equal("G435 - Video", n3.Locked, "lock to return to unchanged");
        });

        Program.Add("coordinator: deleting the locked preset ends the lock (back to Auto)", () =>
        {
            var cur = new ModeNames(Mode.Locked, Mode.Auto, "G435 - Video", null, null);
            var (n, ended) = CoordinatorLogic.FollowPresetOp(cur, PresetOpKind.Delete, [("G435 - Video", "G435 - Music")]);
            Assert.Equal(Mode.Auto, n.Mode, "auto");
            Assert.Null(n.Locked, "no locked preset");
            Assert.Equal("G435 - Video", ended, "lock ended reported");

            var (n2, ended2) = CoordinatorLogic.FollowPresetOp(cur, PresetOpKind.Delete, [("G435 - Music", "")]);
            Assert.Equal(cur, n2, "another preset deleted: lock kept");
            Assert.Null(ended2, "nothing reported");

            var editing = new ModeNames(Mode.Editing, Mode.Locked, "G435 - Video", "G435 - Music", "G435 - Music");
            var (n3, _) = CoordinatorLogic.FollowPresetOp(editing, PresetOpKind.Delete, [("G435 - Video", "")]);
            Assert.Equal(Mode.Editing, n3.Mode, "editing continues");
            Assert.Equal(Mode.Auto, n3.ModeBeforeEdit, "returns to Auto after the edit");
        });

        Program.Add("coordinator: user mode to persist (Locked, timed pause, edit returns, until-restart not saved)", () =>
        {
            var until = new DateTimeOffset(2026, 10, 26, 20, 0, 0, TimeSpan.Zero);
            Assert.Equal(new SavedMode(Mode.Locked, "G435 - Music", null), CoordinatorLogic.ToSaved(Mode.Locked, Mode.Auto, "G435 - Music", null), "locked");
            Assert.Equal(new SavedMode(Mode.Paused, null, until), CoordinatorLogic.ToSaved(Mode.Paused, Mode.Auto, "G435 - Music", until), "timed pause");
            Assert.Null(CoordinatorLogic.ToSaved(Mode.Paused, Mode.Auto, null, null), "pause until restart: nothing saved");
            Assert.Null(CoordinatorLogic.ToSaved(Mode.Auto, Mode.Auto, null, null), "auto: nothing saved");
            Assert.Equal(new SavedMode(Mode.Locked, "G435 - FPS", null), CoordinatorLogic.ToSaved(Mode.Editing, Mode.Locked, "G435 - FPS", null), "editing: the mode to go back to");
            Assert.Null(CoordinatorLogic.ToSaved(Mode.Editing, Mode.Auto, "G435 - FPS", null), "editing from Auto");
        });

        Program.Add("coordinator: restore Locked only when its preset still exists", () =>
        {
            var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
            var saved = new SavedMode(Mode.Locked, "G435 - Music", null);
            var r = CoordinatorLogic.RestoreMode(saved, now, p => p == "G435 - Music");
            Assert.Equal((Mode.Locked, "G435 - Music", (DateTimeOffset?)null, (string?)null), r, "preset exists: locked again");
            var gone = CoordinatorLogic.RestoreMode(saved, now, _ => false);
            Assert.Equal((Mode.Auto, (string?)null, (DateTimeOffset?)null, "G435 - Music"), gone, "preset deleted: Auto, dropped lock reported");
            Assert.Equal(Mode.Auto, CoordinatorLogic.RestoreMode(null, now, _ => true).Mode, "nothing saved: Auto");
        });

        Program.Add("coordinator: restore a timed pause only while it has not ended", () =>
        {
            var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
            var live = CoordinatorLogic.RestoreMode(new SavedMode(Mode.Paused, null, now.AddMinutes(20)), now, _ => true);
            Assert.Equal((Mode.Paused, (string?)null, (DateTimeOffset?)now.AddMinutes(20), (string?)null), live, "pause still running");
            Assert.Equal(Mode.Auto, CoordinatorLogic.RestoreMode(new SavedMode(Mode.Paused, null, now.AddMinutes(-1)), now, _ => true).Mode, "ended pause: Auto");
            Assert.Equal(Mode.Auto, CoordinatorLogic.RestoreMode(new SavedMode(Mode.Paused, null, now), now, _ => true).Mode, "ends right now: Auto");
            Assert.Equal(Mode.Auto, CoordinatorLogic.RestoreMode(new SavedMode(Mode.Paused, null, null), now, _ => true).Mode, "pause without end is not restored");
        });

        Program.Add("coordinator: a rename carries the persisted lock (file, rename, file, restore)", () =>
        {
            var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
            var state = LearnedStateJson.Empty() with { UserMode = CoordinatorLogic.ToSaved(Mode.Locked, Mode.Auto, "G435_Video", null) };
            var (loaded, _) = LearnedStateJson.Parse(LearnedStateJson.Serialize(state));
            var (mode, locked, until, _) = CoordinatorLogic.RestoreMode(loaded.UserMode, now, _ => true);
            var cur = new ModeNames(mode, Mode.Auto, locked, null, null);
            var (next, _) = CoordinatorLogic.FollowPresetOp(cur, PresetOpKind.Rename, [("G435_Video", "G435 - Video")]);
            var after = loaded with { UserMode = CoordinatorLogic.ToSaved(next.Mode, next.ModeBeforeEdit, next.Locked, until) };
            var (reloaded, _) = LearnedStateJson.Parse(LearnedStateJson.Serialize(after));
            Assert.Equal(new SavedMode(Mode.Locked, "G435 - Video", null), reloaded.UserMode, "file holds the new name");
            var r = CoordinatorLogic.RestoreMode(reloaded.UserMode, now, p => p == "G435 - Video");
            Assert.Equal("G435 - Video", r.Locked, "restarts locked on the renamed preset");

            var (deleted, _) = CoordinatorLogic.FollowPresetOp(next, PresetOpKind.Delete, [("G435 - Video", "")]);
            Assert.Null(CoordinatorLogic.ToSaved(deleted.Mode, deleted.ModeBeforeEdit, deleted.Locked, until), "deleted lock: nothing saved (Auto)");
        });

        Program.Add("coordinator: candidates = playing roots + foreground (silent game only when unknown to the detector)", () =>
        {
            var t0 = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
            var states = new List<PlayingState>
            {
                new(10, "chrome.exe", true, t0, t0),
                new(20, "spotify.exe", false, null, t0),
                new(30, "game.exe", false, null, t0),
                new(40, "zoom.exe", true, t0, t0),
            };
            var classes = new Dictionary<int, SourceClass>
            {
                [10] = Cls(Categories.Video, browser: true), [20] = Cls(Categories.Music), [30] = Cls(Categories.GameFps),
                [40] = Cls(Categories.Call), [50] = Cls(Categories.GameRpg), [60] = Cls(Categories.Unknown),
            };
            SourceClass Classify(int pid) => classes[pid];
            DateTimeOffset? Start(int pid) => t0.AddMinutes(-pid);

            var c = CoordinatorLogic.BuildCandidates(states, 30, Classify, Start);
            Assert.Equal("10,30,40", string.Join(",", c.Select(x => x.RootPid)), "playing + silent foreground known to the detector");
            Assert.True(c.Single(x => x.RootPid == 30).IsForeground && !c.Single(x => x.RootPid == 30).Playing, "foreground flag");
            Assert.Equal(t0.AddMinutes(-30), c.Single(x => x.RootPid == 30).ProcessStart, "process start");

            var silentGame = CoordinatorLogic.BuildCandidates(states, 50, Classify, Start);
            Assert.True(silentGame.Any(x => x.RootPid == 50 && x.IsForeground && !x.Playing && x.Class.IsGame), "silent foreground game added");

            var silentApp = CoordinatorLogic.BuildCandidates(states, 60, Classify, Start);
            Assert.True(silentApp.All(x => x.RootPid != 60), "silent foreground non-game unknown to the detector: not added");

            var exited = CoordinatorLogic.BuildCandidates(states, 50, Classify, Start, new HashSet<int> { 10, 50 });
            Assert.Equal("40", string.Join(",", exited.Select(x => x.RootPid)), "exited roots left out");
        });

        Program.Add("coordinator: playing changes, ignored apps and neutral foreground windows", () =>
        {
            var t0 = DateTimeOffset.Now;
            var before = new Dictionary<int, bool> { [1] = true, [2] = false, [3] = true };
            var after = new List<PlayingState> { new(1, "a.exe", true, t0, t0), new(2, "b.exe", true, t0, t0), new(4, "d.exe", false, null, null) };
            var ch = CoordinatorLogic.PlayingChanges(before, after);
            Assert.Equal("2:True,3:False", string.Join(",", ch.OrderBy(x => x.RootPid).Select(x => $"{x.RootPid}:{x.Playing}")), "start 2, stop 3 (forgotten)");

            var snap = new AudioSnapshot(t0, "fx", null, [
                new SessionSample("fx", 5, 5, "Discord.exe", null, null, 0.5f, true),
                new SessionSample("fx", 6, 6, "chrome.exe", null, null, 0.5f, true)]);
            var filtered = CoordinatorLogic.WithoutIgnored(snap, ["discord.exe"]);
            Assert.Equal("chrome.exe", string.Join(",", filtered.Sessions.Select(s => s.RootExe)), "ignored app removed (case-insensitive)");
            Assert.True(ReferenceEquals(snap, CoordinatorLogic.WithoutIgnored(snap, ["x.exe"])), "nothing to remove: same snapshot");

            ForegroundInfo Fg(string? exe, int pid, bool unreadable = false) => new(1, exe, null, null, null, pid, null, unreadable);
            Assert.True(CoordinatorLogic.IsNeutralForeground(Fg("FxAuto.exe", 99), 99, [], []), "own window");
            Assert.True(CoordinatorLogic.IsNeutralForeground(Fg("game.exe", 7, unreadable: true), 99, [], []), "elevated/unreadable");
            Assert.True(CoordinatorLogic.IsNeutralForeground(Fg("steam.exe", 7), 99, [], ["Steam.exe"]), "launcher");
            Assert.True(CoordinatorLogic.IsNeutralForeground(Fg("explorer.exe", 7), 99, ["explorer.exe"], []), "ignored");
            Assert.True(!CoordinatorLogic.IsNeutralForeground(Fg("cs2.exe", 7), 99, ["explorer.exe"], ["steam.exe"]), "a game is not neutral");
        });

        Program.Add("coordinator: unknown-device key is stable across port prefixes; pause expiry; once-set", () =>
        {
            Assert.Equal(CoordinatorLogic.UnknownDeviceKey("2- Some Buds", "x", FormFactor.Headphones),
                CoordinatorLogic.UnknownDeviceKey("3- Some Buds", "y", FormFactor.Headphones), "port prefix ignored");
            var now = DateTimeOffset.Now;
            Assert.True(CoordinatorLogic.PauseExpired(Mode.Paused, now.AddSeconds(-1), now), "1 h pause ended");
            Assert.True(!CoordinatorLogic.PauseExpired(Mode.Paused, null, now), "until restart never ends");
            Assert.True(!CoordinatorLogic.PauseExpired(Mode.Auto, now.AddSeconds(-1), now), "not paused");
            var once = new OnceSet();
            Assert.True(once.First("k") && !once.First("k"), "once");
            once.Forget("k");
            Assert.True(once.First("k"), "forgotten");
            Assert.Equal(now, CoordinatorLogic.Earliest(null, now.AddSeconds(1), now), "earliest");
        });

        Program.Add("config store: first run writes the defaults; own saves are ignored by the watcher check", () =>
        {
            using var dir = new TempDir();
            using var store = new ConfigStore(dir.Path, (_, _) => { });
            var load = store.Load();
            Assert.Equal(ConfigOrigin.Default, load.Origin, "default");
            Assert.True(File.Exists(store.ConfigFile), "config.json written");
            Assert.Null(store.CheckFile(), "just written: no change reported");

            var edited = load.Config with { General = load.Config.General with { MinDwellMs = 4000 } };
            store.Save(edited);
            Assert.True(File.Exists(store.BackupFile), "config.json.bak kept");
            Assert.Null(store.CheckFile(), "own save ignored");

            // hand edit (valid)
            File.WriteAllText(store.ConfigFile, ConfigJson.Serialize(edited with { General = edited.General with { MinDwellMs = 3000 } }));
            var change = store.CheckFile();
            Assert.Equal(3000, change?.Config?.General.MinDwellMs, "hand edit reported");
            Assert.Null(store.CheckFile(), "same content is not reported twice");

            // hand edit (broken JSON): keep the last valid config
            File.WriteAllText(store.ConfigFile, "{ broken");
            var bad = store.CheckFile();
            Assert.True(bad is not null && bad.Config is null && bad.Issues.Any(i => i.Level == IssueLevel.Error), "invalid edit reported without a config");
        });

        Program.Add("config store: first run ignores old rules files (reglas*.json): defaults, files untouched", () =>
        {
            using var dir = new TempDir();
            var reglas = Path.Combine(dir.Path, "reglas.json");
            const string old = "{ \"reglas\": [ { \"nombre\": \"x\", \"exe\": \"a.exe\", \"preset\": \"P\" } ] }";
            File.WriteAllText(reglas, old);
            var events = new List<string>();
            using var store = new ConfigStore(dir.Path, (e, _) => events.Add(e));
            var load = store.Load();
            Assert.Equal(ConfigOrigin.Default, load.Origin, "defaults, not a migration");
            Assert.Equal(old, File.ReadAllText(reglas), "reglas.json untouched");
            Assert.True(!Directory.GetFiles(dir.Path, "migration*").Any(), "no migration log written");
            Assert.True(!load.Issues.Any(i => i.Level != IssueLevel.Info), "no errors or warnings");
            Assert.True(events.Contains("config.created"), "config.created logged");
        });

        Program.Add("config store: corrupt config.json at startup uses the backup and is not overwritten; newer schema is read-only", () =>
        {
            using var dir = new TempDir();
            using (var first = new ConfigStore(dir.Path, (_, _) => { }))
            {
                var l = first.Load();
                first.Save(l.Config);                     // creates config.json.bak
            }
            File.WriteAllText(Path.Combine(dir.Path, ConfigStore.ConfigFileName), "{ broken");
            using (var store = new ConfigStore(dir.Path, (_, _) => { }))
            {
                var load = store.Load();
                Assert.Equal(ConfigOrigin.Backup, load.Origin, "backup used");
                Assert.True(load.Issues.Any(i => i.Level == IssueLevel.Error && i.Path == ConfigStore.ConfigFileName), "error shown");
                Assert.Equal("{ broken", File.ReadAllText(store.ConfigFile), "broken file kept for the user to fix");
            }

            var current = ConfigJson.Serialize(DefaultConfig.Create());
            var newer = System.Text.RegularExpressions.Regex.Replace(current, @"""schemaVersion""\s*:\s*\d+",
                $"\"schemaVersion\": {ConfigJson.CurrentSchemaVersion + 1}");
            Assert.True(newer != current, "schemaVersion replaced");
            File.WriteAllText(Path.Combine(dir.Path, ConfigStore.ConfigFileName), newer);
            using (var store = new ConfigStore(dir.Path, (_, _) => { }))
            {
                var load = store.Load();
                Assert.True(store.ReadOnlyReason is not null, "read-only: " + load.Origin);
                bool threw = false;
                try { store.Save(load.Config); } catch (InvalidOperationException) { threw = true; }
                Assert.True(threw, "never saved");
            }
        });

        Program.Add("config store: Load uses a valid config.json as it is", () =>
        {
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, ConfigStore.ConfigFileName),
                ConfigJson.Serialize(DefaultConfig.Create() with { General = DefaultConfig.CreateGeneral() with { MinDwellMs = 4321 } }));
            using var store = new ConfigStore(dir.Path, (_, _) => { });
            var load = store.Load();
            Assert.Equal(ConfigOrigin.File, load.Origin, "file");
            Assert.Equal(4321, load.Config.General.MinDwellMs, "content");
            Assert.Null(store.SaveBlockedReason, "saves allowed");
        });

        Program.Add("config store: config.json with validation errors + valid .bak → backup active; first save keeps the .bak", () =>
        {
            using var dir = new TempDir();
            var good = ConfigJson.Serialize(DefaultConfig.Create() with { General = DefaultConfig.CreateGeneral() with { MinDwellMs = 4321 } });
            File.WriteAllText(Path.Combine(dir.Path, ConfigStore.BackupFileName), good);
            var bad = InvalidConfigJson();
            File.WriteAllText(Path.Combine(dir.Path, ConfigStore.ConfigFileName), bad);
            using var store = new ConfigStore(dir.Path, (_, _) => { });
            var load = store.Load();
            Assert.Equal(ConfigOrigin.Backup, load.Origin, "backup used, not the file with errors");
            Assert.Equal(4321, load.Config.General.MinDwellMs, "backup content");
            Assert.True(load.Issues.Any(i => i.Level == IssueLevel.Error && i.Path == ConfigStore.ConfigFileName), "error in Status");
            Assert.Null(store.SaveBlockedReason, "a valid backup allows saving");

            store.Save(load.Config with { General = load.Config.General with { MinDwellMs = 1111 } });
            Assert.Equal(good, File.ReadAllText(store.BackupFile), "last good copy not replaced by the broken file");
            Assert.Equal(bad, File.ReadAllText(store.InvalidFile), "broken file moved aside to config.json.invalid");
            var (saved, _) = ConfigJson.Parse(File.ReadAllText(store.ConfigFile));
            Assert.Equal(1111, saved?.General.MinDwellMs, "new config saved");

            store.Save(load.Config);
            Assert.True(File.ReadAllText(store.BackupFile).Contains("1111", StringComparison.Ordinal), "valid file → normal backup again");
        });

        Program.Add("config store: config.json with errors and no .bak → defaults, protected until confirmed or fixed", () =>
        {
            using var dir = new TempDir();
            var bad = InvalidConfigJson();
            File.WriteAllText(Path.Combine(dir.Path, ConfigStore.ConfigFileName), bad);
            using var store = new ConfigStore(dir.Path, (_, _) => { });
            var load = store.Load();
            Assert.Equal(ConfigOrigin.DefaultAfterError, load.Origin, "defaults");
            Assert.True(store.ProtectedReason is not null && store.SaveBlockedReason == store.ProtectedReason, "protected");
            bool threw = false;
            try { store.Save(load.Config); } catch (InvalidOperationException) { threw = true; }
            Assert.True(threw, "plain save refused");
            Assert.Equal(bad, File.ReadAllText(store.ConfigFile), "user's file untouched");

            // the user fixes the file by hand: protection ends
            var fixedJson = ConfigJson.Serialize(DefaultConfig.Create() with { General = DefaultConfig.CreateGeneral() with { MinDwellMs = 2222 } });
            File.WriteAllText(store.ConfigFile, fixedJson);
            Assert.Equal(2222, store.CheckFile()?.Config?.General.MinDwellMs, "fixed file reported");
            Assert.Null(store.ProtectedReason, "no longer protected");

            // broken again, then the user confirms the replacement
            File.WriteAllText(store.ConfigFile, "{ broken");
            Assert.True(store.CheckFile()?.Config is null, "broken edit");
            store.Save(load.Config, confirmReplace: false);   // not protected (a valid config was loaded since): allowed
            Assert.Equal("{ broken", File.ReadAllText(store.InvalidFile), "broken edit moved aside");
            Assert.True(!File.Exists(store.BackupFile), "no .bak made from a broken file");
        });

        Program.Add("config store: protected defaults can be saved over the file only with confirmation", () =>
        {
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, ConfigStore.ConfigFileName), "{ broken");
            using var store = new ConfigStore(dir.Path, (_, _) => { });
            var load = store.Load();
            store.Save(load.Config, confirmReplace: true);
            Assert.Null(store.ProtectedReason, "confirmed: protection ends");
            Assert.Equal("{ broken", File.ReadAllText(store.InvalidFile), "the replaced file is kept as config.json.invalid");
            Assert.True(ConfigJson.Parse(File.ReadAllText(store.ConfigFile)).Config is not null, "defaults written");
        });

        Program.Add("config store: runtime state round-trip and corrupt state starts empty", () =>
        {
            using var dir = new TempDir();
            using var store = new ConfigStore(dir.Path, (_, _) => { });
            var s = LearnedStateJson.Learn(LearnedStateJson.Empty(), "g435", "{0.0.0.00000000}.{abc}") with { EditPendingPreset = "G435 - Music" };
            Assert.Null(store.SaveState(s), "saved");
            var (back, issues) = store.LoadState();
            Assert.True(issues.Count == 0 && back.EditPendingPreset == "G435 - Music"
                && back.EndpointIdsByDevice["g435"].Contains("{0.0.0.00000000}.{abc}"), "round-trip");
            File.WriteAllText(store.StateFile, "{ nope");
            var (empty, warn) = store.LoadState();
            Assert.True(empty.EndpointIdsByDevice.Count == 0 && warn.Count > 0, "corrupt → empty + warning");
        });
    }

    /// <summary>Parseable config.json whose validation fails (unknown log level).</summary>
    private static string InvalidConfigJson()
    {
        var json = ConfigJson.Serialize(DefaultConfig.Create());
        var bad = System.Text.RegularExpressions.Regex.Replace(json, @"""logLevel""\s*:\s*""info""", @"""logLevel"": ""verbose""");
        Assert.True(bad != json, "logLevel replaced");
        var (cfg, issues) = ConfigJson.Parse(bad);
        Assert.True(cfg is not null && issues.Any(i => i.Level == IssueLevel.Error), "parseable with errors");
        return bad;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fxauto-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
