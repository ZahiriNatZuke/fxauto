using System.Collections.Concurrent;
using System.Diagnostics;
using FxAuto.Browser;
using FxAuto.Core.Classification;

namespace FxAuto.Tests;

/// <summary>Task 4c: tab name parsing and the tab read worker (timeouts, disable, replace, re-enable) with a fake
/// reader. The UIA reader itself is checked live (manual probe), not here.</summary>
internal static class BrowserTabReaderTests
{
    private static readonly string[] Markers = ["reproducción de audio", "Audio playing"];

    private sealed record Result(long Id, int Pid, IReadOnlyList<BrowserTab>? Tabs, IReadOnlyList<string> Titles,
        long AtMs);

    private sealed class Sink
    {
        private readonly BlockingCollection<Result> _q = [];
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public void Post(long id, int pid, IReadOnlyList<BrowserTab>? tabs, IReadOnlyList<string> titles)
            => _q.Add(new Result(id, pid, tabs, titles, _sw.ElapsedMilliseconds));
        public Result Next(int capMs = 3000)
            => _q.TryTake(out var r, capMs) ? r : throw new AssertException($"no result within {capMs} ms");
        public void None(int waitMs)
        {
            if (_q.TryTake(out var r, waitMs)) throw new AssertException($"unexpected result for request {r.Id}");
        }
        public long Now => _sw.ElapsedMilliseconds;
    }

    private static readonly IReadOnlyList<BrowserTab> OneTab = [new BrowserTab("Song - YouTube", true, false)];
    private static IReadOnlyList<string> Titles(int pid) => ["Song - YouTube - Google Chrome"];

