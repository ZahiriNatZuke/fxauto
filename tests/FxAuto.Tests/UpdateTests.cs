using System.Net;
using System.Security.Cryptography;
using System.Text;
using FxAuto.App;
using FxAuto.Core.Updates;

namespace FxAuto.Tests;

/// <summary>Update engine: versions, release JSON, asset choice, checksum, schedule, persisted state and the service
/// against a fake HTTP handler (no request ever reaches GitHub).</summary>
internal static class UpdateTests
{
    private const string Owner = UpdateService.Owner;
    private const string Repo = UpdateService.Repo;
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    private const string LatestUrl = "https://api.github.com/repos/ZahiriNatZuke/fxauto/releases/latest";
    private const string Dl = "https://github.com/ZahiriNatZuke/fxauto/releases/download/v1.2.0/";

    private static SemVersion V(string s) => SemVersion.Parse(s);

    private static string ReleaseJsonText(string tag = "v1.2.0", bool prerelease = false, string? extraAssets = null) => $$"""
        {
          "url": "https://api.github.com/repos/ZahiriNatZuke/fxauto/releases/1",
          "html_url": "https://github.com/ZahiriNatZuke/fxauto/releases/tag/{{tag}}",
          "tag_name": "{{tag}}",
          "name": "FxAuto {{tag}}",
          "body": "## What's new\n- Faster preset switching",
          "draft": false,
          "prerelease": {{(prerelease ? "true" : "false")}},
          "author": { "login": "someone" },
          "assets": [
            { "name": "FxAuto-Setup-1.2.0.exe", "size": 11, "browser_download_url": "{{Dl}}FxAuto-Setup-1.2.0.exe", "content_type": "application/x-msdownload" },
            { "name": "FxAuto-Setup-1.2.0.exe.sha256", "size": 90, "browser_download_url": "{{Dl}}FxAuto-Setup-1.2.0.exe.sha256" }
            {{extraAssets ?? ""}}
          ]
        }
        """;

