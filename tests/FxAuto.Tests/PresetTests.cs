using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.FxSound;
using FxAuto.Core.Presets;

namespace FxAuto.Tests;

/// <summary>Preset resolution (REQ-106) and preset name rules (REQ-107).</summary>
internal static class PresetTests
{
    private static SourceClass Cls(string category, string? sourceId = null)
        => new(category, sourceId, null, "test", category.StartsWith("game-", StringComparison.Ordinal), false);

    private static FxAutoConfig Cfg()
    {
        var baseCfg = PresetOpsTests.Config(
            PresetOpsTests.Device("g435", "G435", "G435 - General", "G435 Wireless Gaming Headset"),
            PresetOpsTests.Device("realtek", "Realtek", "Realtek - General", "Realtek(R) Audio"));
        return baseCfg with
        {
            Presets = new Dictionary<string, PresetMeta>
            {
                ["G435 - General"] = new(1, null), ["G435 - Music"] = new(1.5, null), ["Shared - Movies"] = new(2.5, null),
                ["G435 - Spotify"] = new(0.5, null), ["Realtek - General"] = new(0, null),
            },
            Assignments =
            [
                new Assignment("g435", Categories.Music, null, "G435 - Music"),
                new Assignment("g435", null, "spotify", "G435 - Spotify"),
                new Assignment("g435", Categories.MovieOrSeries, null, "Shared - Movies"),
                new Assignment("realtek", Categories.MovieOrSeries, null, "Shared - Movies"),
            ],
        };
    }

    private static FxStatus Fx(params string[] user)
        => new("1.2.16.0", "General", true, false, 0, null, user, FxSoundSim.BuiltInNames, DateTimeOffset.Now);