    public static void Register()
    {
        Program.Add("Browser tabs: audible Chrome name (Spanish) is cleaned and flagged", () =>
        {
            var t = BrowserTabNames.ToTab("Uso de memoria de Live match - YouTube: reproducción de audio: 629 MB",
                selected: false, Markers);
            Assert.True(t.Audible, "audible");
            Assert.True(!t.Selected, "not selected");
            Assert.True(!t.Title.Contains("reproducción", StringComparison.Ordinal), "marker removed: " + t.Title);
            Assert.True(!t.Title.Contains("MB", StringComparison.Ordinal), "memory size removed: " + t.Title);
            Assert.True(t.Title.EndsWith("YouTube", StringComparison.Ordinal), "site is the last segment: " + t.Title);
        });

        Program.Add("Browser tabs: silent tab and English marker", () =>
        {
            var silent = BrowserTabNames.ToTab("Uso de memoria de Docs - Notes: 116 MB", true, Markers);
            Assert.True(!silent.Audible, "silent tab not audible");
            Assert.True(silent.Selected, "selected kept");
            var en = BrowserTabNames.ToTab("Podcast - Spotify - Audio playing", false, Markers);
            Assert.True(en.Audible, "English marker");
            var custom = BrowserTabNames.ToTab("Podcast - Spotify - Audio playing", false, ["something else"]);
            Assert.True(!custom.Audible, "markers come from config");
        });

        Program.Add("Tab worker: a fast read is delivered with its request id", () =>
        {
            var sink = new Sink();
            using var w = new TabReadWorker(_ => OneTab, Titles, sink.Post, TimeSpan.FromMilliseconds(500));
            long id = w.Request(42);
            var r = sink.Next();
            Assert.Equal(id, r.Id, "request id");
            Assert.Equal(42, r.Pid, "root pid");
            Assert.Equal(1, r.Tabs?.Count ?? -1, "tabs delivered");
            Assert.Equal(1, r.Titles.Count, "window titles delivered");
            Assert.Equal(0, w.ConsecutiveTimeouts, "no timeouts");
        });

        Program.Add("Tab worker: a hung read is abandoned at the deadline and its late result ignored", () =>
        {
            var sink = new Sink();
            using var release = new ManualResetEventSlim(false);
            using var w = new TabReadWorker(_ => { release.Wait(5000); return OneTab; }, Titles, sink.Post,
                TimeSpan.FromMilliseconds(150));
            long start = sink.Now;
            long id = w.Request(7);
            var r = sink.Next();
            Assert.Equal(id, r.Id, "abandoned request answered");
            Assert.Null(r.Tabs, "abandoned → tabs null");
            Assert.Equal(1, r.Titles.Count, "titles still delivered on timeout");
            Assert.True(r.AtMs - start >= 120, $"answered at the deadline, not before ({r.AtMs - start} ms)");
            Assert.Equal(1, w.ConsecutiveTimeouts, "one timeout");
            release.Set();
            sink.None(300);
            Assert.True(!w.IsDisabled, "one timeout does not disable");
        });

        Program.Add("Tab worker: 3 consecutive timeouts disable reading; disabled requests are answered at once", () =>
        {
            var sink = new Sink();
            using var release = new ManualResetEventSlim(false);
            int disabledEvents = 0, reads = 0;
            using var w = new TabReadWorker(_ => { Interlocked.Increment(ref reads); release.Wait(5000); return OneTab; },
                Titles, sink.Post, TimeSpan.FromMilliseconds(100));
            w.Disabled += () => Interlocked.Increment(ref disabledEvents);
            for (int i = 0; i < 3; i++)
            {
                long id = w.Request(1);
                var r = sink.Next();
                Assert.Equal(id, r.Id, $"timeout {i + 1} answered");
                Assert.Null(r.Tabs, "tabs null on timeout");
            }
            Assert.True(w.IsDisabled, "disabled after 3 timeouts");
            Assert.Equal(1, Volatile.Read(ref disabledEvents), "Disabled raised once");
            long t0 = sink.Now;
            long id4 = w.Request(1);
            var r4 = sink.Next(500);
            Assert.Equal(id4, r4.Id, "disabled request answered");
            Assert.Null(r4.Tabs, "disabled → tabs null");
            Assert.Equal(1, r4.Titles.Count, "disabled → window titles still read");
            Assert.True(r4.AtMs - t0 < 80, "answered at once");
            Assert.Equal(1, Volatile.Read(ref reads), "the hung reader was not called again");
            release.Set();
            sink.None(200);
            Assert.Equal(1, Volatile.Read(ref disabledEvents), "still one Disabled event");
        });

        Program.Add("Tab worker: a successful read resets the consecutive timeout count", () =>
        {
            var sink = new Sink();
            int slow = 1;      // 1 = hang until released
            using var release = new ManualResetEventSlim(false);
            using var w = new TabReadWorker(_ =>
            {
                if (Volatile.Read(ref slow) == 1) release.Wait(5000);
                return OneTab;
            }, Titles, sink.Post, TimeSpan.FromMilliseconds(100));
            w.Request(1); Assert.Null(sink.Next().Tabs, "timeout 1");
            w.Request(1); Assert.Null(sink.Next().Tabs, "timeout 2");
            Assert.Equal(2, w.ConsecutiveTimeouts, "two timeouts");
            Volatile.Write(ref slow, 0);
            release.Set();                  // the stuck read returns late (ignored); the second one runs fast
            long id = w.Request(1);
            var r = sink.Next();
            Assert.Equal(id, r.Id, "fresh request answered");
            Assert.True(r.Tabs is not null, "fresh request delivered tabs");
            Assert.Equal(0, w.ConsecutiveTimeouts, "count reset by success");
            Assert.True(!w.IsDisabled, "not disabled");
        });

        Program.Add("Tab worker: a newer request replaces the pending one (one pending at a time)", () =>
        {
            var sink = new Sink();
            using var gate = new ManualResetEventSlim(false);
            var seen = new ConcurrentQueue<int>();
            using var w = new TabReadWorker(pid => { seen.Enqueue(pid); if (pid == 1) gate.Wait(5000); return OneTab; },
                Titles, sink.Post, TimeSpan.FromSeconds(2));
            long a = w.Request(1);
            Thread.Sleep(50);               // a is in flight
            long b = w.Request(2);          // pending
            long c = w.Request(3);          // replaces b
            gate.Set();
            var r1 = sink.Next();
            var r2 = sink.Next();
            Assert.Equal(a, r1.Id, "in-flight request delivered");
            Assert.Equal(c, r2.Id, "newest request delivered");
            sink.None(200);
            Assert.True(!seen.Contains(2), $"replaced request {b} never read");
        });

        Program.Add("Tab worker: Reenable after disable serves requests on a fresh thread", () =>
        {
            var sink = new Sink();
            using var release = new ManualResetEventSlim(false);
            int hang = 1;
            using var w = new TabReadWorker(_ =>
            {
                if (Volatile.Read(ref hang) == 1) release.Wait(5000);
                return OneTab;
            }, Titles, sink.Post, TimeSpan.FromMilliseconds(100));
            for (int i = 0; i < 3; i++) { w.Request(1); sink.Next(); }
            Assert.True(w.IsDisabled, "disabled");
            Volatile.Write(ref hang, 0);    // the old thread stays stuck (release not set yet)
            w.Reenable();
            Assert.True(!w.IsDisabled, "re-enabled");
            long id = w.Request(1);
            var r = sink.Next();
            Assert.Equal(id, r.Id, "served after re-enable");
            Assert.True(r.Tabs is not null, "tabs delivered by the fresh thread");
            release.Set();                  // the old thread returns and exits without posting
            sink.None(200);
        });
    }
}
