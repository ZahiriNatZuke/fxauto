using System.Text.RegularExpressions;
using FxAuto.Core;
using FxAuto.Core.Arbitration;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Devices;
using FxAuto.Core.Localization;
using FxAuto.Core.Presets;

namespace FxAuto.Tests;

/// <summary>UI text catalog (English/Spanish), language and theme choices. The JSONL log keeps English.</summary>
internal static class LocalizationTests
{
    public static void Register()
    {
        Program.Add("Localization: English and Spanish catalogs have the same keys", () =>
        {
            var onlyEn = Catalog.English.Keys.Except(Catalog.Spanish.Keys).ToList();
            var onlyEs = Catalog.Spanish.Keys.Except(Catalog.English.Keys).ToList();
            Assert.True(onlyEn.Count == 0, "missing in Spanish: " + string.Join(", ", onlyEn.Take(10)));
            Assert.True(onlyEs.Count == 0, "missing in English: " + string.Join(", ", onlyEs.Take(10)));
        });

        Program.Add("Localization: both languages keep the same placeholders and format cleanly", () =>
        {
            var holes = new Regex(@"\{(\d+)(?::[^}]*)?\}", RegexOptions.CultureInvariant);
            foreach (var (key, en) in Catalog.English)
            {
                var es = Catalog.Spanish[key];
                var a = holes.Matches(en).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal);
                var b = holes.Matches(es).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal);
                Assert.True(a.SequenceEqual(b), $"{key}: placeholders differ (\"{en}\" / \"{es}\")");
                var args = Enumerable.Range(0, 6).Select(i => (object)("x" + i)).ToArray();
                try
                {
                    _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, en, args);
                    _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, es, args);
                }
                catch (FormatException ex)
                {
                    throw new AssertException($"{key}: bad format string: {ex.Message}");
                }
                Assert.True(es.Length > 0 || en.Length == 0, key + ": empty Spanish text");
            }
        });

        Program.Add("Localization: every key the code asks for exists (static keys and key families)", () =>
        {
            var root = RepoRoot();
            // any literal shaped like a catalog key ("Prefix.Rest" with a prefix the catalog uses)
            var prefixes = Catalog.English.Keys.Select(k => k[..k.IndexOf('.', StringComparison.Ordinal)]).Distinct().ToList();
            var keyUse = new Regex(@"""((?:" + string.Join("|", prefixes.Select(Regex.Escape)) + @")\.[^""\\]+)""", RegexOptions.CultureInvariant);
            var missing = new List<string>();
            foreach (var file in SourceFiles(root))
            {
                foreach (Match m in keyUse.Matches(File.ReadAllText(file)))
                {
                    var key = m.Groups[1].Value;
                    if (key.EndsWith('.')) continue; // a prefix completed at run time (checked below)
                    if (!Catalog.English.ContainsKey(key)) missing.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
            // families built at run time
            foreach (var c in Categories.All) Need("Category." + c);
            foreach (var l in Enum.GetValues<WinLevel>()) Need("Why.Level." + l);
            foreach (var f in Enum.GetValues<FormFactor>()) Need("FormFactor." + f);
            foreach (var k in Enum.GetValues<SourceKind>()) Need("Kind." + k);
            foreach (var l in new[] { "Error", "Warning", "Note" })
            {
                Need("Status.Issue." + l);
                Need("Status.IssueDraft." + l);
                Need("Status.Level." + l);
            }
            Assert.True(missing.Count == 0, "keys not in the catalog: " + string.Join("; ", missing.Take(15)));

            void Need(string key)
            {
                if (!Catalog.English.ContainsKey(key)) missing.Add("family: " + key);
            }
        });

        Program.Add("Localization: no visible UI text skips the catalog (settings window, dialogs, tray)", () =>
        {
            var root = RepoRoot();
            // Literal text handed to the usual UI sinks. Brand names, units and the language names are allowed.
            var sinks = new Regex(
                @"(?:\b(?:Text|AccessibleName|AccessibleDescription|PlaceholderText|Title|Message)\s*=\s*\$?""(?<t>[^""]*)"")" +
                @"|(?:\b(?:Ui\.(?:Button|Text|Hint|SectionTitle)|SetToolTip\(\w+,|ToolStripMenuItem|ShowMessage\(""\w+"",\s*Tone\.\w+,|Confirm|AddRow|AddAction)\(\s*\$?""(?<t>[^""]*)"")" +
                @"|(?:AddField\(\w+,\s*\$?""(?<t>[^""]*)"")" +
                @"|(?:\b(?:Heading|Subheading)\s*=>\s*\$?""(?<t>[^""]*)"")" +
                @"|(?:SetColumns\(\(\s*""(?<t>[^""]*)"")" +
                @"|(?:new Ui\.Choice\([^,]+,\s*\$?""(?<t>[^""]*)"")",
                RegexOptions.CultureInvariant);
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "", "FxAuto", "FxSound", ".NET", "dB", "English", "Español", "—" };
            var offenders = new List<string>();
            var ui = Path.Combine(root, "src", "FxAuto", "Ui");
            foreach (var file in Directory.GetFiles(ui, "*.cs", SearchOption.AllDirectories))
            {
                if (file.EndsWith("FakeSettingsHost.cs", StringComparison.Ordinal)) continue; // debug-only sample data
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].TrimStart();
                    if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                    foreach (Match m in sinks.Matches(lines[i]))
                    {
                        var t = m.Groups["t"].Value;
                        if (allowed.Contains(t) || !t.Any(char.IsLetter) || t.StartsWith('{')) continue;
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: \"{t}\"");
                    }
                }
            }
            Assert.True(offenders.Count == 0, "literal UI text: " + string.Join("; ", offenders.Take(15)));
        });

        Program.Add("Localization: \"system\" follows the Windows display language (es-* → Spanish, else English)", () =>
        {
            Assert.Equal("es", Loc.Resolve("system", "es-ES"), "es-ES");
            Assert.Equal("es", Loc.Resolve("system", "es-419"), "es-419");
            Assert.Equal("es", Loc.Resolve("system", "es-CU"), "es-CU");
            Assert.Equal("es", Loc.Resolve(null, "es"), "missing setting, es");
            Assert.Equal("en", Loc.Resolve("system", "en-US"), "en-US");
            Assert.Equal("en", Loc.Resolve("system", "fr-FR"), "fr-FR");
            Assert.Equal("en", Loc.Resolve("system", "estonian-made-up"), "es prefix without dash is not Spanish");
            Assert.Equal("en", Loc.Resolve("system", ""), "invariant");
            Assert.Equal("en", Loc.Resolve("en", "es-ES"), "explicit English wins");
            Assert.Equal("es", Loc.Resolve("es", "en-US"), "explicit Spanish wins");
            Assert.Equal("es", Loc.Resolve("klingon", "es-MX"), "unknown value behaves like system");
        });

        Program.Add("Localization: theme choice resolves to light or dark", () =>
        {
            Assert.True(!UiChoices.ResolveDark("light", windowsAppsDark: true), "light");
            Assert.True(UiChoices.ResolveDark("dark", windowsAppsDark: false), "dark");
            Assert.True(UiChoices.ResolveDark("system", windowsAppsDark: true), "system, Windows dark");
            Assert.True(!UiChoices.ResolveDark("system", windowsAppsDark: false), "system, Windows light");
            Assert.True(UiChoices.ResolveDark(null, windowsAppsDark: true), "missing = system");
            Assert.True(!UiChoices.ResolveDark("purple", windowsAppsDark: false), "unknown = system");
        });

        Program.Add("Localization: config without language/theme is valid and reads as \"system\"", () =>
        {
            var (c, issues) = ConfigJson.Parse("""{ "schemaVersion": 2, "general": { "logLevel": "info" } }""");
            var cfg = Assert.NotNull(c, "parsed");
            Assert.Equal("system", cfg.General.Language, "language default");
            Assert.Equal("system", cfg.General.Theme, "theme default");
            Assert.True(!issues.Any(i => i.Path.Contains("language", StringComparison.Ordinal) || i.Path.Contains("theme", StringComparison.Ordinal)),
                "no issue about the absent keys");
            Assert.True(!ConfigValidator.Validate(DefaultConfig.Create()).Any(i => i.Level == IssueLevel.Error), "defaults validate");
        });

        Program.Add("Localization: config with language/theme reads, round-trips and rejects unknown values softly", () =>
        {
            var (c, issues) = ConfigJson.Parse("""{ "schemaVersion": 2, "general": { "language": "es", "theme": "dark" } }""");
            var cfg = Assert.NotNull(c, "parsed");
            Assert.Equal("es", cfg.General.Language, "language");
            Assert.Equal("dark", cfg.General.Theme, "theme");
            Assert.True(!issues.Any(i => i.Message.Contains("Unknown key", StringComparison.Ordinal)), "known keys");
            var json = ConfigJson.Serialize(cfg);
            Assert.True(json.Contains("\"language\": \"es\"", StringComparison.Ordinal) && json.Contains("\"theme\": \"dark\"", StringComparison.Ordinal), "written back");
            var (again, _) = ConfigJson.Parse(json);
            Assert.True(again!.General.Language == "es" && again.General.Theme == "dark", "round trip");

            var odd = cfg with { General = cfg.General with { Language = "fr", Theme = "sepia" } };
            var v = ConfigValidator.Validate(odd);
            Assert.True(v.Any(i => i.Level == IssueLevel.Warning && i.Path == "general.language"), "unknown language warns");
            Assert.True(v.Any(i => i.Level == IssueLevel.Warning && i.Path == "general.theme"), "unknown theme warns");
            Assert.True(!v.Any(i => i.Level == IssueLevel.Error && i.Path.StartsWith("general.", StringComparison.Ordinal)), "and does not block saving");
        });

        Program.Add("Localization: the UI renders reasons in Spanish while the log text stays English", () =>
        {
            try
            {
                Loc.SetLanguage(Loc.Spanish);
                var cfg = DefaultConfig.Create() with
                {
                    Devices = [new DeviceConfig("desk", "Desk speakers", "Desk", new DeviceMatchSpec(["Realtek"], null, null, [], null), "General", true, null)],
                    Assignments = [new Assignment("desk", Categories.Music, null, "Desk - Music")],
                };
                var res = PresetResolver.Resolve(cfg, "desk", new SourceClass(Categories.Music, null, null, "app exe", false, false));
                Assert.Equal("Desk - Music", res.Preset, "preset");
                Assert.Equal("category (music)", res.Why, "log text stays English");
                Assert.Equal("preset de la categoría Música", Loc.Render(Assert.NotNull(res.WhyText, "structured reason")), "UI text is Spanish");

                var issue = ConfigIssue.Of(IssueLevel.Warning, "x", "Issue.Validator.PresetMissing", "G435 - Music");
                Assert.Equal("Preset \"G435 - Music\" does not exist in FxSound; FxAuto will not send it.", issue.Message, "issue log text");
                Assert.True(issue.Shown.StartsWith("El preset «G435 - Music»", StringComparison.Ordinal), "issue UI text: " + issue.Shown);

                Assert.Equal("«G435 - Music» no existe en FxSound.", Loc.Known("\"G435 - Music\" does not exist in FxSound."), "known preset-op error");
                Assert.Equal("some unknown text", Loc.Known("some unknown text"), "unknown text stays as is");
                Assert.Equal("Juego: FPS", Categories.DisplayName(Categories.GameFps), "category name");
                Assert.Equal("G435 - FPS", PresetNameRules.Compose("G435", Categories.PresetLabel(Categories.GameFps)), "preset names stay English");
                Assert.Equal("app: exe", Loc.How("app exe"), "how (no translation needed)");
                Assert.Equal("navegador por defecto", Loc.How("browser default"), "how");
            }
            finally
            {
                Loc.SetLanguage(Loc.English);
            }
            Assert.Equal("Game: FPS", Categories.DisplayName(Categories.GameFps), "back to English");
        });
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FxAuto.sln"))) dir = dir.Parent;
        if (dir is null) throw new SkipException("repository not found from " + AppContext.BaseDirectory);
        return dir.FullName;
    }

    private static IEnumerable<string> SourceFiles(string root)
        => Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
