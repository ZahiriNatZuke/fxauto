using System.Text.RegularExpressions;
using FxAuto.Core;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Devices;
using FxAuto.Core.FxSound;

namespace FxAuto.Tests;

/// <summary>Task 3a: config model, JSON, validation, defaults, edits and runtime state.</summary>
internal static class ConfigTests
{
    private static FxAutoConfig Sample()
    {
        var d = DefaultConfig.Create();
        var g435 = new DeviceConfig("g435", "G435 headset", "G435",
            new DeviceMatchSpec(["G435 Wireless Gaming Headset"], FormFactor.Headset, null, [], null), "G435 - General", true,
            "Headphones (G435 Wireless Gaming Headset)");
        var speakers = new DeviceConfig("realtek", "Desk speakers", "Realtek",
            new DeviceMatchSpec(["Realtek(R) Audio"], FormFactor.Speakers, "Speakers", ["{0.0.0.00000000}.{abc}"], "{c1}"), "Realtek - General", true, null);
        return d with
        {
            Presets = new Dictionary<string, PresetMeta>
            {
                ["G435 - General"] = new(0, null),
                ["G435 - Video"] = new(1.5, "louder dialogue"),
                ["Realtek - General"] = new(0, null),
            },
            Devices = [g435, speakers],
            Assignments =
            [
                new("g435", Categories.Video, null, "G435 - Video"),
                new("g435", null, "youtube", "G435 - Video"),
                new("realtek", Categories.Music, null, "Realtek - General"),
            ],
        };
    }

    private static IReadOnlyList<ConfigIssue> Errors(IReadOnlyList<ConfigIssue> issues)
        => issues.Where(i => i.Level == IssueLevel.Error).ToList();

    private static string Dump(IEnumerable<ConfigIssue> issues) => string.Join(" | ", issues.Select(i => $"{i.Level} {i.Path}: {i.Message}"));

    private static void HasError(FxAutoConfig c, string pathPart, string what)
    {
        var errs = Errors(ConfigValidator.Validate(c));
        Assert.True(errs.Any(e => e.Path.Contains(pathPart, StringComparison.Ordinal)), $"{what}: expected an error at \"{pathPart}\", got: {Dump(errs)}");
    }

    private static void NoErrors(FxAutoConfig c, string what)
    {
        var errs = Errors(ConfigValidator.Validate(c));
        Assert.True(errs.Count == 0, $"{what}: unexpected errors: {Dump(errs)}");
    }

    private static FxAutoConfig WithSource(FxAutoConfig c, SourceConfig s) => c with { Sources = [.. c.Sources, s] };

    private static SourceConfig SiteSource(string id, string category, IReadOnlyList<string> sites, string? regex = null)
        => new(id, id, SourceKind.Site, new SourceMatch([], [], [], sites, regex), category, false, DefaultConfig.OriginUser);

