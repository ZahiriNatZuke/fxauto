using System.Text.RegularExpressions;
using FxAuto.Core.Classification;
using FxAuto.Core.FxSound;
using FxAuto.Core.Localization;

namespace FxAuto.Core.Config;

/// <summary>Validation of a config (REQ-109, research 03 §6.1): errors block saving, warnings are allowed, info is
/// shown only. With <paramref name="fx"/>, preset names are also checked against FxSound's lists.</summary>
public static class ConfigValidator
{
    /// <summary>Regex timeout used everywhere for title regexes.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Accepted values of <see cref="GeneralSettings.LogLevel"/>.</summary>
    public static readonly IReadOnlyList<string> LogLevels = ["info", "debug"];

    /// <summary>Accepted values of <see cref="GeneralSettings.UnknownDevice"/>.</summary>
    public static readonly IReadOnlyList<string> UnknownDeviceModes = ["keepCurrent"];

    /// <summary>Device tag rule: 1–20 ASCII characters among letters, digits, space, '_', '.', '-'.</summary>
    public const int MaxTagLength = 20;
    private static readonly Regex TagPattern = new(@"^[A-Za-z0-9 _.\-]{1,20}$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly string[] Origins = [DefaultConfig.OriginBuiltIn, DefaultConfig.OriginUser, DefaultConfig.OriginMigrated];

    public static IReadOnlyList<ConfigIssue> Validate(FxAutoConfig c, FxStatus? fx = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var issues = new List<ConfigIssue>();
        if (c.SchemaVersion > ConfigJson.CurrentSchemaVersion)
            issues.Add(ConfigIssue.Of(IssueLevel.Error, "schemaVersion",
                "Issue.Validator.01", c.SchemaVersion));
        else if (c.SchemaVersion != ConfigJson.CurrentSchemaVersion)
            issues.Add(ConfigIssue.Of(IssueLevel.Error, "schemaVersion", "Issue.Validator.02", c.SchemaVersion, ConfigJson.CurrentSchemaVersion));

        ValidateGeneral(c.General, issues);
        ValidatePresets(c, issues);
        ValidateDevices(c, issues);
        ValidateSources(c, issues);
        ValidateAssignments(c, issues);
        ValidateNameList(c.IgnoredApps, "ignoredApps", issues);
        ValidateNameList(c.Launchers, "launchers", issues);
        ValidateNameList(c.GameFolderExclusions, "gameFolderExclusions", issues);
        if (fx is not null) ValidateAgainstFxSound(c, fx, issues);
        return issues;
    }

    // ── general ──

    private static void Range(List<ConfigIssue> issues, string path, double value, double min, double max, string unit = "")
    {
        if (double.IsNaN(value) || value < min || value > max)
            issues.Add(ConfigIssue.Of(IssueLevel.Error, path, "Issue.Validator.03", value, unit, min, max, unit));
    }

    private static void ValidateGeneral(GeneralSettings g, List<ConfigIssue> issues)
    {
        const string P = "general.";
        if (g is null)
        {
            issues.Add(ConfigIssue.Of(IssueLevel.Error, "general", "Issue.Validator.04"));
            return;
        }
        if (!LogLevels.Contains(g.LogLevel, StringComparer.Ordinal))
            issues.Add(ConfigIssue.Of(IssueLevel.Error, P + "logLevel", "Issue.Validator.05", g.LogLevel, string.Join(", ", LogLevels)));
        if (!UiChoices.Languages.Contains(g.Language, StringComparer.Ordinal))
            issues.Add(ConfigIssue.Of(IssueLevel.Warning, P + "language", "Issue.Validator.Language", g.Language, string.Join(", ", UiChoices.Languages)));
        if (!UiChoices.Themes.Contains(g.Theme, StringComparer.Ordinal))
            issues.Add(ConfigIssue.Of(IssueLevel.Warning, P + "theme", "Issue.Validator.Theme", g.Theme, string.Join(", ", UiChoices.Themes)));
        // general.updates (optional): both values are booleans, so the reader already reports wrong types
        // (Issue.Json.E09) and unknown keys (Issue.Json.03); an absent object is valid and means the defaults.
        if (!UnknownDeviceModes.Contains(g.UnknownDevice, StringComparer.Ordinal))
            issues.Add(ConfigIssue.Of(IssueLevel.Error, P + "unknownDevice", "Issue.Validator.06", g.UnknownDevice, string.Join(", ", UnknownDeviceModes)));
        if (g.FxSoundPath is not null && string.IsNullOrWhiteSpace(g.FxSoundPath))
            issues.Add(ConfigIssue.Of(IssueLevel.Warning, P + "fxSoundPath", "Issue.Validator.07"));

        Range(issues, P + "startupGraceSeconds", g.StartupGraceSeconds, 0, 60, " s");
        Range(issues, P + "checkIntervalSeconds", g.CheckIntervalSeconds, 1, 3600, " s");
        Range(issues, P + "idleGraceSeconds", g.IdleGraceSeconds, 0, 3600, " s");
        Range(issues, P + "minDwellMs", g.MinDwellMs, 0, 60_000, " ms");
        Range(issues, P + "gameLoadingMaxMs", g.GameLoadingMaxMs, 0, 600_000, " ms");

        var t = g.Detection;
        if (t is null)
        {
            issues.Add(ConfigIssue.Of(IssueLevel.Error, P + "detection", "Issue.Validator.08"));
        }
        else
        {
            const string D = P + "detection.";
            Range(issues, D + "pollMs", t.PollMs, 50, 2000, " ms");
            Range(issues, D + "peakThreshold", t.PeakThreshold, 0.0001, 0.5);
            Range(issues, D + "attackRatio", t.AttackRatio, 0.05, 1);
            Range(issues, D + "attackMs", t.AttackMs, 0, 10_000, " ms");
            Range(issues, D + "burstIgnoreMs", t.BurstIgnoreMs, 0, 30_000, " ms");
            Range(issues, D + "releaseMs", t.ReleaseMs, 500, 120_000, " ms");
            Range(issues, D + "gameReleaseMs", t.GameReleaseMs, 500, 300_000, " ms");
            if (t.GameReleaseMs < t.ReleaseMs)
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, D + "gameReleaseMs", "Issue.Validator.09"));
            if (t.AttackMs < t.PollMs)
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, D + "attackMs", "Issue.Validator.10"));
        }

        if (g.AudibleTabMarkers is null || g.AudibleTabMarkers.Count == 0)
        {
            if (g.ReadBrowserTabs)
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, P + "audibleTabMarkers", "Issue.Validator.11"));
        }
        else
        {
            for (var i = 0; i < g.AudibleTabMarkers.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(g.AudibleTabMarkers[i]))
                    issues.Add(ConfigIssue.Of(IssueLevel.Error, $"{P}audibleTabMarkers[{i}]", "Issue.Validator.12"));
            }
        }
    }

    // ── presets ──

    private static void ValidatePresets(FxAutoConfig c, List<ConfigIssue> issues)
    {
        foreach (var (name, meta) in c.Presets)
        {
            var path = $"presets[\"{name}\"]";
            ConfigText.CheckPresetName(name, path, issues);
            if (meta is null) continue;
            if (double.IsNaN(meta.VolumeLeveling) || meta.VolumeLeveling < 0)
                issues.Add(ConfigIssue.Of(IssueLevel.Error, path + ".volumeLeveling", "Issue.Validator.13"));
        }
    }

    // ── devices ──

    private static void ValidateDevices(FxAutoConfig c, List<ConfigIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < c.Devices.Count; i++)
        {
            var d = c.Devices[i];
            var p = $"devices[{i}]";
            CheckId(d.Id, p + ".id", "device", ids, issues);
            if (string.IsNullOrWhiteSpace(d.DisplayName))
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, p + ".displayName", "Issue.Validator.14"));

            if (string.IsNullOrEmpty(d.Tag) || !TagPattern.IsMatch(d.Tag) || d.Tag != d.Tag.Trim())
                issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".tag",
                    "Issue.Validator.15", d.Tag, MaxTagLength));
            else if (tags.TryGetValue(d.Tag, out var other))
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, p + ".tag", "Issue.Validator.16", d.Tag, other));
            else
                tags[d.Tag] = d.Id;

            if (string.IsNullOrWhiteSpace(d.DefaultPreset))
            {
                issues.Add(new(d.Enabled ? IssueLevel.Error : IssueLevel.Warning, p + ".defaultPreset",
                    "A managed device needs a default preset."));
            }
            else
            {
                ConfigText.CheckPresetName(d.DefaultPreset, p + ".defaultPreset", issues);
            }

            var m = d.Match;
            if (m is null || (m.Adapters.All(string.IsNullOrWhiteSpace) && m.PinnedEndpointIds.Count == 0))
                issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".match", "Issue.Validator.17"));
            else
            {
                for (var k = 0; k < m.Adapters.Count; k++)
                {
                    if (string.IsNullOrWhiteSpace(m.Adapters[k]))
                        issues.Add(ConfigIssue.Of(IssueLevel.Error, $"{p}.match.adapters[{k}]", "Issue.Validator.18"));
                }
            }
        }

        // Two managed devices that match the same output.
        var enabled = c.Devices.Select((d, i) => (d, i)).Where(x => x.d.Enabled && x.d.Match is not null).ToList();
        for (var a = 0; a < enabled.Count; a++)
        {
            for (var b = a + 1; b < enabled.Count; b++)
            {
                var (da, ia) = enabled[a];
                var (db, ib) = enabled[b];
                var overlap = Overlap(da.Match, db.Match);
                if (overlap == MatchOverlap.None) continue;
                var pinnedShared = da.Match.PinnedEndpointIds.Intersect(db.Match.PinnedEndpointIds, StringComparer.OrdinalIgnoreCase).Any();
                if (overlap == MatchOverlap.Same || pinnedShared)
                    issues.Add(ConfigIssue.Of(IssueLevel.Error, $"devices[{ib}].match",
                        "Issue.Validator.19", da.Id, db.Id));
                else
                    issues.Add(ConfigIssue.Of(IssueLevel.Warning, $"devices[{ib}].match",
                        "Issue.Validator.20", da.Id, db.Id, ia));
            }
        }
    }

    private enum MatchOverlap { None, Partial, Same }

    private static MatchOverlap Overlap(DeviceMatchSpec a, DeviceMatchSpec b)
    {
        if (a.PinnedEndpointIds.Intersect(b.PinnedEndpointIds, StringComparer.OrdinalIgnoreCase).Any()) return MatchOverlap.Same;
        var aa = a.Adapters.Select(x => Devices.DeviceKeys.NormalizeAdapter(x)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ba = b.Adapters.Select(x => Devices.DeviceKeys.NormalizeAdapter(x)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!aa.Overlaps(ba)) return MatchOverlap.None;
        static int Cmp(string? x, string? y) => x is null || y is null ? (x is null && y is null ? 0 : 1)
            : string.Equals(x, y, StringComparison.OrdinalIgnoreCase) ? 0 : 2;
        var ff = a.FormFactor is null || b.FormFactor is null ? (a.FormFactor is null && b.FormFactor is null ? 0 : 1)
            : a.FormFactor == b.FormFactor ? 0 : 2;
        var desc = Cmp(a.EndpointDesc, b.EndpointDesc);
        var cont = Cmp(a.ContainerId, b.ContainerId);
        if (ff == 2 || desc == 2 || cont == 2) return MatchOverlap.None;   // a criterion tells them apart
        return ff == 0 && desc == 0 && cont == 0 && aa.SetEquals(ba) ? MatchOverlap.Same : MatchOverlap.Partial;
    }

    // ── sources ──

    private static void ValidateSources(FxAutoConfig c, List<ConfigIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byCriteria = new Dictionary<string, (int Index, SourceConfig Source)>(StringComparer.Ordinal);
        for (var i = 0; i < c.Sources.Count; i++)
        {
            var s = c.Sources[i];
            var p = $"sources[{i}]";
            CheckId(s.Id, p + ".id", "source", ids, issues);
            if (string.IsNullOrWhiteSpace(s.Name))
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, p + ".name", "Issue.Validator.21"));
            if (!Origins.Contains(s.Origin, StringComparer.Ordinal))
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, p + ".origin", "Issue.Validator.22", s.Origin));

            if (!Categories.IsKnown(s.Category))
                issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".category", "Issue.Validator.23", s.Category));
            else if (s.Category == Categories.Unknown)
                issues.Add(ConfigIssue.Of(IssueLevel.Info, p + ".category", "Issue.Validator.24"));
            else if (s.Category == Categories.Hold && s.Fallback)
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, p + ".category", "Issue.Validator.25"));
            if (s.Kind == SourceKind.Game && Categories.IsKnown(s.Category) && !Categories.IsGame(s.Category)
                && s.Category is not (Categories.Hold or Categories.Unknown))
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, p + ".category", "Issue.Validator.26", s.Category));

            var m = s.Match ?? new SourceMatch([], [], [], [], null);
            CheckEntries(m.Exe, p + ".match.exe", issues);
            CheckEntries(m.Aumid, p + ".match.aumid", issues);
            CheckEntries(m.Folder, p + ".match.folder", issues);
            CheckEntries(m.Site, p + ".match.site", issues);
            switch (s.Kind)
            {
                case SourceKind.App:
                    if (m.Exe.Count == 0 && m.Aumid.Count == 0)
                        issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".match", "Issue.Validator.27"));
                    break;
                case SourceKind.Game:
                    if (m.Exe.Count == 0 && m.Folder.Count == 0)
                        issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".match", "Issue.Validator.28"));
                    break;
                case SourceKind.Browser:
                    if (m.Exe.Count == 0)
                        issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".match", "Issue.Validator.29"));
                    break;
                case SourceKind.Site:
                    if (m.Site.Count == 0)
                        issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".match.site", "Issue.Validator.30"));
                    break;
            }
            if (s.Kind != SourceKind.Game && m.Folder.Count > 0)
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, p + ".match.folder", "Issue.Validator.31"));
            if (s.Kind != SourceKind.Game && s.Fallback)
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, p + ".fallback", "Issue.Validator.32"));
            if (s.Kind != SourceKind.Site && (m.Site.Count > 0 || m.TitleRegex is not null))
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, p + ".match", "Issue.Validator.33"));

            ValidateSiteAndRegex(s, m, p, issues);

            var key = CriteriaKey(s.Kind, m);
            if (byCriteria.TryGetValue(key, out var prev))
            {
                if (!string.Equals(prev.Source.Category, s.Category, StringComparison.Ordinal))
                    issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".match",
                        "Issue.Validator.34", prev.Source.Name, prev.Index, prev.Source.Category));
                else
                    issues.Add(ConfigIssue.Of(IssueLevel.Warning, p + ".match", "Issue.Validator.35", prev.Source.Name, prev.Index));
            }
            else
            {
                byCriteria[key] = (i, s);
            }
        }
    }

    private static void ValidateSiteAndRegex(SourceConfig s, SourceMatch m, string p, List<ConfigIssue> issues)
    {
        var star = m.Site.Any(x => x.Trim() == "*");
        if (star && m.Site.Count > 1)
            issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".match.site", "Issue.Validator.36"));
        if (star && m.TitleRegex is null)
            issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".match.titleRegex", "Issue.Validator.37"));
        if (m.TitleRegex is null) return;

        var rp = p + ".match.titleRegex";
        if (string.IsNullOrWhiteSpace(m.TitleRegex))
        {
            issues.Add(ConfigIssue.Of(IssueLevel.Error, rp, "Issue.Validator.38"));
            return;
        }
        try
        {
            _ = new Regex(m.TitleRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            issues.Add(ConfigIssue.Of(IssueLevel.Error, rp, "Issue.Validator.39", ex.Message));
            return;
        }
        if (s.Kind == SourceKind.Site && m.Site.Count == 0)
            issues.Add(ConfigIssue.Of(IssueLevel.Error, rp, "Issue.Validator.40"));
        else if (star && !RegexText.IsBounded(m.TitleRegex))
            issues.Add(ConfigIssue.Of(IssueLevel.Error, rp,
                "Issue.Validator.41"));
    }

    private static string CriteriaKey(SourceKind kind, SourceMatch m)
    {
        static string L(IReadOnlyList<string> xs) =>
            string.Join("\u001f", xs.Select(x => x.Trim().ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal));
        return string.Join("\u001e", ConfigJson.EnumName(kind), L(m.Exe), L(m.Aumid), L(m.Folder), L(m.Site), m.TitleRegex ?? "");
    }

    private static void CheckEntries(IReadOnlyList<string> xs, string path, List<ConfigIssue> issues)
    {
        for (var i = 0; i < xs.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(xs[i]))
                issues.Add(ConfigIssue.Of(IssueLevel.Error, $"{path}[{i}]", "Issue.Validator.42"));
        }
    }

    // ── assignments ──

    private static void ValidateAssignments(FxAutoConfig c, List<ConfigIssue> issues)
    {
        var devices = c.Devices.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = c.Sources.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < c.Assignments.Count; i++)
        {
            var a = c.Assignments[i];
            var p = $"assignments[{i}]";
            if (!devices.Contains(a.Device ?? ""))
                issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".device", "Issue.Validator.43", a.Device));

            var hasCat = a.Category is not null;
            var hasSrc = a.Source is not null;
            if (hasCat == hasSrc)
            {
                issues.Add(ConfigIssue.Of(IssueLevel.Error, p, "Issue.Validator.44"));
            }
            else if (hasCat)
            {
                if (a.Category == Categories.Hold)
                    issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".category", "Issue.Validator.45"));
                else if (!Categories.IsKnown(a.Category))
                    issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".category", "Issue.Validator.46", a.Category));
            }
            else if (!sources.Contains(a.Source!))
            {
                issues.Add(ConfigIssue.Of(IssueLevel.Error, p + ".source", "Issue.Validator.47", a.Source));
            }
            ConfigText.CheckPresetName(a.Preset, p + ".preset", issues);

            if (hasCat != hasSrc)
            {
                var key = $"{a.Device}\u001e{(hasCat ? "c:" + a.Category : "s:" + a.Source)}";
                if (keys.TryGetValue(key, out var first))
                    issues.Add(new ConfigIssue(IssueLevel.Error, p, $"Duplicate of assignments[{first}] (same device and {(hasCat ? "category" : "source")}).")
                    { Text = new Msg("Issue.Validator.48", first, new Msg(hasCat ? "Word.Category" : "Word.Source")) });
                else
                    keys[key] = i;
            }
        }
    }

    // ── lists ──

    private static void ValidateNameList(IReadOnlyList<string> xs, string path, List<ConfigIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < xs.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(xs[i]))
                issues.Add(ConfigIssue.Of(IssueLevel.Warning, $"{path}[{i}]", "Issue.Validator.49"));
            else if (!seen.Add(xs[i].Trim()))
                issues.Add(ConfigIssue.Of(IssueLevel.Info, $"{path}[{i}]", "Issue.Validator.50", xs[i]));
        }
    }

    // ── FxSound ──

    private static void ValidateAgainstFxSound(FxAutoConfig c, FxStatus fx, List<ConfigIssue> issues)
    {
        var exact = new HashSet<string>(fx.UserPresets.Concat(fx.BuiltInPresets), StringComparer.Ordinal);
        var loose = new HashSet<string>(exact, StringComparer.OrdinalIgnoreCase);
        void Check(string? name, string path)
        {
            if (string.IsNullOrWhiteSpace(name) || exact.Contains(name)) return;
            issues.Add(loose.Contains(name)
                ? ConfigIssue.Of(IssueLevel.Warning, path, "Issue.Validator.CaseDiffers", name)
                : ConfigIssue.Of(IssueLevel.Warning, path, "Issue.Validator.PresetMissing", name));
        }
        for (var i = 0; i < c.Devices.Count; i++) Check(c.Devices[i].DefaultPreset, $"devices[{i}].defaultPreset");
        for (var i = 0; i < c.Assignments.Count; i++) Check(c.Assignments[i].Preset, $"assignments[{i}].preset");
        foreach (var name in c.Presets.Keys)
        {
            if (!exact.Contains(name))
                issues.Add(ConfigIssue.Of(IssueLevel.Info, $"presets[\"{name}\"]", "Issue.Validator.51", name));
        }
    }

    private static void CheckId(string? id, string path, string what, HashSet<string> ids, List<ConfigIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(id))
            issues.Add(ConfigIssue.Of(IssueLevel.Error, path, "Issue.Validator.52", what));
        else if (!IdPattern.IsMatch(id))
            issues.Add(ConfigIssue.Of(IssueLevel.Error, path, "Issue.Validator.53", what, id));
        else if (!ids.Add(id))
            issues.Add(ConfigIssue.Of(IssueLevel.Error, path, "Issue.Validator.54", what, id));
    }
}
