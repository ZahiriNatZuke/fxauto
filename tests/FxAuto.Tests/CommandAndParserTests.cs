using System.Globalization;
using FxAuto.Core;
using FxAuto.Core.FxSound;

namespace FxAuto.Tests;

internal static class CommandAndParserTests
{
    private static readonly DateTimeOffset W = new(2026, 10, 25, 15, 39, 0, TimeSpan.Zero);

    public static void Register()
    {
        Program.Add("FxCommand: spaces, quotes and es-ES culture active", () =>
        {
            var prev = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("es-ES");
                Assert.Equal("1,5", 1.5.ToString(CultureInfo.CurrentCulture), "es-ES culture is active");
                Assert.Equal("--power=1 --preset=\"G435_Video\" --volume_leveling=1.5 --run_minimized",
                    FxCommand.Apply("G435_Video", 1.5), "command with invariant leveling");
                Assert.Equal("--power=1 --preset=\"Bass Boost\" --volume_leveling=0 --run_minimized",
                    FxCommand.Apply("Bass Boost", 0), "space inside quotes");
                Assert.Equal("\"a \\\"b\\\" c\"", FxCommand.Quote("a \"b\" c"), "escaped quotes");
                Assert.Equal("\"C:\\dir\\\\\"", FxCommand.Quote("C:\\dir\\"), "trailing backslash doubled, inner one intact");
                Assert.Equal("\"a\\b\"", FxCommand.Quote("a\\b"), "inner backslash untouched");
                Assert.Equal("0.333", FxCommand.FormatNumber(1.0 / 3), "rounded to 3 decimals");
                Assert.Equal("--status", FxCommand.Status, "status");
            }
            finally
            {
                CultureInfo.CurrentCulture = prev;
            }
        });

