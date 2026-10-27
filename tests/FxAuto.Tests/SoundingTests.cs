using FxAuto.Core.Audio;

namespace FxAuto.Tests;

/// <summary>Drives a <see cref="SoundingDetector"/> with 250 ms snapshots on a fake clock.</summary>
internal sealed class SoundingRig
{
    public const string Fx = "{fxsound-speakers}";
    public FakeClock Clock { get; } = new();
    public SoundingDetector Det { get; }
    public HashSet<int> Games { get; } = [];
    public HashSet<int> Foreground { get; } = [];
    public IReadOnlyList<PlayingState> Last { get; private set; } = [];

    public SoundingRig(SoundingTimings? t = null) => Det = new SoundingDetector(t ?? new SoundingTimings(), Clock);

    /// <summary>One snapshot with the given (pid, peak) sessions on the FxSound endpoint, then +250 ms.</summary>
    public IReadOnlyList<PlayingState> Step(params (int Pid, float Peak)[] sessions)
    {
        var snap = new AudioSnapshot(Clock.Now, Fx, null,
            sessions.Select(x => new SessionSample(Fx, x.Pid + 1000, x.Pid, "app" + x.Pid + ".exe", null, null, x.Peak, true)).ToList());
        Last = Det.Observe(snap, Games.Contains, Foreground.Contains);
        Clock.Advance(250);
        return Last;
    }

    /// <summary>Runs <paramref name="ms"/> of snapshots; returns the first time (ms from start) pid was playing, or -1.</summary>
    public int Run(int ms, int pid, Func<int, (int Pid, float Peak)[]> at)
    {
        int first = -1;
        for (int t = 0; t < ms; t += 250)
        {
            var st = Step(at(t));
            if (first < 0 && Playing(pid, st)) first = t;
        }
        return first;
    }

    public bool Playing(int pid) => Playing(pid, Last);
    public static bool Playing(int pid, IReadOnlyList<PlayingState> st) => st.Any(s => s.RootPid == pid && s.Playing);
}

internal static class SoundingTests
{
    private const float Loud = 0.2f;