    public static void Register()
    {
        Program.Add("resolver: source override > category > device default", () =>
        {
            var cfg = Cfg();
            var src = PresetResolver.Resolve(cfg, "g435", Cls(Categories.Music, "spotify"));
            Assert.Equal("G435 - Spotify", src.Preset, "source override");
            Assert.True(src.Why.StartsWith(PresetResolver.WhySource, StringComparison.Ordinal), "why " + src.Why);
            Assert.Equal(0.5, src.Leveling, "leveling of the preset");
            var cat = PresetResolver.Resolve(cfg, "g435", Cls(Categories.Music, "chrome"));
            Assert.Equal("G435 - Music", cat.Preset, "category");
            Assert.Equal(1.5, cat.Leveling, "leveling");
            var def = PresetResolver.Resolve(cfg, "g435", Cls(Categories.GameFps));
            Assert.Equal("G435 - General", def.Preset, "device default");
            Assert.Equal(PresetResolver.WhyDefault, def.Why, "why");
            Assert.Equal("G435 - General", PresetResolver.Resolve(cfg, "g435", null).Preset, "nothing playing → default");
        });

        Program.Add("resolver: the same preset on two devices; per-device category", () =>
        {
            var cfg = Cfg();
            Assert.Equal("Shared - Movies", PresetResolver.Resolve(cfg, "g435", Cls(Categories.MovieOrSeries)).Preset, "g435");
            Assert.Equal("Shared - Movies", PresetResolver.Resolve(cfg, "realtek", Cls(Categories.MovieOrSeries)).Preset, "realtek");
            Assert.Equal("Realtek - General", PresetResolver.Resolve(cfg, "realtek", Cls(Categories.Music)).Preset, "realtek has no music");
        });

        Program.Add("resolver: unknown / disabled device or hold → no change", () =>
        {
            var cfg = Cfg();
            Assert.Null(PresetResolver.Resolve(cfg, null, Cls(Categories.Music)).Preset, "unknown device");
            Assert.Null(PresetResolver.Resolve(cfg, "nope", Cls(Categories.Music)).Preset, "not configured");
            var disabled = cfg with { Devices = [.. cfg.Devices.Select(d => d with { Enabled = d.Id != "g435" })] };
            Assert.Null(PresetResolver.Resolve(disabled, "g435", Cls(Categories.Music)).Preset, "disabled");
            Assert.Null(PresetResolver.Resolve(cfg, "g435", Cls(Categories.Hold)).Preset, "hold");
        });

        Program.Add("resolver: preset missing in FxSound → never sent; falls back to the device default", () =>
        {
            var cfg = Cfg();
            var r = PresetResolver.Resolve(cfg, "g435", Cls(Categories.Music), Fx("G435 - General"));
            Assert.Equal("G435 - General", r.Preset, "device default");
            Assert.True(r.Why.Contains("missing in FxSound", StringComparison.Ordinal), "warning: " + r.Why);
            var none = PresetResolver.Resolve(cfg, "g435", Cls(Categories.Music), Fx());
            Assert.Null(none.Preset, "default missing too → nothing");
            Assert.Equal("G435 - Music", PresetResolver.Resolve(cfg, "g435", Cls(Categories.Music), Fx("G435 - Music")).Preset, "present");
        });

        Program.Add("name rules: Compose and the 64-character limit", () =>
        {
            Assert.Equal("G435 - Music", PresetNameRules.Compose(" G435 ", "Music"), "compose");
            var st = Fx("G435 - Music");
            Assert.True(PresetNameRules.Validate(new string('a', 64), st, 120).Ok, "64 ok");
            var long65 = PresetNameRules.Validate(new string('a', 65), st, 120);
            Assert.True(!long65.Ok, "65 refused");
            Assert.Equal(64, long65.Sanitized.Length, "sanitized cut to 64");
            Assert.True(PresetNameRules.Validate("Realtek - Sports TV (night) #2", st, 120).Ok, "allowed punctuation");
        });

        Program.Add("name rules: forbidden characters, non-ASCII, edges", () =>
        {
            var st = Fx();
            foreach (var c in PresetNameRules.Forbidden)
            {
                var r = PresetNameRules.Validate($"G435 {c} Music", st, 120);
                Assert.True(!r.Ok, "forbidden " + c);
                Assert.True(!r.Sanitized.Contains(c, StringComparison.Ordinal), "sanitized without " + c);
            }
            Assert.True(!PresetNameRules.Validate("G435 - Música", st, 120).Ok, "non-ASCII refused");
            Assert.True(!PresetNameRules.Validate("G435 - Music.", st, 120).Ok, "trailing dot refused");
            Assert.True(!PresetNameRules.Validate(" G435", st, 120).Ok, "leading space refused");
            Assert.True(!PresetNameRules.Validate("", st, 120).Ok, "empty refused");
            Assert.True(!PresetNameRules.Validate("'G435'", st, 120).Ok, "quotes at the edges refused");
        });

        Program.Add("name rules: unique ignoring case against the 13 built-ins and user presets", () =>
        {
            var st = Fx("G435 - Music");
            Assert.Equal(13, FxSoundSim.BuiltInNames.Length, "13 built-ins");
            foreach (var b in FxSoundSim.BuiltInNames)
            {
                var r = PresetNameRules.Validate(b.ToUpperInvariant(), st, 120);
                Assert.True(!r.Ok && r.Error!.Contains("built-in", StringComparison.Ordinal), "clash with " + b);
            }
            Assert.True(!PresetNameRules.Validate("g435 - music", st, 120).Ok, "user clash ignoring case");
            Assert.True(PresetNameRules.Validate("G435 - Musics", st, 120).Ok, "different name ok");
        });

        Program.Add("name rules: user preset limit (120) applies to new names, not to renames", () =>
        {
            var many = Enumerable.Range(0, 120).Select(i => $"P{i}").ToArray();
            var st = Fx(many);
            var r = PresetNameRules.Validate("New One", st, PresetNameRules.DefaultMaxUserPresets);
            Assert.True(!r.Ok && r.Error!.Contains("120", StringComparison.Ordinal), "limit: " + r.Error);
            Assert.True(PresetNameRules.Validate("New One", st, 120, renamingFrom: "P1").Ok, "rename allowed at the limit");
            Assert.True(!PresetNameRules.Validate("P1", st, 120, renamingFrom: "P1").Ok, "same name refused");
            Assert.True(!PresetNameRules.Validate("p1", st, 120, renamingFrom: "P1").Ok, "case-only rename refused");
        });
    }
}