        Program.Add("status.json: real sample (FxSound 1.2.16.0)", () =>
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "status.real.json"));
            var st = Assert.NotNull(FxStatusParser.TryParse(json, W), "real sample");
            Assert.Equal("1.2.16.0", st.Version, "version");
            Assert.True(st.Power, "power");
            Assert.Equal("G435_General", st.SelectedPreset, "selected preset");
            Assert.Equal(false, st.SelectedModified, "not modified");
            Assert.Equal(0.0, st.VolumeLeveling, "leveling");
            Assert.True(st.SelectedOutput!.Contains("G435", StringComparison.Ordinal), "G435 output (\\u00ed escapes)");
            Assert.True(st.SelectedOutput.Contains("Aud\u00edfono", StringComparison.Ordinal), "escapes decoded");
            Assert.Equal(16, st.UserPresets.Count(p => p.StartsWith("G435_", StringComparison.Ordinal)), "16 G435 presets");
            Assert.True(st.BuiltInPresets.Contains("General") && st.BuiltInPresets.Contains("Bass Boost"), "built-in");
            Assert.Equal(W, st.FileWrittenAt, "file time");
        });

        Program.Add("status.json: truncated, empty or odd → null or tolerant", () =>
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "status.real.json"));
            foreach (var cut in new[] { 1, 10, json.Length / 3, json.Length / 2, json.Length - 2 })
                Assert.Null(FxStatusParser.TryParse(json[..cut], W), $"truncated at {cut}");
            Assert.Null(FxStatusParser.TryParse("", W), "empty");
            Assert.Null(FxStatusParser.TryParse("[]", W), "list root");
            var odd = FxStatusParser.TryParse("""{"power":1,"selected_preset":"X","presets":{"user_defined":["X",{"name":"Y","modified":true}]}}""", W);
            Assert.True(odd is { Power: true, SelectedPreset: "X", SelectedModified: null, VolumeLeveling: null }, "odd fields tolerated");
            Assert.Equal(2, odd!.UserPresets.Count, "mixed list");
            var mod = FxStatusParser.TryParse("""{"power":true,"selected_preset":"Y","presets":{"user_defined":[{"name":"Y","modified":true}]}}""", W);
            Assert.Equal(true, mod!.SelectedModified, "modified");
        });

        Program.Add("FxCommand preset operation builders: quoted names, invariant numbers, one operation per call", () =>
        {
            var prev = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("es-ES");
                Assert.Equal("--preset=\"G435 - Music\" --run_minimized", FxCommand.SelectPreset("G435 - Music"), "select");
                Assert.Equal("--output=\"Altavoces (Realtek(R) Audio)\" --run_minimized", FxCommand.Output("Altavoces (Realtek(R) Audio)"), "output");
                Assert.Equal("--set_effect=clarity:1.023622 --run_minimized", FxCommand.SetEffect("clarity", 1.023622035980225), "set effect");
                Assert.Equal("--save_preset=\"Realtek - Music\" --run_minimized", FxCommand.SavePreset("Realtek - Music"), "save");
                Assert.Equal("--rename_preset=\"A \\\"B\\\"\" --run_minimized", FxCommand.RenamePreset("A \"B\""), "rename quoted");
                Assert.Equal("--delete_preset --run_minimized", FxCommand.DeletePreset(), "delete");
                Assert.Equal("--overwrite_preset --run_minimized", FxCommand.OverwritePreset(), "overwrite");
                Assert.Equal("--undo_preset --run_minimized", FxCommand.UndoPreset(), "undo");
                Assert.Equal("--view=2", FxCommand.View(2), "view");
                var edit = FxCommand.EditInFxSound("G435 - Music");
                Assert.Equal("--preset=\"G435 - Music\" --view=2", edit, "edit");
                Assert.True(!edit.Contains("--run_minimized", StringComparison.Ordinal), "edit shows the window");
                var threw = false;
                try
                {
                    FxCommand.SetEffect("clarity --power=0", 1);
                }
                catch (ArgumentException)
                {
                    threw = true;
                }
                Assert.True(threw, "effect name injection refused");
            }
            finally
            {
                CultureInfo.CurrentCulture = prev;
            }
        });

        Program.Add("status.json extra fields: output_devices, modified map, DSP (real file with two outputs)", () =>
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "status.two-outputs.json"));
            var st = Assert.NotNull(FxStatusParser.TryParse(json, W), "parse");
            Assert.Equal(2, st.OutputDevices.Count, "two outputs");
            Assert.Equal("Audífono de los auriculares con micrófono (G435 Wireless Gaming Headset)", st.OutputDevices[0], "exact name");
            Assert.Equal("Altavoces (Realtek(R) Audio)", st.OutputDevices[1], "realtek");
            Assert.Equal(st.BuiltInPresets.Count + st.UserPresets.Count, st.ModifiedByPreset.Count, "modified flag for every preset");
            Assert.True(st.ModifiedByPreset.Values.All(m => !m), "none modified");
            var dsp = Assert.NotNull(st.Dsp, "dsp");
            Assert.Equal(10, dsp.Bands.Count, "10 bands");
            Assert.Equal(-2.0, dsp.Bands[0], "band 0 gain");
            Assert.Equal(-2.0, dsp.MasterGain, "master gain");
            Assert.True(Math.Abs(dsp.Effects["clarity"] - 1.023622035980225) < 1e-9, "clarity");
            Assert.Equal(5, dsp.Effects.Count, "five effects");

            var realJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "status.real.json"));
            var real = Assert.NotNull(FxStatusParser.TryParse(realJson, W), "real");
            Assert.Equal(1.0, real.Dsp!.Bands[0], "first fixture band 0");
            var bare = Assert.NotNull(FxStatusParser.TryParse("""{"power":true,"presets":{"user_defined":[{"name":"Y","modified":true},{"name":"Z"}]}}""", W), "bare");
            Assert.Null(bare.Dsp, "no DSP");
            Assert.Equal(0, bare.OutputDevices.Count, "no outputs");
            Assert.True(bare.ModifiedByPreset["Y"] && !bare.ModifiedByPreset.ContainsKey("Z"), "modified map");
        });

        Program.Add("LogPolicy: daily name and 7-day rotation", () =>
        {
            var now = new DateTimeOffset(2026, 10, 25, 23, 30, 0, TimeSpan.FromHours(-4));
            Assert.Equal("fxauto-2026-10-25.jsonl", LogPolicy.FileName(now), "name with the local date");
            var files = new[]
            {
                @"C:\L\fxauto-2026-10-25.jsonl", @"C:\L\fxauto-2026-10-19.jsonl", @"C:\L\fxauto-2026-10-18.jsonl",
                @"C:\L\fxauto-2026-09-01.jsonl", @"C:\L\other.txt", @"C:\L\fxauto-junk.jsonl", @"C:\L\FXAUTO-2026-10-01.JSONL",
            };
            var expired = LogPolicy.Expired(files, now).ToList();
            Assert.Equal(3, expired.Count, "expired: " + string.Join(", ", expired));
            Assert.True(expired.Contains(@"C:\L\fxauto-2026-10-18.jsonl") && expired.Contains(@"C:\L\fxauto-2026-09-01.jsonl")
                        && expired.Contains(@"C:\L\FXAUTO-2026-10-01.JSONL"), "those 7+ days old");
            Assert.True(!expired.Contains(@"C:\L\fxauto-2026-10-19.jsonl"), "the 6-day-old one is kept");
        });
    }
}
