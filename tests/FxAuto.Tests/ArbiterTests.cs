using FxAuto.Core;
using FxAuto.Core.Arbitration;
using FxAuto.Core.Audio;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;

namespace FxAuto.Tests;

internal static class ArbiterTests
{
    private static SourceClass Cls(string category, string? id = null, bool browser = false)
        => new(category, id ?? category, null, "test", Categories.IsGame(category), browser);

    private static CandidateSource Cand(FakeClock clock, int pid, string category, bool fg = false, bool playing = true,
        int sinceAgoMs = 10000, int? lastAudibleAgoMs = 0, int? startedAgoMs = 600000)
        => new(pid, Cls(category), fg, playing,
            playing ? clock.Now.AddMilliseconds(-sinceAgoMs) : null,
            lastAudibleAgoMs is null ? null : clock.Now.AddMilliseconds(-lastAudibleAgoMs.Value),
            startedAgoMs is null ? null : clock.Now.AddMilliseconds(-startedAgoMs.Value));

    private static (SourceArbiter A, FakeClock C) New(int idleGraceMs = 60000, int minDwellMs = 5000,
        CallPolicy policy = CallPolicy.Ignore)
    {
        var c = new FakeClock();
        return (new SourceArbiter(new ArbiterTimings(60000, idleGraceMs, minDwellMs, policy), c), c);
    }