    public static void Register()
    {
        Program.Add("update: SemVer parse (v prefix, pre-release, build metadata, 4-part assembly version)", () =>
        {
            Assert.Equal("1.2.3", V("v1.2.3").ToString(), "v prefix");
            Assert.Equal("1.2.3-beta.2", V("V1.2.3-beta.2+sha.abc").ToString(), "pre-release kept, build dropped");
            Assert.Equal("1.0.0", V("1.0.0+855044644e1f").ToString(), "informational version");
            Assert.Equal("1.0.0", V("1.0.0.0").ToString(), "assembly version");
            foreach (var bad in new[] { "", "1.2", "1.2.3.4", "01.2.3", "1.2.3-", "1.2.3-beta..1", "1.2.3-01", "x1.2.3", "1.2.3+" })
                Assert.True(!SemVersion.TryParse(bad, out _), "rejects " + bad);
        });

        Program.Add("update: SemVer precedence", () =>
        {
            string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2",
                                "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "1.10.0", "2.0.0"];
            for (int i = 0; i + 1 < ordered.Length; i++)
                Assert.True(V(ordered[i]) < V(ordered[i + 1]), $"{ordered[i]} < {ordered[i + 1]}");
            Assert.True(V("v1.2.0") == V("1.2.0+build"), "build metadata ignored");
            Assert.Equal(V("1.2.0").GetHashCode(), V("v1.2.0+x").GetHashCode(), "hash");
        });

        Program.Add("update: release JSON (latest) is read; unknown fields ignored, broken assets skipped", () =>
        {
            var r = ReleaseJson.Parse(ReleaseJsonText(extraAssets: """, { "name": "broken" }, 5"""), out var err);
            Assert.Null(err, "no error");
            r = Assert.NotNull(r, "release");
            Assert.Equal("v1.2.0", r.TagName, "tag");
            Assert.Equal("FxAuto v1.2.0", r.Name, "name");
            Assert.True(r.Body!.Contains("Faster", StringComparison.Ordinal), "body");
            Assert.Equal("https://github.com/ZahiriNatZuke/fxauto/releases/tag/v1.2.0", r.HtmlUrl, "html_url");
            Assert.Equal(2, r.Assets.Count, "two usable assets");
            Assert.Equal(11L, r.Assets[0].Size, "size");
            Assert.True(ReleaseJson.Parse("{\"name\":\"x\"}", out err) is null && err is not null, "no tag_name");
            Assert.True(ReleaseJson.Parse("not json", out err) is null && err is not null, "invalid JSON");
            Assert.True(ReleaseJson.Parse("[]", out err) is null && err is not null, "array is not a release");
        });

        Program.Add("update: release list picks the newest, pre-releases only on request, drafts never", () =>
        {
            var list = ReleaseJson.ParseList("[" + ReleaseJsonText("v1.2.0") + "," + ReleaseJsonText("v1.3.0-beta.1", prerelease: true)
                                             + ",{\"tag_name\":\"v9.0.0\",\"draft\":true},{\"tag_name\":\"nightly\"}]", out var err);
            Assert.Null(err, "no error");
            Assert.Equal("1.2.0", UpdateDecision.PickLatest(list!, false)!.Value.Version.ToString(), "stable only");
            Assert.Equal("1.3.0-beta.1", UpdateDecision.PickLatest(list!, true)!.Value.Version.ToString(), "with pre-releases");
        });

        Program.Add("update: decision (newer, same, older, pre-release ignored, 404 = no releases, missing assets)", () =>
        {
            var rel = ReleaseJson.Parse(ReleaseJsonText(), out _)!;
            var up = UpdateDecision.Evaluate(V("1.1.0"), rel, false, Owner, Repo, T0);
            Assert.Equal(UpdateCheckKind.UpdateAvailable, up.Kind, "newer");
            Assert.Equal("1.2.0", up.Latest!.ToString(), "latest");
            Assert.Equal("FxAuto-Setup-1.2.0.exe", up.Installer!.Name, "installer");
            Assert.Equal("FxAuto-Setup-1.2.0.exe.sha256", up.Checksum!.Name, "checksum");
            Assert.True(up.Notes!.Contains("What's new", StringComparison.Ordinal), "notes");
            Assert.Equal(UpdateCheckKind.UpToDate, UpdateDecision.Evaluate(V("1.2.0"), rel, false, Owner, Repo, T0).Kind, "same");
            Assert.Equal(UpdateCheckKind.UpToDate, UpdateDecision.Evaluate(V("1.3.0"), rel, false, Owner, Repo, T0).Kind, "older");
            Assert.Equal(UpdateCheckKind.UpdateAvailable, UpdateDecision.Evaluate(V("1.2.0-rc.1"), rel, false, Owner, Repo, T0).Kind,
                "an rc is older than its release");
            var pre = rel with { TagName = "v1.3.0-beta.1", Prerelease = true };
            Assert.Equal(UpdateCheckKind.UpToDate, UpdateDecision.Evaluate(V("1.2.0"), pre, false, Owner, Repo, T0).Kind, "pre-release ignored");
            Assert.Equal(UpdateCheckKind.UpToDate, UpdateDecision.Evaluate(V("1.2.0"), null, false, Owner, Repo, T0).Kind, "no releases");
            var noSum = rel with { Assets = [rel.Assets[0]] };
            Assert.Equal(UpdateCheckKind.Error, UpdateDecision.Evaluate(V("1.1.0"), noSum, false, Owner, Repo, T0).Kind, "no checksum");
            Assert.Equal(UpdateCheckKind.Error, UpdateDecision.Evaluate(V("1.1.0"), rel with { TagName = "latest" }, false, Owner, Repo, T0).Kind, "bad tag");
        });

        Program.Add("update: asset choice (exact name, single fallback, foreign URLs and path names ignored)", () =>
        {
            var rel = ReleaseJson.Parse(ReleaseJsonText(), out _)!;
            var (i, c) = UpdateAssets.Select(rel, V("1.2.0"), Owner, Repo);
            Assert.Equal("FxAuto-Setup-1.2.0.exe", i?.Name, "exact");
            Assert.Equal("FxAuto-Setup-1.2.0.exe.sha256", c?.Name, "checksum");
            (i, _) = UpdateAssets.Select(rel, V("1.2.1"), Owner, Repo);
            Assert.Equal("FxAuto-Setup-1.2.0.exe", i?.Name, "single FxAuto-Setup-*.exe fallback");
            var foreign = rel with { Assets = [.. rel.Assets.Select(a => a with { DownloadUrl = a.DownloadUrl.Replace("ZahiriNatZuke", "evil", StringComparison.Ordinal) })] };
            Assert.True(UpdateAssets.Select(foreign, V("1.2.0"), Owner, Repo).Installer is null, "other repository");
            var http = rel with { Assets = [.. rel.Assets.Select(a => a with { DownloadUrl = a.DownloadUrl.Replace("https:", "http:", StringComparison.Ordinal) })] };
            Assert.True(UpdateAssets.Select(http, V("1.2.0"), Owner, Repo).Installer is null, "plain http");
            Assert.True(!UpdateAssets.IsPlainFileName("..\\FxAuto-Setup-1.2.0.exe") && !UpdateAssets.IsPlainFileName("a/b.exe"), "path names");
        });

        Program.Add("update: .sha256 parsing (sha256sum text and binary modes, bare hash, wrong file, bad hex)", () =>
        {
            const string h = "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08";
            Assert.Equal(h.ToLowerInvariant(), UpdateAssets.ParseChecksum(h + "  FxAuto-Setup-1.2.0.exe\n", "FxAuto-Setup-1.2.0.exe"), "text mode");
            Assert.Equal(h.ToLowerInvariant(), UpdateAssets.ParseChecksum("﻿" + h + " *FxAuto-Setup-1.2.0.exe\r\n", "FxAuto-Setup-1.2.0.exe"), "binary mode, BOM, CRLF");
            Assert.Equal(h.ToLowerInvariant(), UpdateAssets.ParseChecksum(h, "FxAuto-Setup-1.2.0.exe"), "bare hash");
            Assert.Null(UpdateAssets.ParseChecksum(h + "  other.exe", "FxAuto-Setup-1.2.0.exe"), "other file");
            Assert.Null(UpdateAssets.ParseChecksum("abc  FxAuto-Setup-1.2.0.exe", "FxAuto-Setup-1.2.0.exe"), "short hex");
        });

        Program.Add("update: SHA-256 verification of a file (match and mismatch)", () =>
        {
            var dir = TempDir();
            try
            {
                var f = Path.Combine(dir, "a.bin");
                File.WriteAllBytes(f, Encoding.ASCII.GetBytes("test"));
                var ok = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("test")));
                Assert.True(UpdateAssets.VerifySha256(f, ok), "match");
                Assert.True(UpdateAssets.VerifySha256(f, ok.ToUpperInvariant()), "case-insensitive");
                Assert.True(!UpdateAssets.VerifySha256(f, new string('0', 64)), "mismatch");
                Assert.True(!UpdateAssets.VerifySha256(f, "nothex"), "bad expected hash");
            }
            finally { Directory.Delete(dir, true); }
        });

        Program.Add("update: schedule (3 min after start, every 24 h, back-off after errors, rate limit, off)", () =>
        {
            var s = new UpdateState();
            Assert.Equal(T0 + UpdateSchedule.StartupDelay, UpdateSchedule.NextCheckAt(s, T0), "first run: a few minutes after start");
            Assert.True(!UpdateSchedule.IsDue(s, T0, T0.AddMinutes(2)) && UpdateSchedule.IsDue(s, T0, T0.AddMinutes(3)), "due at +3 min");
            var ok = new UpdateCheckResult(UpdateCheckKind.UpToDate, V("1.0.0"), T0.AddMinutes(3));
            s = UpdateSchedule.After(s, ok);
            Assert.Equal(T0.AddMinutes(3) + UpdateSchedule.Interval, UpdateSchedule.NextCheckAt(s, T0), "24 h after a success");
            Assert.Equal(T0.AddDays(5) + UpdateSchedule.StartupDelay, UpdateSchedule.NextCheckAt(s, T0.AddDays(5)), "overdue: after the start-up delay");
            var err = new UpdateCheckResult(UpdateCheckKind.Error, V("1.0.0"), T0.AddDays(1)) { Error = "x" };
            s = UpdateSchedule.After(s, err);
            Assert.Equal(1, s.ConsecutiveFailures, "one failure");
            Assert.Equal(T0.AddDays(1).AddMinutes(30), UpdateSchedule.NextCheckAt(s, T0), "30 min back-off");
            s = UpdateSchedule.After(s, err with { At = T0.AddDays(1).AddMinutes(30) });
            Assert.Equal(T0.AddDays(1).AddMinutes(90), UpdateSchedule.NextCheckAt(s, T0), "1 h back-off");
            Assert.Equal(UpdateSchedule.MaxBackoff, UpdateSchedule.Backoff(20), "capped");
            var limited = err with { At = T0.AddDays(2), RetryAfter = T0.AddDays(2).AddHours(3) };
            var s2 = UpdateSchedule.After(s, limited);
            Assert.Equal(s.ConsecutiveFailures, s2.ConsecutiveFailures, "rate limit is not a failure");
            Assert.Equal(T0.AddDays(2).AddHours(3), UpdateSchedule.NextCheckAt(s2, T0), "waits for the rate limit");
            s2 = UpdateSchedule.After(s2, ok with { At = T0.AddDays(3) });
            Assert.True(s2.ConsecutiveFailures == 0 && s2.RetryNotBefore is null, "success clears");
            Assert.Null(UpdateSchedule.NextCheckAt(s2 with { CheckAutomatically = false }, T0), "off");
        });

        Program.Add("update: rate-limit headers (Retry-After, x-ratelimit-reset, default 1 h, not a limit)", () =>
        {
            Assert.Equal(T0.AddSeconds(120), UpdateDecision.RateLimitedUntil(429, "120", null, null, T0), "Retry-After");
            var reset = T0.AddMinutes(20).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(T0.AddMinutes(20).AddSeconds(5), UpdateDecision.RateLimitedUntil(403, null, "0", reset, T0), "reset epoch");
            Assert.Equal(T0.AddHours(1), UpdateDecision.RateLimitedUntil(403, null, null, null, T0), "default");
            Assert.Equal(T0.AddHours(24), UpdateDecision.RateLimitedUntil(429, "999999", null, null, T0), "capped");
            Assert.Null(UpdateDecision.RateLimitedUntil(500, "10", null, null, T0), "500 is not a rate limit");
        });

        Program.Add("update: persisted state is backward compatible (missing file/fields, corrupt, round trip)", () =>
        {
            var d = UpdateStateJson.Parse(null);
            Assert.True(d.CheckAutomatically && !d.IncludePrereleases && d.LastCheckAt is null && d.ConsecutiveFailures == 0, "defaults");
            Assert.True(UpdateStateJson.Parse("{ \"schemaVersion\": 1 }").CheckAutomatically, "missing field: on");
            Assert.True(UpdateStateJson.Parse("{ broken").CheckAutomatically, "corrupt: defaults");
            Assert.True(UpdateStateJson.Parse("{ \"checkAutomatically\": \"yes\", \"consecutiveFailures\": -3 }") is { CheckAutomatically: true, ConsecutiveFailures: 0 }, "wrong types: defaults");
            var s = new UpdateState
            {
                CheckAutomatically = false, IncludePrereleases = true, LastCheckAt = T0, LastSuccessAt = T0.AddHours(-1),
                ConsecutiveFailures = 2, RetryNotBefore = T0.AddHours(1), LatestKnownVersion = "1.2.0", NotifiedVersion = "1.2.0",
                SettingsInFile = true,   // settings still kept here (older build): they round-trip until migrated
            };
            Assert.Equal(s, UpdateStateJson.Parse(UpdateStateJson.Serialize(s) .Replace("}", ", \"futureField\": 1 }", StringComparison.Ordinal)), "round trip, unknown field ignored");
            var dir = TempDir();
            try
            {
                var f = Path.Combine(dir, UpdateStateJson.FileName);
                Assert.Equal(new UpdateState(), UpdateStateJson.Load(f), "missing file");
                UpdateStateJson.Save(f, s);
                Assert.Equal(s, UpdateStateJson.Load(f), "saved and loaded");
            }
            finally { Directory.Delete(dir, true); }
        });

        Program.Add("update: state file keeps only run state once the settings moved to config.json", () =>
        {
            var legacy = UpdateStateJson.Parse("{ \"schemaVersion\": 1, \"checkAutomatically\": false, \"includePrereleases\": true }");
            Assert.True(legacy is { SettingsInFile: true, CheckAutomatically: false, IncludePrereleases: true }, "older file: settings read and flagged");
            Assert.True(!UpdateStateJson.Parse("{ \"lastCheckAt\": \"2026-10-26T09:00:00+00:00\" }").SettingsInFile, "run state only: nothing to migrate");
            var json = UpdateStateJson.Serialize(legacy with { SettingsInFile = false, LastCheckAt = T0 });
            Assert.True(!json.Contains("checkAutomatically", StringComparison.Ordinal) && !json.Contains("includePrereleases", StringComparison.Ordinal),
                        "migrated: settings no longer written");
            Assert.True(json.Contains("lastCheckAt", StringComparison.Ordinal), "run state still written");

            // migration decision
            var prefs = Assert.NotNull(UpdateNotice.SettingsToMigrate(null, legacy), "config without keys + legacy file: migrate");
            Assert.Equal(new FxAuto.Core.Config.UpdatePreferences(false, true), prefs, "migrated values");
            Assert.Null(UpdateNotice.SettingsToMigrate(new FxAuto.Core.Config.UpdatePreferences(true, false), legacy), "config already has the keys: it rules");
            Assert.Null(UpdateNotice.SettingsToMigrate(null, new UpdateState()), "no legacy settings: defaults, nothing to migrate");
        });

        Program.Add("update: one tray balloon per version, only for automatic checks", () =>
        {
            var found = new UpdateCheckResult(UpdateCheckKind.UpdateAvailable, V("1.1.0"), T0) { Latest = V("1.2.0"), Automatic = true };
            Assert.True(UpdateNotice.ShouldAnnounce(found, null), "first time: announce");
            Assert.True(!UpdateNotice.ShouldAnnounce(found, "1.2.0"), "same version again: no balloon");
            Assert.True(UpdateNotice.ShouldAnnounce(found with { Latest = V("1.3.0") }, "1.2.0"), "a newer version: announce");
            Assert.True(!UpdateNotice.ShouldAnnounce(found with { Automatic = false }, null), "manual check: About shows it, no balloon");
            Assert.True(!UpdateNotice.ShouldAnnounce(found with { Kind = UpdateCheckKind.UpToDate }, null), "up to date: no balloon");
            Assert.True(!UpdateNotice.ShouldAnnounce(found with { Kind = UpdateCheckKind.Error, Latest = null }, null), "error: no balloon");

            // pending version (tray "Update to X…")
            Assert.Equal("1.2.0", UpdateNotice.Pending(V("1.1.0"), found, null, false)?.ToString(), "last check found it");
            Assert.Null(UpdateNotice.Pending(V("1.1.0"), found with { Kind = UpdateCheckKind.UpToDate }, "1.2.0", false), "up to date wins over the old note");
            Assert.Equal("1.2.0", UpdateNotice.Pending(V("1.1.0"), null, "1.2.0", false)?.ToString(), "before any check: version seen by an earlier run");
            Assert.Null(UpdateNotice.Pending(V("1.2.0"), null, "1.2.0", false), "already installed");
            Assert.Null(UpdateNotice.Pending(V("1.1.0"), null, "1.2.0-beta.1", false), "pre-release ignored when they are off");
            Assert.Equal("1.2.0", UpdateNotice.Pending(V("1.1.0"), found with { Kind = UpdateCheckKind.Error, Latest = null }, "1.2.0", false)?.ToString(),
                         "an error keeps what was known");
        });

        Program.Add("update: service takes its settings from config and marks a version announced once (persisted)", async () =>
        {
            var dir = TempDir();
            try
            {
                var state = Path.Combine(dir, UpdateStateJson.FileName);
                File.WriteAllText(state, "{ \"schemaVersion\": 1, \"checkAutomatically\": false, \"includePrereleases\": true }");
                var h = new FakeHttp();
                h.Set(LatestUrl, HttpStatusCode.OK, ReleaseJsonText());
                using (var svc = NewService(h, dir, "1.1.0", out _))
                {
                    Assert.True(svc.State is { CheckAutomatically: false, SettingsInFile: true }, "legacy settings in force before config applies");
                    svc.ApplySettings(FxAuto.Core.Config.UpdatePreferences.Default);
                    Assert.True(svc.State is { CheckAutomatically: true, IncludePrereleases: false, SettingsInFile: false }, "config rules");
                    Assert.True(!File.ReadAllText(state).Contains("checkAutomatically", StringComparison.Ordinal), "state file has only run state");

                    var manual = await svc.CheckAsync().ConfigureAwait(false);
                    Assert.True(!manual.Automatic && !svc.TryMarkAnnounced(manual), "manual check: no balloon");
                    var auto = manual with { Automatic = true };
                    Assert.True(svc.TryMarkAnnounced(auto), "automatic: first balloon");
                    Assert.True(!svc.TryMarkAnnounced(auto), "automatic: second time no balloon");
                    Assert.Equal("1.2.0", svc.PendingVersion?.ToString(), "pending version for the tray menu");
                }
                using (var again = NewService(h, dir, "1.1.0", out _))
                {
                    Assert.True(!again.TryMarkAnnounced(new UpdateCheckResult(UpdateCheckKind.UpdateAvailable, V("1.1.0"), T0) { Latest = V("1.2.0"), Automatic = true }),
                                "after a restart the same version is not announced again");
                    Assert.Equal("1.2.0", again.PendingVersion?.ToString(), "pending survives a restart (latest known)");
                }
            }
            finally { Directory.Delete(dir, true); }
        });

        Program.Add("update: config general.updates is optional, reads, round-trips and reports wrong types", () =>
        {
            // older config without the keys: valid, defaults (on / off), nothing written back
            var (c, issues) = FxAuto.Core.Config.ConfigJson.Parse("""{ "schemaVersion": 2, "general": { "logLevel": "info" } }""");
            var cfg = Assert.NotNull(c, "parsed without updates");
            Assert.Null(cfg.General.Updates, "absent = null");
            Assert.True(cfg.General.EffectiveUpdates is { CheckAutomatically: true, IncludePrereleases: false }, "defaults");
            Assert.True(!issues.Any(i => i.Path.StartsWith("general.updates", StringComparison.Ordinal)), "no issue for absent keys");
            Assert.True(!FxAuto.Core.Config.ConfigJson.Serialize(cfg).Contains("\"updates\"", StringComparison.Ordinal), "absent stays absent");
            Assert.True(!FxAuto.Core.Config.ConfigValidator.Validate(cfg).Any(i => i.Level == FxAuto.Core.Config.IssueLevel.Error), "validates");

            // with the keys
            var (c2, issues2) = FxAuto.Core.Config.ConfigJson.Parse(
                """{ "schemaVersion": 2, "general": { "updates": { "checkAutomatically": false, "includePrereleases": true } } }""");
            var cfg2 = Assert.NotNull(c2, "parsed with updates");
            Assert.Equal(new FxAuto.Core.Config.UpdatePreferences(false, true), cfg2.General.Updates, "read");
            Assert.True(!issues2.Any(i => i.Path.StartsWith("general.updates", StringComparison.Ordinal)), "known keys");
            var (again, _) = FxAuto.Core.Config.ConfigJson.Parse(FxAuto.Core.Config.ConfigJson.Serialize(cfg2));
            Assert.Equal(cfg2.General.Updates, again!.General.Updates, "round trip");

            // partial object: the missing key takes its default
            var (c3, _) = FxAuto.Core.Config.ConfigJson.Parse("""{ "schemaVersion": 2, "general": { "updates": { "includePrereleases": true } } }""");
            Assert.Equal(new FxAuto.Core.Config.UpdatePreferences(true, true), c3!.General.Updates, "partial");

            // wrong type and unknown key are reported, the rest still loads
            var (c4, issues4) = FxAuto.Core.Config.ConfigJson.Parse(
                """{ "schemaVersion": 2, "general": { "updates": { "checkAutomatically": "yes", "channel": "beta" } } }""");
            Assert.True(issues4.Any(i => i.Path == "general.updates.checkAutomatically"), "wrong type reported");
            Assert.True(issues4.Any(i => i.Path == "general.updates.channel" && i.Level == FxAuto.Core.Config.IssueLevel.Warning), "unknown key warns");
        });

        Program.Add("update: release notes become plain text", () =>
        {
            var text = UpdateNotice.PlainText("## What's new\r\n- **Faster** switching\n* `code` and [a link](https://example.com)\n\nPlain line");
            Assert.Equal("What's new\r\n• Faster switching\r\n• code and a link\r\n\r\nPlain line", text, "markdown stripped");
            Assert.Equal("", UpdateNotice.PlainText(null), "no notes");
        });

        Program.Add("update: service check (headers, newer release, 404 = up to date, 429 respected, 500 error)", async () =>
        {
            var dir = TempDir();
            try
            {
                var h = new FakeHttp();
                h.Set(LatestUrl, HttpStatusCode.OK, ReleaseJsonText());
                using var svc = NewService(h, dir, "1.1.0", out _);
                var r = await svc.CheckAsync().ConfigureAwait(false);
                Assert.Equal(UpdateCheckKind.UpdateAvailable, r.Kind, "available");
                Assert.Equal("1.2.0", r.Latest?.ToString(), "latest");
                var req = h.Requests.Single();
                Assert.Equal("FxAuto/1.1.0", req.UserAgent, "User-Agent");
                Assert.Equal("application/vnd.github+json", req.Accept, "Accept");
                Assert.True(req.Authorization is null, "no token");
                Assert.True(svc.LastResult == r, "LastResult");
                Assert.True(File.Exists(Path.Combine(dir, UpdateStateJson.FileName)), "state saved");

                h.Set(LatestUrl, HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
                Assert.Equal(UpdateCheckKind.UpToDate, (await svc.CheckAsync().ConfigureAwait(false)).Kind, "404 = no releases");

                h.Set(LatestUrl, HttpStatusCode.InternalServerError, "");
                r = await svc.CheckAsync().ConfigureAwait(false);
                Assert.True(r.Kind == UpdateCheckKind.Error && svc.State.ConsecutiveFailures == 1, "500 error counted");

                h.Set(LatestUrl, HttpStatusCode.TooManyRequests, "", ("Retry-After", "600"));
                r = await svc.CheckAsync().ConfigureAwait(false);
                Assert.True(r.Kind == UpdateCheckKind.Error && r.RetryAfter is not null, "429 rate limit");
                int sent = h.Requests.Count;
                h.Set(LatestUrl, HttpStatusCode.OK, ReleaseJsonText());
                r = await svc.CheckAsync().ConfigureAwait(false);
                Assert.True(r.Kind == UpdateCheckKind.Error && h.Requests.Count == sent, "no request inside the rate-limit window");
            }
            finally { Directory.Delete(dir, true); }
        });

        Program.Add("update: download verifies SHA-256 (good file kept, bad file deleted, old downloads cleaned)", async () =>
        {
            var dir = TempDir();
            try
            {
                var payload = Encoding.ASCII.GetBytes("installer!!");   // 11 bytes, matches the fixture size
                var hex = Convert.ToHexStringLower(SHA256.HashData(payload));
                var h = new FakeHttp();
                h.Set(LatestUrl, HttpStatusCode.OK, ReleaseJsonText());
                h.Set(Dl + "FxAuto-Setup-1.2.0.exe", HttpStatusCode.OK, payload);
                h.Set(Dl + "FxAuto-Setup-1.2.0.exe.sha256", HttpStatusCode.OK, hex + "  FxAuto-Setup-1.2.0.exe\n");
                using var svc = NewService(h, dir, "1.1.0", out var downloads);
                Directory.CreateDirectory(downloads);
                File.WriteAllText(Path.Combine(downloads, "FxAuto-Setup-1.0.5.exe"), "old");
                var check = await svc.CheckAsync().ConfigureAwait(false);
                var reports = new List<DownloadProgress>();
                var ok = await svc.DownloadAndVerifyAsync(check, new SyncProgress(reports.Add)).ConfigureAwait(false);
                Assert.True(ok.Ok, "verified: " + ok.Error);
                Assert.Equal(hex, ok.Sha256, "hash");
                Assert.True(File.Exists(ok.InstallerPath!) && Path.GetFileName(ok.InstallerPath) == "FxAuto-Setup-1.2.0.exe", "installer in place");
                Assert.True(!File.Exists(Path.Combine(downloads, "FxAuto-Setup-1.0.5.exe")), "old download removed");
                Assert.True(reports.Count > 0 && reports[^1].BytesReceived == 11, "progress");

                h.Set(Dl + "FxAuto-Setup-1.2.0.exe.sha256", HttpStatusCode.OK, new string('a', 64) + "  FxAuto-Setup-1.2.0.exe\n");
                var bad = await svc.DownloadAndVerifyAsync(check).ConfigureAwait(false);
                Assert.True(!bad.Ok && bad.Error!.Contains("SHA-256", StringComparison.Ordinal), "mismatch is an error");
                Assert.True(Directory.GetFiles(downloads).Length == 0, "mismatched file deleted");
                Assert.True(!svc.LaunchInstallerAndExit(bad), "nothing to launch");

                h.Set(Dl + "FxAuto-Setup-1.2.0.exe.sha256", HttpStatusCode.OK, hex + "  FxAuto-Setup-1.2.0.exe\n");
                using var cts = new CancellationTokenSource();
                await cts.CancelAsync().ConfigureAwait(false);
                var cancelled = await svc.DownloadAndVerifyAsync(check, null, cts.Token).ContinueWith(t => t.IsCanceled ? null : t.Result,
                    TaskScheduler.Default).ConfigureAwait(false);
                Assert.True(cancelled is null || cancelled.Cancelled, "cancellable");
                Assert.True(h.Requests.All(r => r.Url.StartsWith("https://api.github.com/repos/ZahiriNatZuke/fxauto/", StringComparison.Ordinal)
                                               || r.Url.StartsWith(Dl, StringComparison.Ordinal)), "only the two kinds of URL");
            }
            finally { Directory.Delete(dir, true); }
        });
    }

    private static UpdateService NewService(FakeHttp h, string dir, string version, out string downloads)
    {
        downloads = Path.Combine(dir, "updates");
        return new UpdateService(V(version), Path.Combine(dir, UpdateStateJson.FileName), downloads, (_, _) => { },
                                 _ => throw new InvalidOperationException("must not exit in tests"), h, () => T0);
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "fxauto-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    private sealed record SentRequest(string Url, string? UserAgent, string? Accept, string? Authorization);

    /// <summary>Answers from a URL table; any other URL is a 599 so a stray request fails loudly.</summary>
    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Code, byte[] Body, (string, string)[] Headers)> _map = [];
        public List<SentRequest> Requests { get; } = [];

        public void Set(string url, HttpStatusCode code, string body, params (string, string)[] headers)
            => _map[url] = (code, Encoding.UTF8.GetBytes(body), headers);

        public void Set(string url, HttpStatusCode code, byte[] body) => _map[url] = (code, body, []);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.ToString();
            Requests.Add(new(url, request.Headers.UserAgent.ToString(), request.Headers.Accept.ToString(),
                             request.Headers.Authorization?.ToString()));
            if (!_map.TryGetValue(url, out var e)) return Task.FromResult(new HttpResponseMessage((HttpStatusCode)599));
            var resp = new HttpResponseMessage(e.Code) { Content = new ByteArrayContent(e.Body), RequestMessage = request };
            foreach (var (k, v) in e.Headers) resp.Headers.TryAddWithoutValidation(k, v);
            return Task.FromResult(resp);
        }
    }
}