    public static void Register()
    {
        Program.Add("Sounding: foreground source enters after 1.5 s, not before", () =>
        {
            var r = new SoundingRig();
            r.Foreground.Add(1);
            var first = r.Run(3000, 1, _ => [(1, Loud)]);
            Assert.Equal(1500, first, "entry time");
            var st = r.Last.Single(s => s.RootPid == 1);
            Assert.Equal(new FakeClock().Now, st.AudibleSince!.Value, "audible since = start of the run");
        });

        Program.Add("Sounding: background source needs 2 s", () =>
        {
            var r = new SoundingRig();
            Assert.Equal(2000, r.Run(3000, 1, _ => [(1, Loud)]), "entry time");
        });

        Program.Add("Sounding: 1 s notification burst from the background is ignored", () =>
        {
            var r = new SoundingRig();
            Assert.Equal(-1, r.Run(10000, 1, t => [(1, t < 1000 ? Loud : 0f)]), "never playing");
        });

        Program.Add("Sounding: 1.9 s burst from the background is ignored", () =>
        {
            var r = new SoundingRig();
            Assert.Equal(-1, r.Run(10000, 1, t => [(1, t < 1900 ? Loud : 0f)]), "never playing");
        });

        Program.Add("Sounding: muted video (peak 0, session active) is not playing", () =>
        {
            var r = new SoundingRig();
            r.Foreground.Add(1);
            Assert.Equal(-1, r.Run(20000, 1, _ => [(1, 0f)]), "never playing");
            Assert.Equal(-1, r.Run(5000, 1, _ => [(1, 0.002f)]), "below threshold");
        });

        Program.Add("Sounding: intermittent audio under the attack ratio does not enter", () =>
        {
            var r = new SoundingRig();
            r.Foreground.Add(1);
            // audible one sample out of three (33 % < 60 %)
            Assert.Equal(-1, r.Run(10000, 1, t => [(1, t / 250 % 3 == 0 ? Loud : 0f)]), "never playing");
        });

        Program.Add("Sounding: 5 s silence between songs → no release; release after 8 s", () =>
        {
            var r = new SoundingRig();
            r.Run(3000, 1, _ => [(1, Loud)]);
            Assert.True(r.Playing(1), "playing");
            r.Run(5000, 1, _ => [(1, 0f)]);
            Assert.True(r.Playing(1), "still playing after 5 s of silence");
            r.Run(2000, 1, _ => [(1, Loud)]);
            Assert.True(r.Playing(1), "next song");
            r.Run(7750, 1, _ => [(1, 0f)]);
            Assert.True(r.Playing(1), "7.75 s silence: still playing");
            r.Run(500, 1, _ => [(1, 0f)]);
            Assert.True(!r.Playing(1), "released after 8 s");
        });

        Program.Add("Sounding: game silent 15 s (loading screen) keeps playing; released at 20 s", () =>
        {
            var r = new SoundingRig();
            r.Games.Add(7);
            r.Foreground.Add(7);
            r.Run(2000, 7, _ => [(7, Loud)]);
            Assert.True(r.Playing(7), "playing");
            r.Run(15000, 7, _ => [(7, 0f)]);
            Assert.True(r.Playing(7), "still playing after 15 s silence");
            r.Run(5500, 7, _ => []);   // session gone from the snapshot counts as silence
            Assert.True(!r.Playing(7), "released after 20 s");
        });

        Program.Add("Sounding: a source that played recently re-enters in 1.5 s even in the background", () =>
        {
            var r = new SoundingRig();
            r.Run(3000, 1, _ => [(1, Loud)]);
            r.Run(8500, 1, _ => [(1, 0f)]);
            Assert.True(!r.Playing(1), "released");
            Assert.Equal(1500, r.Run(3000, 1, _ => [(1, Loud)]), "re-entry");
        });

        Program.Add("Sounding: only sessions on the FxSound endpoint count; max over the root's sessions", () =>
        {
            var r = new SoundingRig();
            r.Foreground.Add(1);
            r.Foreground.Add(2);
            for (int i = 0; i < 12; i++)
            {
                var snap = new AudioSnapshot(r.Clock.Now, SoundingRig.Fx, null,
                [
                    new SessionSample("{g435-direct}", 11, 1, "Discord.exe", null, null, Loud, true),
                    new SessionSample(SoundingRig.Fx, 21, 2, "chrome.exe", null, null, 0f, true),
                    new SessionSample(SoundingRig.Fx, 22, 2, "chrome.exe", null, null, Loud, true),
                ]);
                r.Det.Observe(snap, _ => false, _ => true);
                r.Clock.Advance(250);
            }
            var st = r.Det.Observe(new AudioSnapshot(r.Clock.Now, SoundingRig.Fx, null, []), _ => false, _ => true);
            Assert.True(!st.Any(s => s.RootPid == 1 && s.Playing), "other endpoint ignored");
            Assert.True(st.Any(s => s.RootPid == 2 && s.Playing && s.Exe == "chrome.exe"), "browser root playing");
        });

        Program.Add("Sounding: inactive session never counts", () =>
        {
            var r = new SoundingRig();
            for (int i = 0; i < 12; i++)
            {
                r.Det.Observe(new AudioSnapshot(r.Clock.Now, SoundingRig.Fx, null,
                    [new SessionSample(SoundingRig.Fx, 11, 1, "a.exe", null, null, Loud, false)]), _ => false, _ => true);
                r.Clock.Advance(250);
            }
            Assert.True(!r.Det.Observe(new AudioSnapshot(r.Clock.Now, SoundingRig.Fx, null, []), _ => false, _ => true)
                .Any(s => s.Playing), "not playing");
        });

        Program.Add("Sounding: NextDueAt = earliest release deadline; release without new snapshots uses the clock", () =>
        {
            var r = new SoundingRig();
            Assert.Null(r.Det.NextDueAt, "nothing pending");
            r.Games.Add(2);
            r.Run(3000, 1, _ => [(1, Loud), (2, Loud)]);
            var lastAudible = r.Last.Single(s => s.RootPid == 1).LastAudibleAt!.Value;
            Assert.Equal(lastAudible.AddMilliseconds(8000), r.Det.NextDueAt!.Value, "media release first");
            // no snapshot for 9 s, then one old-dated snapshot: release still evaluated with the clock
            r.Clock.Advance(9000);
            var st = r.Det.Observe(new AudioSnapshot(lastAudible, SoundingRig.Fx, null, []), _ => false, _ => false);
            Assert.True(!st.Single(s => s.RootPid == 1).Playing, "media released");
        });

        Program.Add("Sounding: thresholds come from the timings (custom attack/release)", () =>
        {
            var r = new SoundingRig(new SoundingTimings(AttackMs: 500, BurstIgnoreMs: 500, ReleaseMs: 1000));
            Assert.Equal(500, r.Run(2000, 1, _ => [(1, Loud)]), "entry at 500 ms");
            r.Run(1250, 1, _ => [(1, 0f)]);
            Assert.True(!r.Playing(1), "released after 1 s");
        });
    }
}