    public static void Register()
    {
        Program.Add("Arbiter: background music + foreground game → game", () =>
        {
            var (a, c) = New();
            var d = a.Decide([Cand(c, 1, Categories.Music), Cand(c, 2, Categories.GameFps, fg: true, sinceAgoMs: 2000)], null, Mode.Auto);
            Assert.Equal(WinLevel.ForegroundGamePlaying, d.Level, "level");
            Assert.Equal(2, d.Winner!.RootPid, "winner");
        });

        Program.Add("Arbiter: game exits → music; then nothing → grace → device default", () =>
        {
            var (a, c) = New();
            a.Decide([Cand(c, 1, Categories.Music), Cand(c, 2, Categories.GameFps, fg: true)], null, Mode.Auto);
            c.Advance(30000);
            var d = a.Decide([Cand(c, 1, Categories.Music)], null, Mode.Auto);
            Assert.Equal(WinLevel.BackgroundMostRecent, d.Level, "music level");
            Assert.Equal(1, d.Winner!.RootPid, "music");
            c.Advance(10000);
            d = a.Decide([], null, Mode.Auto);
            Assert.Equal(WinLevel.IdleGrace, d.Level, "grace");
            Assert.Equal(1, d.Winner!.RootPid, "keeps music during grace");
            Assert.Equal(c.Now.AddMilliseconds(60000), a.NextDueAt!.Value, "grace end due");
            c.Advance(59000);
            Assert.Equal(WinLevel.IdleGrace, a.Decide([], null, Mode.Auto).Level, "still grace");
            c.Advance(1000);
            d = a.Decide([], null, Mode.Auto);
            Assert.Equal(WinLevel.IdleDefault, d.Level, "default");
            Assert.Null(d.Winner, "no winner");
        });

        Program.Add("Arbiter: sound resumes during grace → grace cancelled", () =>
        {
            var (a, c) = New();
            a.Decide([Cand(c, 1, Categories.Music)], null, Mode.Auto);
            c.Advance(10000);
            a.Decide([], null, Mode.Auto);
            c.Advance(30000);
            Assert.Equal(WinLevel.BackgroundMostRecent, a.Decide([Cand(c, 1, Categories.Music)], null, Mode.Auto).Level, "back");
            c.Advance(50000);
            Assert.Equal(WinLevel.IdleGrace, a.Decide([], null, Mode.Auto).Level, "new grace starts again");
        });

        Program.Add("Arbiter: nothing ever played → device default at once", () =>
        {
            var (a, _) = New();
            var d = a.Decide([], null, Mode.Auto);
            Assert.Equal(WinLevel.IdleDefault, d.Level, "level");
        });

        Program.Add("Arbiter: YouTube in background while a silent app is in front → video stays", () =>
        {
            var (a, c) = New();
            var telegram = Cand(c, 3, Categories.Unknown, fg: true, playing: false, lastAudibleAgoMs: null);
            var d = a.Decide([Cand(c, 1, Categories.Video), telegram], null, Mode.Auto);
            Assert.Equal(1, d.Winner!.RootPid, "video");
            Assert.Equal(WinLevel.BackgroundMostRecent, d.Level, "level");
        });

        Program.Add("Arbiter: playing hold source freezes changes", () =>
        {
            var (a, c) = New(minDwellMs: 0);
            a.Decide([Cand(c, 1, Categories.Music)], null, Mode.Auto);
            c.Advance(1000);
            var d = a.Decide([Cand(c, 1, Categories.Music), Cand(c, 2, Categories.GameFps, fg: true), Cand(c, 9, Categories.Hold)], null, Mode.Auto);
            Assert.Equal(WinLevel.Hold, d.Level, "hold");
            Assert.Equal(9, d.Winner!.RootPid, "hold source");
            c.Advance(1000);
            d = a.Decide([Cand(c, 1, Categories.Music), Cand(c, 9, Categories.Hold, playing: false)], null, Mode.Auto);
            Assert.Equal(WinLevel.BackgroundMostRecent, d.Level, "hold stopped: normal again");
        });

        Program.Add("Arbiter: call with Ignore never wins; with Category it can", () =>
        {
            var (a, c) = New();
            var d = a.Decide([Cand(c, 5, Categories.Call, fg: true, sinceAgoMs: 1)], null, Mode.Auto);
            Assert.Equal(WinLevel.IdleDefault, d.Level, "call alone ignored");
            d = a.Decide([Cand(c, 1, Categories.Music, sinceAgoMs: 60000), Cand(c, 5, Categories.Call, sinceAgoMs: 1)], null, Mode.Auto);
            Assert.Equal(1, d.Winner!.RootPid, "music, not the newer call");
            var (b, c2) = New(policy: CallPolicy.Category);
            d = b.Decide([Cand(c2, 5, Categories.Call, fg: true)], null, Mode.Auto);
            Assert.Equal(5, d.Winner!.RootPid, "call allowed");
        });

        Program.Add("Arbiter: two background sources → the one that started most recently", () =>
        {
            var (a, c) = New();
            var d = a.Decide([Cand(c, 1, Categories.Music, sinceAgoMs: 60000), Cand(c, 2, Categories.Video, sinceAgoMs: 5000)], null, Mode.Auto);
            Assert.Equal(2, d.Winner!.RootPid, "most recent");
        });

        Program.Add("Arbiter: same start time → category order (sports > video > music)", () =>
        {
            var (a, c) = New();
            var d = a.Decide([Cand(c, 1, Categories.Music), Cand(c, 2, Categories.Sports), Cand(c, 3, Categories.Video)], null, Mode.Auto);
            Assert.Equal(2, d.Winner!.RootPid, "sports");
        });

        Program.Add("Arbiter: unknown background newcomer does not displace a classified winner", () =>
        {
            var (a, c) = New();
            a.Decide([Cand(c, 1, Categories.Music, sinceAgoMs: 60000)], null, Mode.Auto);
            c.Advance(30000);
            var d = a.Decide([Cand(c, 1, Categories.Music, sinceAgoMs: 90000), Cand(c, 4, Categories.Unknown, sinceAgoMs: 2000)], null, Mode.Auto);
            Assert.Equal(1, d.Winner!.RootPid, "music kept");
            d = a.Decide([Cand(c, 4, Categories.Unknown, sinceAgoMs: 2000)], null, Mode.Auto);
            Assert.Equal(4, d.Winner!.RootPid, "unknown wins once music stopped");
        });

        Program.Add("Arbiter: foreground game loading (never sounded, young process) beats background music", () =>
        {
            var (a, c) = New();
            var game = Cand(c, 2, Categories.GameRpg, fg: true, playing: false, lastAudibleAgoMs: null, startedAgoMs: 15000);
            var d = a.Decide([Cand(c, 1, Categories.Music), game], null, Mode.Auto);
            Assert.Equal(WinLevel.ForegroundGameLoading, d.Level, "loading");
            Assert.Equal(2, d.Winner!.RootPid, "game");
            Assert.Equal(game.ProcessStart!.Value.AddMilliseconds(60000), a.NextDueAt!.Value, "loading end due");
            c.Advance(50000);
            var old = game with { ProcessStart = c.Now.AddMilliseconds(-65000) };
            d = a.Decide([Cand(c, 1, Categories.Music), old], null, Mode.Auto);
            Assert.Equal(1, d.Winner!.RootPid, "old silent game loses to music");
        });

        Program.Add("Arbiter: foreground game that sounded and was released is not loading", () =>
        {
            var (a, c) = New();
            var game = Cand(c, 2, Categories.GameFps, fg: true, playing: false, lastAudibleAgoMs: 25000, startedAgoMs: 30000);
            Assert.Equal(1, a.Decide([Cand(c, 1, Categories.Music), game], null, Mode.Auto).Winner!.RootPid, "music");
        });

        Program.Add("Arbiter: foreground source playing > background game > background source", () =>
        {
            var (a, c) = New(minDwellMs: 0);
            var d = a.Decide([Cand(c, 1, Categories.Music, sinceAgoMs: 1), Cand(c, 2, Categories.GameFps), Cand(c, 3, Categories.Video, fg: true, sinceAgoMs: 90000)], null, Mode.Auto);
            Assert.Equal(WinLevel.ForegroundPlaying, d.Level, "fg playing");
            Assert.Equal(3, d.Winner!.RootPid, "fg video");
            d = a.Decide([Cand(c, 1, Categories.Music, sinceAgoMs: 1), Cand(c, 2, Categories.GameFps)], null, Mode.Auto);
            Assert.Equal(WinLevel.BackgroundGamePlaying, d.Level, "bg game");
            Assert.Equal(2, d.Winner!.RootPid, "game");
        });

        Program.Add("Arbiter: minimum dwell blocks a quick change; foreground game is exempt", () =>
        {
            var (a, c) = New();
            a.Decide([Cand(c, 1, Categories.Music)], null, Mode.Auto);
            c.Advance(2000);
            var d = a.Decide([Cand(c, 1, Categories.Music), Cand(c, 2, Categories.Video, sinceAgoMs: 1)], null, Mode.Auto);
            Assert.Equal(1, d.Winner!.RootPid, "kept during dwell");
            Assert.True(d.Reason.Contains("dwell", StringComparison.Ordinal), "reason: " + d.Reason);
            Assert.Equal(c.Now.AddMilliseconds(3000), a.NextDueAt!.Value, "dwell end due");
            c.Advance(3000);
            d = a.Decide([Cand(c, 1, Categories.Music), Cand(c, 2, Categories.Video, sinceAgoMs: 1)], null, Mode.Auto);
            Assert.Equal(2, d.Winner!.RootPid, "changed after dwell");
            c.Advance(500);
            d = a.Decide([Cand(c, 2, Categories.Video), Cand(c, 3, Categories.GameFps, fg: true, sinceAgoMs: 1)], null, Mode.Auto);
            Assert.Equal(3, d.Winner!.RootPid, "foreground game enters at once");
        });

        Program.Add("Arbiter: same source changing category is a change (dwell applies)", () =>
        {
            var (a, c) = New();
            a.Decide([Cand(c, 1, Categories.Video)], null, Mode.Auto);
            c.Advance(1000);
            var d = a.Decide([Cand(c, 1, Categories.Music)], null, Mode.Auto);
            Assert.Equal(Categories.Video, d.Winner!.Class.Category, "kept");
            c.Advance(4000);
            Assert.Equal(Categories.Music, a.Decide([Cand(c, 1, Categories.Music)], null, Mode.Auto).Winner!.Class.Category, "changed");
        });

        Program.Add("Arbiter: Locked, Editing and Paused modes send nothing", () =>
        {
            var (a, c) = New();
            var src = new[] { Cand(c, 2, Categories.GameFps, fg: true) };
            Assert.Equal(WinLevel.Locked, a.Decide(src, null, Mode.Locked).Level, "locked");
            Assert.Equal(WinLevel.Editing, a.Decide(src, null, Mode.Editing).Level, "editing");
            var p = a.Decide(src, null, Mode.Paused);
            Assert.Null(p.Winner, "paused: no winner");
            Assert.Equal(WinLevel.ForegroundGamePlaying, a.Decide(src, null, Mode.Auto).Level, "auto");
        });

        Program.Add("Arbiter: Reason never contains the window title (privacy)", () =>
        {
            var (a, c) = New();
            const string title = "Secret project meeting - YouTube";
            var fg = new ForegroundInfo(1, "chrome.exe", null, title, null, 2, null, false);
            var cls = new SourceClass(Categories.Video, "youtube", "YouTube", "site segment", false, true);
            var reasons = new List<string>
            {
                a.Decide([new CandidateSource(2, cls, true, true, c.Now, c.Now, null)], fg, Mode.Auto).Reason,
            };
            c.Advance(1000);
            reasons.Add(a.Decide([new CandidateSource(3, Cls(Categories.Music), false, true, c.Now, c.Now, null)], fg, Mode.Auto).Reason);
            reasons.Add(a.Decide([], fg, Mode.Auto).Reason);
            foreach (var r in reasons)
                Assert.True(!r.Contains("Secret", StringComparison.OrdinalIgnoreCase), "reason leaks: " + r);
        });

        Program.Add("Arbiter: timings taken from the config (idle grace seconds, dwell, call policy)", () =>
        {
            var t = SourceArbiter.TimingsFrom(ClassTestConfig.General(idleGraceSeconds: 30, minDwellMs: 2000, callPolicy: CallPolicy.Category));
            Assert.Equal(30000, t.IdleGraceMs, "grace");
            Assert.Equal(2000, t.MinDwellMs, "dwell");
            Assert.Equal(60000, t.GameLoadingMaxMs, "loading");
            Assert.Equal(CallPolicy.Category, t.CallPolicy, "policy");
        });

        // ---------- Detector + tracker + arbiter together ----------
        Program.Add("Pipeline: background notification burst does not take the preset from the game", () =>
        {
            var rig = new SoundingRig();
            var arb = new SourceArbiter(new ArbiterTimings(), rig.Clock);
            rig.Games.Add(7);
            rig.Foreground.Add(7);
            var cls = new Dictionary<int, SourceClass> { [7] = Cls(Categories.GameFps), [8] = Cls(Categories.Call) with { Category = Categories.Unknown } };
            ArbiterDecision? last = null;
            for (int t = 0; t < 12000; t += 250)
            {
                var st = rig.Step(t >= 5000 && t < 6000 ? [(7, 0.2f), (8, 0.3f)] : [(7, 0.2f)]);
                var cands = st.Select(s => new CandidateSource(s.RootPid, cls[s.RootPid], rig.Foreground.Contains(s.RootPid),
                    s.Playing, s.AudibleSince, s.LastAudibleAt, null)).ToList();
                last = arb.Decide(cands, null, Mode.Auto);
                if (t >= 1500) Assert.Equal(7, last.Winner?.RootPid ?? -1, "game wins at " + t);
            }
            Assert.Equal(WinLevel.ForegroundGamePlaying, last!.Level, "level");
        });

        Program.Add("Pipeline: browser plays in background, user switches tab, UIA unavailable → still music", () =>
        {
            var rig = new SoundingRig();
            var arb = new SourceArbiter(new ArbiterTimings(), rig.Clock);
            var tracker = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), rig.Clock);
            tracker.OnBrowserTitle(4, "chrome.exe", "Song - YouTube Music - Google Chrome");
            bool wasPlaying = false;
            ArbiterDecision? last = null;
            for (int t = 0; t < 6000; t += 250)
            {
                var st = rig.Step([(4, 0.2f)]);
                var s = st.Single();
                if (s.Playing != wasPlaying) { tracker.OnPlayingChanged(4, s.Playing); wasPlaying = s.Playing; }
                if (tracker.NeedsTabRead(4)) tracker.OnTabs(4, null);
                if (t == 3000) tracker.OnBrowserTitle(4, "chrome.exe", "Mail - Inbox - Google Chrome");
                last = arb.Decide([new CandidateSource(4, tracker.Current(4, "chrome.exe"), false, s.Playing, s.AudibleSince, s.LastAudibleAt, null)], null, Mode.Auto);
            }
            Assert.Equal(Categories.Music, last!.Winner!.Class.Category, "music");
        });
    }
}
