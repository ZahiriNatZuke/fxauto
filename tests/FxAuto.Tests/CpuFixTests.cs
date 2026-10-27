using FxAuto.App;
using FxAuto.Browser;
using FxAuto.Core.Audio;
using FxAuto.Core.Classification;

namespace FxAuto.Tests;

/// <summary>CPU fixes: event-driven tab reads, decisions only when the inputs change, a timer armed for the next due
/// instant, title changes that no rule uses, and the process-tree walk that replaces a snapshot per tab read.</summary>
internal static class CpuFixTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static PlayingState St(int pid, bool playing, DateTimeOffset? since = null, DateTimeOffset? last = null,
        string exe = "chrome.exe")
        => new(pid, exe, playing, since, last);

    public static void Register()
    {
        Program.Add("cpu: tab reads are event-driven (start + follow-up 4 s, stale), safety re-read after 60 s", () =>
        {
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            t.OnBrowserTitle(100, "chrome.exe", "x - YouTube");
            t.MarkStale(100);
            Assert.True(!t.NeedsTabRead(100), "stale while not playing is ignored");
            t.OnPlayingChanged(100, true);
            Assert.True(t.NeedsTabRead(100), "on start");
            t.OnTabsRequested(100);
            t.OnTabs(100, [new BrowserTab("x - YouTube", true, true)]);
            Assert.True(!t.NeedsTabRead(100), "just read");
            Assert.Equal(clock.Now.AddSeconds(4), t.NextTabReadAt(100), "follow-up read 4 s after the first one");
            clock.Advance(4000);
            Assert.True(t.NeedsTabRead(100), "follow-up due");
            t.OnTabsRequested(100);
            t.OnTabs(100, [new BrowserTab("x - YouTube", true, true)]);
            clock.Advance(5000);
            Assert.True(!t.NeedsTabRead(100), "no fixed 5 s polling any more");
            Assert.Equal(clock.Now.AddSeconds(55), t.NextTabReadAt(100), "safety re-read 60 s after the read");
            t.MarkStale(100);
            Assert.True(t.NeedsTabRead(100), "stale: read at once");
            clock.Advance(40000);
            Assert.True(t.NeedsTabRead(100), "still stale");
            t.OnTabsRequested(100);
            t.OnTabs(100, [new BrowserTab("x - YouTube", true, true)]);
            Assert.True(!t.NeedsTabRead(100), "stale cleared by a read requested after it");
            clock.Advance(59999);
            Assert.True(!t.NeedsTabRead(100), "before 60 s");
            clock.Advance(1);
            Assert.True(t.NeedsTabRead(100), "safety re-read at 60 s");
            t.OnPlayingChanged(100, false);
            Assert.True(t.NextTabReadAt(100) is null, "not playing: never");
        });

        Program.Add("cpu: tab results are matched to the last request of the current playback (request id)", () =>
        {
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            t.OnBrowserTitle(100, "chrome.exe", "Inbox - Mail - Google Chrome");
            t.OnPlayingChanged(100, true);
            t.OnTabsRequested(100, 1);
            t.OnPlayingChanged(100, false);              // stopped before the answer
            t.OnPlayingChanged(100, true);               // and started again: request 1 belongs to the old playback
            Assert.True(!t.OnTabs(100, [new BrowserTab("Song - YouTube Music", true, false)], 1), "old playback: dropped");
            Assert.True(t.NeedsTabRead(100), "still needs its first read");
            Assert.Equal(Categories.Video, t.Current(100, "chrome.exe").Category, "dropped result not used");
            t.OnTabsRequested(100, 2);
            t.OnTabsRequested(100, 3);
            Assert.True(!t.OnTabs(100, null, 2), "superseded request: dropped");
            Assert.True(t.OnTabs(100, [new BrowserTab("Song - YouTube Music", true, false)], 3), "last request: applied");
            Assert.Equal(Categories.Music, t.Current(100, "chrome.exe").Category, "audible tab");
            Assert.True(!t.OnTabs(100, null, 3), "answered once");
            t.OnPlayingChanged(100, false);
            Assert.True(!t.OnTabs(100, null, 4), "not playing: dropped");
        });

        Program.Add("cpu: a change seen while a read is in flight is not lost", () =>
        {
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            t.OnBrowserTitle(100, "chrome.exe", "x - YouTube");
            t.OnPlayingChanged(100, true);
            t.OnTabsRequested(100);
            clock.Advance(30);
            t.MarkStale(100);                    // title changed after the request was sent
            clock.Advance(30);
            t.OnTabs(100, [new BrowserTab("x - YouTube", true, true)]);
            Assert.True(t.NeedsTabRead(100), "read again");
        });

        Program.Add("cpu: background audible tab and latch still win without polling", () =>
        {
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            t.OnBrowserTitle(100, "chrome.exe", "Inbox - Mail - Google Chrome");
            t.OnPlayingChanged(100, true);
            Assert.Equal(Categories.Video, t.Current(100, "chrome.exe").Category, "browser default before the read");
            t.OnTabsRequested(100);
            t.OnTabs(100, [new BrowserTab("Inbox - Mail", false, true), new BrowserTab("Song - YouTube Music", true, false)]);
            Assert.Equal(Categories.Music, t.Current(100, "chrome.exe").Category, "audible background tab");
            clock.Advance(30000);
            Assert.Equal(Categories.Music, t.Current(100, "chrome.exe").Category, "kept between reads");

            var l = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            l.OnBrowserTitle(200, "chrome.exe", "Song - YouTube Music - Google Chrome");
            l.OnPlayingChanged(200, true);
            l.OnBrowserTitle(200, "chrome.exe", "Inbox - Mail - Google Chrome");
            l.MarkStale(200);
            l.OnTabsRequested(200);
            l.OnTabs(200, null);                 // read failed
            Assert.Equal(Categories.Music, l.Current(200, "chrome.exe").Category, "latched");
            Assert.Equal(clock.Now.AddSeconds(4), l.NextTabReadAt(200), "follow-up read after 4 s");
            clock.Advance(4000);
            l.OnTabsRequested(200);
            l.OnTabs(200, null);                 // failed again
            Assert.Equal(clock.Now.AddSeconds(15), l.NextTabReadAt(200), "failed read retried after 15 s");
        });

        Program.Add("cpu: tab request due respects the 1.5 s minimum between requests", () =>
        {
            var min = TimeSpan.FromSeconds(1.5);
            Assert.True(CoordinatorLogic.TabRequestDue(null, T0, min) is null, "tracker: never");
            Assert.Equal(T0, CoordinatorLogic.TabRequestDue(T0, null, min), "no previous request");
            Assert.Equal(T0.AddSeconds(1.5), CoordinatorLogic.TabRequestDue(T0, T0, min), "throttled");
            Assert.Equal(T0.AddSeconds(45), CoordinatorLogic.TabRequestDue(T0.AddSeconds(45), T0, min), "later due wins");
            Assert.Equal(T0.AddSeconds(1.5), CoordinatorLogic.TabRequestDue(DateTimeOffset.MinValue, T0, min),
                "'now' (never read) after a recent request");
        });

        Program.Add("cpu: next wake = earliest due, clamped to [250 ms, 60 s]; nothing pending stops the timer", () =>
        {
            var min = TimeSpan.FromMilliseconds(250);
            var max = TimeSpan.FromSeconds(60);
            Assert.True(CoordinatorLogic.NextWake(T0, min, max) is null, "no dues");
            Assert.True(CoordinatorLogic.NextWake(T0, min, max, null, null) is null, "only nulls");
            Assert.Equal(T0.AddSeconds(5), CoordinatorLogic.NextWake(T0, min, max, T0.AddSeconds(8), null, T0.AddSeconds(5)),
                "earliest");
            Assert.Equal(T0.AddMilliseconds(250), CoordinatorLogic.NextWake(T0, min, max, T0.AddSeconds(-3)),
                "overdue: retried at the old tick pace");
            Assert.Equal(T0.AddMilliseconds(250), CoordinatorLogic.NextWake(T0, min, max, T0.AddMilliseconds(100)),
                "not sooner than 250 ms");
            Assert.Equal(T0.AddSeconds(60), CoordinatorLogic.NextWake(T0, min, max, T0.AddHours(2)), "capped at 60 s");
        });

        Program.Add("cpu: same decision states ignore LastAudibleAt moving, not playing/AudibleSince/roots", () =>
        {
            var a = new List<PlayingState> { St(1, true, T0, T0.AddSeconds(1)), St(2, false) };
            var b = new List<PlayingState> { St(1, true, T0, T0.AddSeconds(2)), St(2, false) };
            Assert.True(CoordinatorLogic.SameDecisionStates(a, b), "only LastAudibleAt moved");
            Assert.True(!CoordinatorLogic.SameDecisionStates(a, [St(1, false, null, T0.AddSeconds(2)), St(2, false)]), "stopped");
            Assert.True(!CoordinatorLogic.SameDecisionStates(a, [St(1, true, T0.AddSeconds(1), T0.AddSeconds(2)), St(2, false)]),
                "AudibleSince changed");
            Assert.True(!CoordinatorLogic.SameDecisionStates(a, [St(1, true, T0, T0.AddSeconds(2))]), "root gone");
            Assert.True(!CoordinatorLogic.SameDecisionStates([St(2, false)], [St(2, false, null, T0)]), "became audible once");
            Assert.True(!CoordinatorLogic.SameDecisionStates(a, [St(1, true, T0, T0.AddSeconds(2), "other.exe"), St(2, false)]),
                "exe changed");
        });

        Program.Add("cpu: title matters only for browsers and titleRegex sources that match on the other criteria", () =>
        {
            var c = new SourceClassifier(ClassTestConfig.Create());
            Assert.True(c.TitleMatters(ClassTestConfig.Browser(null)), "browser");
            Assert.True(!c.TitleMatters(ClassTestConfig.App("warp.exe")), "terminal, no rule uses its title");
            Assert.True(!c.TitleMatters(ClassTestConfig.App("Spotify.exe")), "app matched by exe only");

            var withRegex = new SourceClassifier(ClassTestConfig.Create([
                ClassTestConfig.Src("vlc-concert", SourceKind.App, Categories.Music, exe: ["vlc.exe"], regex: "concert"),
                ClassTestConfig.Src("any-podcast", SourceKind.App, Categories.Podcast, folder: [@"\Podcasts\"], regex: "episode"),
            ]));
            Assert.True(withRegex.TitleMatters(ClassTestConfig.App("vlc.exe")), "exe + titleRegex");
            Assert.True(!withRegex.TitleMatters(ClassTestConfig.App("warp.exe")), "other exe");
            Assert.True(withRegex.TitleMatters(ClassTestConfig.App("player.exe", @"C:\Podcasts\player.exe")), "folder + titleRegex");
            Assert.True(!withRegex.TitleMatters(ClassTestConfig.App("player.exe", @"C:\Apps\player.exe")), "folder does not match");
            // The answer agrees with the classifier: with TitleMatters false, the title never changes the class.
            var noTitle = c.Classify(ClassTestConfig.App("warp.exe"));
            var title = c.Classify(ClassTestConfig.App("warp.exe", title: "x - YouTube"));
            Assert.Equal(noTitle.Category, title.Category, "title does not change the class");
        });

        Program.Add("cpu: process tree walk (replaces a snapshot per tab read)", () =>
        {
            var map = new Dictionary<int, (int ParentPid, string Exe)>
            {
                [10] = (1, "chrome.exe"), [11] = (10, "chrome.exe"), [12] = (11, "chrome.exe"),
                [20] = (1, "Code.exe"), [30] = (31, "a.exe"), [31] = (30, "b.exe"),
            };
            Assert.True(ProcessTreeWalk.IsDescendant(map, 10, 10), "root itself");
            Assert.True(ProcessTreeWalk.IsDescendant(map, 10, 12), "grandchild");
            Assert.True(!ProcessTreeWalk.IsDescendant(map, 10, 20), "other app with the same window class");
            Assert.True(!ProcessTreeWalk.IsDescendant(map, 10, 99), "unknown pid");
            Assert.True(!ProcessTreeWalk.IsDescendant(map, 10, 30), "cycle through reused pids terminates");
        });

        Program.Add("cpu: tab worker uses one combined read (tabs + titles) per request", () =>
        {
            int combined = 0, titlesOnly = 0;
            var sink = new System.Collections.Concurrent.BlockingCollection<(IReadOnlyList<BrowserTab>?, IReadOnlyList<string>)>();
            using var w = new TabReadWorker(_ =>
                {
                    Interlocked.Increment(ref combined);
                    return ((IReadOnlyList<BrowserTab>?)[new BrowserTab("x", true, true)], (IReadOnlyList<string>)["w"]);
                },
                _ => { Interlocked.Increment(ref titlesOnly); return ["w"]; },
                (_, _, tabs, titles) => sink.Add((tabs, titles)), TimeSpan.FromSeconds(2));
            w.Request(7);
            Assert.True(sink.TryTake(out var r, 3000), "answered");
            Assert.True(r.Item1 is { Count: 1 } && r.Item2.Count == 1, "tabs and titles");
            Assert.Equal(1, combined, "one combined read");
            Assert.Equal(0, titlesOnly, "no separate window search");
        });

        Program.Add("cpu: browser tabs re-read when its sessions change or audio resumes after a silence", () =>
        {
            var gap = TimeSpan.FromSeconds(3);
            var before = new List<PlayingState> { St(1, true, T0, T0.AddSeconds(10)), St(2, true, T0, T0.AddSeconds(10)) };
            var after = new List<PlayingState> { St(1, true, T0, T0.AddSeconds(13.5)), St(2, true, T0, T0.AddSeconds(10.5)) };
            Assert.Equal("1", string.Join(",", CoordinatorLogic.ResumedAfterSilence(before, after, gap)), "only root 1 had a 3.5 s silence");
            Assert.Equal(0, CoordinatorLogic.ResumedAfterSilence(before, [St(1, false, null, T0.AddSeconds(13.5))], gap).Count,
                "not playing: ignored");

            static AudioSnapshot Snap(params (int Pid, int Root, bool Active)[] xs)
                => new(T0, "fx", null, xs.Select(x => new SessionSample("fx", x.Pid, x.Root, "chrome.exe", null, null, 0.1f, x.Active)).ToList());
            var a = CoordinatorLogic.SessionSignatures(Snap((11, 10, true), (21, 20, true)), r => r == 10);
            Assert.True(a.ContainsKey(10) && !a.ContainsKey(20), "filtered roots");
            var same = CoordinatorLogic.SessionSignatures(Snap((11, 10, true), (21, 20, false)), r => r == 10);
            Assert.Equal(0, CoordinatorLogic.ChangedSignatures(a, same).Count, "no change");
            var newSession = CoordinatorLogic.SessionSignatures(Snap((11, 10, true), (12, 10, true)), r => r == 10);
            Assert.Equal("10", string.Join(",", CoordinatorLogic.ChangedSignatures(a, newSession)), "new session of the browser");
            var inactive = CoordinatorLogic.SessionSignatures(Snap((11, 10, false)), r => r == 10);
            Assert.Equal("10", string.Join(",", CoordinatorLogic.ChangedSignatures(a, inactive)), "session went inactive");

            // Tracker side: a stale mark while playing makes the browser due at once.
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            t.OnBrowserTitle(100, "chrome.exe", "x - YouTube");
            t.OnPlayingChanged(100, true);
            t.OnTabsRequested(100, 1);
            t.OnTabs(100, [new BrowserTab("x - YouTube", true, true)], 1);
            clock.Advance(20000);
            t.MarkStale(100);
            Assert.True(t.NeedsTabRead(100), "re-read after the change");
        });

        Program.Add("cpu: FxSound liveness: a new PID while running counts as closed + opened", () =>
        {
            Assert.Equal(FxProcessChange.None, CoordinatorLogic.FxChange(true, 10, 10), "same pid");
            Assert.Equal(FxProcessChange.Restarted, CoordinatorLogic.FxChange(true, 10, 11), "new pid");
            Assert.Equal(FxProcessChange.Closed, CoordinatorLogic.FxChange(true, 10, null), "closed");
            Assert.Equal(FxProcessChange.Opened, CoordinatorLogic.FxChange(false, null, 11), "opened");
            Assert.Equal(FxProcessChange.Opened, CoordinatorLogic.FxChange(false, 10, 10), "opened (state was closed)");
            Assert.Equal(FxProcessChange.None, CoordinatorLogic.FxChange(true, null, 11), "first pid seen while running");
            Assert.Equal(FxProcessChange.None, CoordinatorLogic.FxChange(false, null, null), "still closed");
        });

        Program.Add("cpu: adaptive audio sampling: 250 ms while changing, 500 ms once stable 3 s", () =>
        {
            var pace = new FxAuto.Audio.SamplingPace(250);
            static AudioSnapshot Snap(params (int Root, float Peak)[] xs)
                => new(T0, "fx", null, xs.Select(x => new SessionSample("fx", x.Root + 1, x.Root, "a.exe", null, null, x.Peak, true)).ToList());
            long ms = 0;
            int Step(AudioSnapshot s, string key = "k") { int d = pace.Next(ms, s, key); ms += d; return d; }
            Assert.Equal(250, Step(Snap((1, 0.2f))), "start: fast");
            while (ms < 3000) Step(Snap((1, 0.2f)));
            Assert.Equal(500, Step(Snap((1, 0.2f))), "stable for 3 s: slow");
            Assert.Equal(500, Step(Snap((1, 0f))), "a pause shorter than 1 s does not count");
            Assert.Equal(500, Step(Snap((1, 0.2f))), "still slow");
            Assert.Equal(250, Step(Snap((1, 0.2f), (2, 0.2f))), "a new source: fast at once (seen within 500 ms)");
            long changed = ms;
            while (ms - changed < 3000) Step(Snap((1, 0.2f), (2, 0.2f)));
            Assert.Equal(500, Step(Snap((1, 0.2f), (2, 0.2f))), "stable again");
            Step(Snap((1, 0.2f), (2, 0f)));
            Step(Snap((1, 0.2f), (2, 0f)));
            Assert.Equal(250, Step(Snap((1, 0.2f), (2, 0f))), "source 2 silent for over 1 s: fast (stop seen at most 250 ms later)");
            Assert.Equal(250, pace.Next(ms, Snap((1, 0.2f)), "other"), "structure changed: fast");
            var slowPoll = new FxAuto.Audio.SamplingPace(1000);
            Assert.Equal(1000, slowPoll.Next(0, Snap((1, 0.2f)), "k"), "pollMs slower than 500 ms: unchanged");
        });

        Program.Add("cpu: detector keeps Playing and AudibleSince when sampled every 500 ms once stable", () =>
        {
            var clock = new FakeClock();
            var det = new SoundingDetector(new SoundingTimings(), clock);
            DateTimeOffset? since = null;
            for (int t = 0; t <= 20000; t += t < 3000 ? 250 : 500)
            {
                clock.Now = T0.AddMilliseconds(t);
                var st = det.Observe(new AudioSnapshot(clock.Now, "fx", null,
                    [new SessionSample("fx", 5, 5, "a.exe", null, null, 0.2f, true)]), _ => false, _ => false).Single();
                if (t >= 2500) Assert.True(st.Playing, "playing at " + t);
                if (st.Playing) { since ??= st.AudibleSince; Assert.Equal(since, st.AudibleSince, "AudibleSince stable at " + t); }
            }
            // Silence sampled at 500 ms: released ReleaseMs (8 s) after the last audible sample, within one 500 ms step.
            int last = 20000, released = -1;
            for (int t = last + 500; t <= last + 10000; t += 500)
            {
                clock.Now = T0.AddMilliseconds(t);
                var st = det.Observe(new AudioSnapshot(clock.Now, "fx", null,
                    [new SessionSample("fx", 5, 5, "a.exe", null, null, 0f, true)]), _ => false, _ => false).Single();
                if (!st.Playing) { released = t; break; }
            }
            Assert.True(released >= last + 8000 && released <= last + 8500, "release after 8 s (+≤500 ms): " + released);
        });
    }
}