    public static void Register()
    {
        // ── defaults ──
        Program.Add("Config: the default config has no errors or warnings", () =>
        {
            var issues = ConfigValidator.Validate(DefaultConfig.Create());
            Assert.True(issues.All(i => i.Level == IssueLevel.Info), "default issues: " + Dump(issues));
        });

        Program.Add("Config: defaults keep REQ-112 off, titles out of logs and no devices", () =>
        {
            var c = DefaultConfig.Create();
            Assert.Equal(2, c.SchemaVersion, "schemaVersion");
            Assert.True(!c.General.StartFxSoundIfClosed, "startFxSoundIfClosed false");
            Assert.True(!c.General.LogTitles, "logTitles false");
            Assert.Equal("keepCurrent", c.General.UnknownDevice, "unknownDevice");
            Assert.Equal(0, c.Devices.Count, "no devices");
            Assert.Equal(0, c.Assignments.Count, "no assignments");
            Assert.True(c.General.AudibleTabMarkers.Contains("Audio playing"), "audible marker");
            Assert.True(c.Sources.Any(s => s.Kind == SourceKind.Browser && s.Category == Categories.Video), "browser default video");
            Assert.True(c.Sources.Any(s => s.Id == "steam-generic" && s.Fallback), "steam generic fallback");
            Assert.True(c.Sources.All(s => s.Origin == DefaultConfig.OriginBuiltIn), "origin builtIn");
            Assert.True(!ReferenceEquals(DefaultConfig.Create().Sources, c.Sources), "new instances");
        });

        Program.Add("Config: the sports regex is segment-safe (SPOTIFY CAMP NOU stays video)", () =>
        {
            var rx = new Regex(DefaultConfig.SportsRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            Assert.True(!rx.IsMatch("FC Barcelona at SPOTIFY CAMP NOU - YouTube"), "Camp Nou title");
            Assert.True(!rx.IsMatch("Championship cooking show - YouTube"), "Champions inside a word");
            foreach (var t in new[] { "Real Madrid vs Barcelona - YouTube", "El Clásico en directo", "LaLiga: resumen", "Champions League final",
                         "Jornada 12 completa", "Partido completo - YouTube", "Clasico highlights" })
                Assert.True(rx.IsMatch(t), "sports title: " + t);
        });

        // ── JSON ──
        Program.Add("Config: serialize → parse → serialize is stable (default and full sample)", () =>
        {
            foreach (var c in new[] { DefaultConfig.Create(), Sample() })
            {
                var json = ConfigJson.Serialize(c);
                var (parsed, issues) = ConfigJson.Parse(json);
                Assert.True(parsed is not null, "parsed: " + Dump(issues));
                Assert.True(Errors(issues).Count == 0, "issues: " + Dump(issues));
                Assert.Equal(json, ConfigJson.Serialize(parsed!), "second serialization");
            }
        });

        Program.Add("Config: round-trip keeps every field of devices, sources and assignments", () =>
        {
            var c = Sample();
            var (p, _) = ConfigJson.Parse(ConfigJson.Serialize(c));
            var r = p!.Devices[1];
            Assert.Equal(FormFactor.Speakers, r.Match.FormFactor, "formFactor");
            Assert.Equal("Speakers", r.Match.EndpointDesc, "endpointDesc");
            Assert.Equal("{0.0.0.00000000}.{abc}", r.Match.PinnedEndpointIds[0], "pinned");
            Assert.Equal("{c1}", r.Match.ContainerId, "containerId");
            Assert.Equal("Headphones (G435 Wireless Gaming Headset)", p.Devices[0].LastSeenOutputName, "lastSeen");
            Assert.Equal("louder dialogue", p.Presets["G435 - Video"].Note, "note");
            Assert.Equal(1.5, p.Presets["G435 - Video"].VolumeLeveling, "leveling");
            Assert.Equal("youtube", p.Assignments[1].Source, "source assignment");
            Assert.Null(p.Assignments[1].Category, "no category");
            Assert.Equal(c.Sources.Count, p.Sources.Count, "sources");
            var sports = p.Sources.Single(s => s.Id == "sports-keywords");
            Assert.Equal(DefaultConfig.SportsRegex, sports.Match.TitleRegex, "regex text with accents and escapes");
            Assert.Equal(c.General.Detection, p.General.Detection, "detection timings");
            Assert.Equal(c.General.CallPolicy, p.General.CallPolicy, "call policy");
        });

        Program.Add("Config: written JSON is strict, camelCase and readable", () =>
        {
            var json = ConfigJson.Serialize(Sample());
            Assert.True(json.Contains("\"schemaVersion\": 2", StringComparison.Ordinal), "schemaVersion");
            Assert.True(json.Contains("\"formFactor\": \"headset\"", StringComparison.Ordinal), "enum camelCase");
            Assert.True(json.Contains("\"kind\": \"browser\"", StringComparison.Ordinal), "kind camelCase");
            Assert.True(json.Contains("\"callPolicy\": \"ignore\"", StringComparison.Ordinal), "callPolicy");
            Assert.True(json.Contains("Clásico", StringComparison.Ordinal) || json.Contains("Cl[áa]sico", StringComparison.Ordinal), "accents unescaped");
            using var _ = System.Text.Json.JsonDocument.Parse(json);   // strict parser, no options
        });

        Program.Add("Config: comments and trailing commas are accepted on read; missing sections take defaults", () =>
        {
            const string json = """
            // hand-edited
            {
              "schemaVersion": 2, // current
              "general": { "logLevel": "debug", "detection": { "releaseMs": 9000, }, },
              "devices": [
                { "id": "g435", "displayName": "G435", "tag": "G435",
                  "match": { "adapters": "G435 Wireless Gaming Headset", "formFactor": "Headset" },
                  "defaultPreset": "G435_General", },
              ],
            }
            """;
            var (c, issues) = ConfigJson.Parse(json);
            Assert.True(c is not null, "parsed: " + Dump(issues));
            Assert.True(Errors(issues).Count == 0, "no errors: " + Dump(issues));
            Assert.Equal("debug", c!.General.LogLevel, "logLevel");
            Assert.Equal(9000, c.General.Detection.ReleaseMs, "releaseMs");
            Assert.Equal(250, c.General.Detection.PollMs, "pollMs default");
            Assert.Equal(60, c.General.StartupGraceSeconds, "grace default");
            Assert.Equal("G435 Wireless Gaming Headset", c.Devices[0].Match.Adapters.Single(), "single string as list");
            Assert.Equal(FormFactor.Headset, c.Devices[0].Match.FormFactor, "enum case-insensitive");
            Assert.True(c.Devices[0].Enabled, "enabled default");
            Assert.Equal(0, c.Sources.Count, "missing sources = none");
        });

        Program.Add("Config: invalid JSON, wrong types and missing schemaVersion are rejected", () =>
        {
            var (a, ia) = ConfigJson.Parse("{ \"schemaVersion\": 2, ");
            Assert.Null(a, "invalid JSON");
            Assert.True(ia.Any(i => i.Level == IssueLevel.Error), "error issue");

            var (b, ib) = ConfigJson.Parse("""{ "schemaVersion": 2, "general": { "startupGraceSeconds": "sixty" } }""");
            Assert.Null(b, "wrong type");
            Assert.True(ib.Any(i => i.Path == "general.startupGraceSeconds"), "path of the wrong type: " + Dump(ib));

            var (c, ic) = ConfigJson.Parse("""{ "general": {} }""");
            Assert.Null(c, "missing schemaVersion");
            Assert.True(ic.Any(i => i.Path == "schemaVersion"), "schemaVersion issue");

            var (d, id) = ConfigJson.Parse("""{ "schemaVersion": 2, "sources": [ { "id": "x", "kind": "plugin", "category": "music" } ] }""");
            Assert.Null(d, "unknown enum");
            Assert.True(id.Any(i => i.Path == "sources[0].kind"), "kind issue: " + Dump(id));

            var (e, _) = ConfigJson.Parse("[1, 2]");
            Assert.Null(e, "root array");
        });

        Program.Add("Config: unknown keys are warnings, not errors", () =>
        {
            var (c, issues) = ConfigJson.Parse("""{ "schemaVersion": 2, "uiLanguage": "en", "general": { "debounce": {} } }""");
            Assert.True(c is not null, "parsed");
            Assert.True(issues.Any(i => i.Level == IssueLevel.Warning && i.Path == "uiLanguage"), "root unknown key");
            Assert.True(issues.Any(i => i.Level == IssueLevel.Warning && i.Path == "general.debounce"), "nested unknown key");
            Assert.True(Errors(issues).Count == 0, "no errors: " + Dump(issues));
        });

        Program.Add("Config: schemaVersion 3 is read-only (error on read, refused on write)", () =>
        {
            var json = ConfigJson.Serialize(DefaultConfig.Create()).Replace("\"schemaVersion\": 2", "\"schemaVersion\": 3", StringComparison.Ordinal);
            var (c, issues) = ConfigJson.Parse(json);
            Assert.True(c is not null, "best-effort config for read-only display");
            Assert.True(issues.Any(i => i.Level == IssueLevel.Error && i.Path == "schemaVersion"), "error: " + Dump(issues));
            var threw = false;
            try { ConfigJson.Serialize(c!); }
            catch (InvalidOperationException) { threw = true; }
            Assert.True(threw, "Serialize must refuse schemaVersion 3");
            HasError(DefaultConfig.Create() with { SchemaVersion = 1 }, "schemaVersion", "old schema");
        });

        // ── validation ──
        Program.Add("Validation: an assignment with category hold is an error; hold on a source is fine", () =>
        {
            var c = Sample();
            HasError(c with { Assignments = [new("g435", Categories.Hold, null, "G435 - General")] }, "assignments[0].category", "hold");
            var obs = new SourceConfig("obs", "OBS", SourceKind.App, new SourceMatch(["obs64.exe"], [], [], [], null), Categories.Hold, false, "user");
            NoErrors(WithSource(c, obs), "hold source");
            var generic = new SourceConfig("all-games", "All", SourceKind.Game, new SourceMatch([], [], [@"\games\"], [], null), Categories.Hold, true, "user");
            Assert.True(ConfigValidator.Validate(WithSource(c, generic)).Any(i => i.Level == IssueLevel.Warning && i.Path.EndsWith(".category", StringComparison.Ordinal)),
                "hold on a fallback warns");
        });

        Program.Add("Validation: categories must be in the fixed taxonomy", () =>
        {
            var c = Sample();
            HasError(c with { Assignments = [new("g435", "movie", null, "G435 - General")] }, "assignments[0].category", "assignment category");
            HasError(WithSource(c, SiteSource("x", "voice", ["X"])), ".category", "source category");
            NoErrors(c with { Assignments = [new("g435", Categories.Unknown, null, "G435 - General")] }, "unknown is assignable");
        });

        Program.Add("Validation: broken references and malformed assignments are errors", () =>
        {
            var c = Sample();
            HasError(c with { Assignments = [new("ghost", Categories.Music, null, "P")] }, "assignments[0].device", "device ref");
            HasError(c with { Assignments = [new("g435", null, "ghost", "P")] }, "assignments[0].source", "source ref");
            HasError(c with { Assignments = [new("g435", Categories.Music, "youtube", "P")] }, "assignments[0]", "both");
            HasError(c with { Assignments = [new("g435", null, null, "P")] }, "assignments[0]", "neither");
            HasError(c with { Assignments = [new("g435", Categories.Music, null, "")] }, "assignments[0].preset", "empty preset");
            HasError(c with { Assignments = [new("g435", Categories.Music, null, new string('x', 65))] }, "assignments[0].preset", "65 chars");
        });

        Program.Add("Validation: two assignments with the same key are an error", () =>
        {
            var c = Sample();
            HasError(c with { Assignments = [new("g435", Categories.Music, null, "A"), new("G435", Categories.Music, null, "B")] }, "assignments[1]", "dup category");
            HasError(c with { Assignments = [new("g435", null, "youtube", "A"), new("g435", null, "youtube", "B")] }, "assignments[1]", "dup source");
            NoErrors(c with { Assignments = [new("g435", Categories.Music, null, "A"), new("realtek", Categories.Music, null, "A")] }, "same category, other device");
        });

        Program.Add("Validation: device tag is 1–20 ASCII [A-Za-z0-9 _.-]", () =>
        {
            var c = Sample();
            FxAutoConfig Tag(string t) => c with { Devices = [c.Devices[0] with { Tag = t }, c.Devices[1]] };
            HasError(Tag(""), "devices[0].tag", "empty");
            HasError(Tag("ABCDEFGHIJKLMNOPQRSTU"), "devices[0].tag", "21 chars");
            HasError(Tag("Auriculares ñ"), "devices[0].tag", "non-ASCII");
            HasError(Tag(" G435"), "devices[0].tag", "leading space");
            HasError(Tag("G435/BT"), "devices[0].tag", "slash");
            NoErrors(Tag("G435 BT_v1.2-x"), "allowed chars");
            NoErrors(Tag("ABCDEFGHIJKLMNOPQRST"), "20 chars");
            Assert.True(ConfigValidator.Validate(Tag("Realtek")).Any(i => i.Level == IssueLevel.Warning && i.Path == "devices[1].tag"), "duplicate tag warns");
        });

        Program.Add("Validation: a managed device needs a default preset and a match", () =>
        {
            var c = Sample();
            HasError(c with { Devices = [c.Devices[0] with { DefaultPreset = "" }, c.Devices[1]] }, "devices[0].defaultPreset", "no default");
            NoErrors(c with { Devices = [c.Devices[0] with { DefaultPreset = "", Enabled = false }, c.Devices[1]] }, "disabled device without default");
            HasError(c with { Devices = [c.Devices[0] with { Match = new DeviceMatchSpec([], null, null, [], null) }, c.Devices[1]] }, "devices[0].match", "no adapter");
            HasError(c with { Devices = [c.Devices[0], c.Devices[1] with { Id = "G435" }] }, "devices[1].id", "duplicate id");
            HasError(c with { Devices = [c.Devices[0] with { Id = "g 435" }, c.Devices[1]] }, "devices[0].id", "id with space");
        });

        Program.Add("Validation: two managed devices matching the same output", () =>
        {
            var c = Sample();
            var a = c.Devices[0];
            var same = a with { Id = "g435-copy", Tag = "G435b" };
            HasError(c with { Devices = [a, same, c.Devices[1]] }, "devices[1].match", "identical match");
            NoErrors(c with { Devices = [c.Devices[1], a, same with { Enabled = false }] }, "one disabled");
            NoErrors(c with { Devices = [c.Devices[1], a, same with { Match = a.Match with { FormFactor = FormFactor.Handset } }] }, "told apart by form factor");
            var partial = same with { Match = a.Match with { FormFactor = null } };
            var issues = ConfigValidator.Validate(c with { Devices = [a, partial, c.Devices[1]] });
            Assert.True(Errors(issues).Count == 0 && issues.Any(i => i.Level == IssueLevel.Warning && i.Path == "devices[1].match"), "partial overlap warns: " + Dump(issues));
        });

        Program.Add("Validation: site \"*\" needs a bounded regex", () =>
        {
            var c = Sample();
            HasError(WithSource(c, SiteSource("any", Categories.Sports, ["*"])), ".match.titleRegex", "star without regex");
            HasError(WithSource(c, SiteSource("any", Categories.Sports, ["*"], "Champions")), ".match.titleRegex", "unbounded");
            HasError(WithSource(c, SiteSource("any", Categories.Sports, ["*"], @"\bLaLiga\b|Champions")), ".match.titleRegex", "one alternative unbounded");
            HasError(WithSource(c, SiteSource("any", Categories.Sports, ["*", "YouTube"], @"\bLaLiga\b")), ".match.site", "star mixed");
            NoErrors(WithSource(c, SiteSource("any", Categories.Sports, ["*"], @"\b(LaLiga|Champions)\b| vs\.? ")), "plan regex");
            NoErrors(WithSource(c, SiteSource("yt-matches", Categories.Sports, ["YouTube"], "Champions")), "refinement of a named site needs no bounds");
        });

        Program.Add("Validation: invalid or orphan title regexes are errors", () =>
        {
            var c = Sample();
            HasError(WithSource(c, SiteSource("bad", Categories.Sports, ["YouTube"], "(unclosed")), ".match.titleRegex", "invalid regex");
            HasError(WithSource(c, SiteSource("alone", Categories.Sports, [], @"\bNBA\b")), ".match", "regex without site");
        });

        Program.Add("Validation: sources with identical criteria", () =>
        {
            var c = Sample();
            HasError(WithSource(c, SiteSource("yt2", Categories.Music, ["youtube"])), ".match", "same criteria, other category");
            var issues = ConfigValidator.Validate(WithSource(c, SiteSource("yt2", Categories.Video, ["YouTube"])));
            Assert.True(Errors(issues).Count == 0 && issues.Any(i => i.Level == IssueLevel.Warning), "same category only warns: " + Dump(issues));
            HasError(WithSource(c, SiteSource("youtube", Categories.Video, ["YouTube TV"])), ".id", "duplicate source id");
        });

        Program.Add("Validation: source kinds need their criteria", () =>
        {
            var c = Sample();
            HasError(WithSource(c, new SourceConfig("app", "App", SourceKind.App, new SourceMatch([], [], [], [], null), Categories.Music, false, "user")), ".match", "app");
            HasError(WithSource(c, new SourceConfig("game", "Game", SourceKind.Game, new SourceMatch([], [], [], [], null), Categories.GameFps, false, "user")), ".match", "game");
            HasError(WithSource(c, new SourceConfig("br", "Br", SourceKind.Browser, new SourceMatch([], [], [], [], null), Categories.Video, false, "user")), ".match", "browser");
            HasError(WithSource(c, SiteSource("site", Categories.Video, [])), ".match.site", "site");
        });

        Program.Add("Validation: timings out of range are errors", () =>
        {
            var c = Sample();
            HasError(c with { General = c.General with { StartupGraceSeconds = 61 } }, "general.startupGraceSeconds", "grace 61");
            HasError(c with { General = c.General with { CheckIntervalSeconds = 0 } }, "general.checkIntervalSeconds", "check 0");
            HasError(c with { General = c.General with { Detection = c.General.Detection with { PollMs = 10 } } }, "general.detection.pollMs", "poll 10");
            HasError(c with { General = c.General with { Detection = c.General.Detection with { PeakThreshold = 0 } } }, "general.detection.peakThreshold", "threshold 0");
            HasError(c with { General = c.General with { MinDwellMs = -1 } }, "general.minDwellMs", "dwell -1");
            HasError(c with { General = c.General with { LogLevel = "trace" } }, "general.logLevel", "log level");
            HasError(c with { Presets = new Dictionary<string, PresetMeta> { ["P"] = new(-1, null) } }, "volumeLeveling", "negative leveling");
            NoErrors(c with { General = c.General with { StartupGraceSeconds = 0 } }, "grace 0");
        });

        Program.Add("Validation: with FxSound status, missing presets are warnings, never errors", () =>
        {
            var c = Sample();
            var fx = new FxStatus("1.2.16.0", "G435 - General", true, false, 0, null,
                UserPresets: ["G435 - General", "g435 - video"], BuiltInPresets: ["General", "Music"], DateTimeOffset.Now);
            var issues = ConfigValidator.Validate(c, fx);
            Assert.True(Errors(issues).Count == 0, "no errors: " + Dump(issues));
            Assert.True(issues.Any(i => i.Level == IssueLevel.Warning && i.Path == "devices[1].defaultPreset"), "Realtek - General missing");
            Assert.True(issues.Any(i => i.Level == IssueLevel.Warning && i.Path == "assignments[0].preset" && i.Message.Contains("capitalisation", StringComparison.Ordinal)), "case differs");
            Assert.True(!issues.Any(i => i.Path == "devices[0].defaultPreset"), "existing preset is fine");
        });

        // ── edits ──
        Program.Add("ConfigEdits: rename moves settings, defaults and assignments", () =>
        {
            var c = Sample();
            var r = ConfigEdits.RenamePresetEverywhere(c, "G435 - Video", "G435 - Video Plus");
            Assert.True(!ConfigEdits.ReferencedPresets(r).Contains("G435 - Video"), "old name gone");
            Assert.Equal(1.5, r.Presets["G435 - Video Plus"].VolumeLeveling, "settings moved");
            Assert.Equal(2, r.Assignments.Count(a => a.Preset == "G435 - Video Plus"), "assignments renamed");
            var d = ConfigEdits.RenamePresetEverywhere(c, "G435 - General", "G435 - Base");
            Assert.Equal("G435 - Base", d.Devices[0].DefaultPreset, "default renamed");
            Assert.Equal("Realtek - General", d.Devices[1].DefaultPreset, "other default untouched");
            NoErrors(r, "renamed config");
            Assert.True(ReferenceEquals(c, ConfigEdits.RenamePresetEverywhere(c, "X", "X")), "no-op rename");
            Assert.True(c.Presets.ContainsKey("G435 - Video"), "input not mutated");
        });

        Program.Add("ConfigEdits: remove with replacement, remove dropping assignments, default needs replacement", () =>
        {
            var c = Sample();
            var r = ConfigEdits.RemovePreset(c, "G435 - Video", "G435 - General");
            Assert.True(!r.Presets.ContainsKey("G435 - Video"), "settings removed");
            Assert.Equal(3, r.Assignments.Count, "assignments kept");
            Assert.Equal(2, r.Assignments.Count(a => a.Preset == "G435 - General"), "moved to the replacement");
            var dropped = ConfigEdits.RemovePreset(c, "G435 - Video", null);
            Assert.Equal(1, dropped.Assignments.Count, "assignments dropped");
            NoErrors(dropped, "after drop");
            var threw = false;
            try { ConfigEdits.RemovePreset(c, "G435 - General", null); }
            catch (InvalidOperationException) { threw = true; }
            Assert.True(threw, "a device default cannot be dropped");
            var moved = ConfigEdits.RemovePreset(c, "G435 - General", "G435 - Video");
            Assert.Equal("G435 - Video", moved.Devices[0].DefaultPreset, "default moved");
        });

        // ── runtime state ──
        Program.Add("LearnedState: serialize → parse round-trip and learning", () =>
        {
            var s = LearnedStateJson.Learn(LearnedStateJson.Empty(), "g435", "{ep-1}");
            s = LearnedStateJson.Learn(s, "g435", "{ep-2}");
            s = LearnedStateJson.Learn(s, "realtek", "{ep-3}");
            s = s with { NotifiedUnknownDeviceKeys = ["usb:abc"], EditPendingPreset = "G435 - Music" };
            var (p, issues) = LearnedStateJson.Parse(LearnedStateJson.Serialize(s));
            Assert.Equal(0, issues.Count, "no issues");
            Assert.Equal("{ep-1}|{ep-2}", string.Join('|', p.EndpointIdsByDevice["G435"]), "device lookup case-insensitive, order kept");
            Assert.Equal("usb:abc", p.NotifiedUnknownDeviceKeys.Single(), "notified keys");
            Assert.Equal("G435 - Music", p.EditPendingPreset, "edit pending");

            var moved = LearnedStateJson.Learn(p, "realtek", "{EP-1}");
            Assert.Equal("{ep-2}", string.Join('|', moved.EndpointIdsByDevice["g435"]), "endpoint leaves the old device");
            Assert.Equal("{ep-3}|{EP-1}", string.Join('|', moved.EndpointIdsByDevice["realtek"]), "most recent last");
            Assert.True(ReferenceEquals(moved, LearnedStateJson.Learn(moved, "realtek", "{ep-1}")), "already learned = same instance");
            var many = LearnedStateJson.Empty();
            for (var i = 0; i < 20; i++) many = LearnedStateJson.Learn(many, "g435", $"{{e{i}}}");
            Assert.Equal(LearnedStateJson.MaxEndpointsPerDevice, many.EndpointIdsByDevice["g435"].Count, "capped");
            Assert.Equal("{e19}", many.EndpointIdsByDevice["g435"][^1], "keeps the newest");
        });

        Program.Add("LearnedState: user mode round-trip (Locked, Paused with end, Auto) and old files without it", () =>
        {
            var until = new DateTimeOffset(2026, 10, 26, 18, 30, 0, TimeSpan.FromHours(-4));
            foreach (var um in new SavedMode?[] { new(Mode.Locked, "G435 - Music", null), new(Mode.Paused, null, until), null })
            {
                var s = LearnedStateJson.Empty() with { EditPendingPreset = "G435 - FPS", UserMode = um };
                var (p, issues) = LearnedStateJson.Parse(LearnedStateJson.Serialize(s));
                Assert.Equal(0, issues.Count, "no issues for " + um);
                Assert.Equal(um, p.UserMode, "user mode kept");
                Assert.Equal("G435 - FPS", p.EditPendingPreset, "edit pending kept next to it");
            }
            var (paused, _) = LearnedStateJson.Parse(LearnedStateJson.Serialize(LearnedStateJson.Empty() with { UserMode = new(Mode.Paused, null, until) }));
            Assert.Equal(until.UtcTicks, paused.UserMode!.PausedUntil!.Value.UtcTicks, "pause end exact instant");

            var old = """{ "schemaVersion": 1, "endpointIdsByDevice": { "g435": ["{ep-1}"] }, "notifiedUnknownDeviceKeys": [], "editPendingPreset": null }""";
            var (o, oi) = LearnedStateJson.Parse(old);
            Assert.Equal(0, oi.Count, "old file: no issues");
            Assert.Null(o.UserMode, "old file: Auto");
            Assert.Equal("{ep-1}", o.EndpointIdsByDevice["g435"].Single(), "old file: endpoints read");

            foreach (var bad in new[] { "\"Locked\"", """{ "mode": "Sideways" }""", """{ "mode": "Locked", "lockedPreset": null }""",
                                        """{ "mode": "Paused", "pausedUntil": "tomorrow" }""", """{ "mode": "Editing" }""" })
            {
                var (b, bi) = LearnedStateJson.Parse("""{ "endpointIdsByDevice": { "g435": ["{ep-9}"] }, "userMode": """ + bad + " }");
                Assert.Null(b.UserMode, "invalid user mode ignored: " + bad);
                Assert.Equal(1, b.EndpointIdsByDevice.Count, "invalid user mode keeps the rest: " + bad);
                Assert.True(bi.Count == 1 && bi[0].Level == IssueLevel.Warning, "one warning: " + bad);
            }
            var (a, ai) = LearnedStateJson.Parse("""{ "userMode": { "mode": "Auto" } }""");
            Assert.True(a.UserMode is null && ai.Count == 0, "Auto = nothing saved");
        });

        Program.Add("LearnedState: corrupt file gives an empty state and a warning", () =>
        {
            foreach (var bad in new[] { "{ not json", "[1]", """{ "endpointIdsByDevice": { "g435": "x" } }""", """{ "schemaVersion": "one" }""" })
            {
                var (s, issues) = LearnedStateJson.Parse(bad);
                Assert.Equal(0, s.EndpointIdsByDevice.Count, "empty state for " + bad);
                Assert.True(issues.Count == 1 && issues[0].Level == IssueLevel.Warning, "one warning for " + bad);
            }
            var (e, ei) = LearnedStateJson.Parse("  ");
            Assert.Equal(0, ei.Count, "empty text is a first run");
            Assert.Null(e.EditPendingPreset, "empty");
        });

        Program.Add("LearnedState/AtomicFile: atomic save replaces the file, keeps a backup and leaves no .tmp", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "fxauto-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = Path.Combine(dir, LearnedStateJson.FileName);
                var (missing, mi) = LearnedStateJson.Load(path);
                Assert.True(missing.EndpointIdsByDevice.Count == 0 && mi.Count == 0, "missing file = empty, no warning");

                LearnedStateJson.Save(path, LearnedStateJson.Learn(LearnedStateJson.Empty(), "g435", "{a}"));
                LearnedStateJson.Save(path, LearnedStateJson.Learn(LearnedStateJson.Empty(), "g435", "{b}"));
                var (s, issues) = LearnedStateJson.Load(path);
                Assert.Equal(0, issues.Count, "no issues");
                Assert.Equal("{b}", s.EndpointIdsByDevice["g435"].Single(), "second save wins");
                Assert.True(!File.Exists(path + ".tmp"), "no .tmp left");

                var cfgPath = Path.Combine(dir, "config.json");
                AtomicFile.WriteAllText(cfgPath, "one", cfgPath + ".bak");
                AtomicFile.WriteAllText(cfgPath, "two", cfgPath + ".bak");
                Assert.Equal("two", File.ReadAllText(cfgPath), "replaced");
                Assert.Equal("one", File.ReadAllText(cfgPath + ".bak"), "backup of the previous save");

                File.WriteAllText(path, "{ broken");
                var (c, ci) = LearnedStateJson.Load(path);
                Assert.True(c.EndpointIdsByDevice.Count == 0 && ci.Single().Level == IssueLevel.Warning, "corrupt file on disk");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        });
    }
}
