using System.Collections.Concurrent;
using System.Diagnostics;
using FxAuto.Core;
using FxAuto.Core.FxSound;

namespace FxAuto.Tests;

internal static class ControllerTests
{
    // Short timings so the tests run fast; the logic is the same.
    private static readonly FxControllerTimings Fast = new()
    {
        PerCommand = TimeSpan.FromSeconds(1),
        Total = TimeSpan.FromSeconds(5),
        StatusPoll = TimeSpan.FromMilliseconds(10),
        StatusWait = TimeSpan.FromMilliseconds(300),
        Retry1 = TimeSpan.FromMilliseconds(30),
        Retry2 = TimeSpan.FromMilliseconds(60),
        PendingPoll = TimeSpan.FromMilliseconds(20),
    };

    private static (FakePort Port, FxSoundController Ctl, ConcurrentQueue<string> Log) Make(FxControllerTimings? t = null)
    {
        var port = new FakePort();
        var log = new ConcurrentQueue<string>();
        var ctl = new FxSoundController(port, SystemClock.Instance, (ev, _) => log.Enqueue(ev), t ?? Fast);
        return (port, ctl, log);
    }

    public static void Register()
    {
        Program.Add("controller: success with verification", async () =>
        {
            var (port, ctl, log) = Make();
            var r = await ctl.ApplyAsync("G435_Video", 1.5, CancellationToken.None);
            Assert.True(r.Ok, "ok: " + r.FailureReason);
            Assert.Equal(1, r.Attempts, "one attempt");
            Assert.Equal("G435_Video", r.Actual, "read");
            Assert.Equal(1.5, port.Leveling, "leveling applied");
            Assert.Equal(2, port.Sent.Count, "command + status");
            Assert.Equal("--power=1 --preset=\"G435_Video\" --volume_leveling=1.5 --run_minimized", port.Sent.First(), "command");
            Assert.True(log.Contains("verify"), "verification logged");
            Assert.True(!ctl.IsBusy || await WaitIdle(ctl), "ends up idle");
        });

        Program.Add("controller: verifies the leveling read from status.json (equalizer.volume_leveling)", async () =>
        {
            var (port, ctl, _) = Make();
            port.Leveling = 0;
            port.IgnoreLeveling = true;   // the preset changes, the leveling stays at 0
            var r = await ctl.ApplyAsync("G435_Video", 1.5, CancellationToken.None);
            Assert.True(!r.Ok, "fails although the preset matches");
            Assert.Equal("G435_Video", r.Actual, "correct preset read");
            Assert.True(r.FailureReason!.Contains("read leveling 0", StringComparison.Ordinal), "reason: " + r.FailureReason);
            Assert.Equal(3, r.Attempts, "retries and gives up");
        });

        Program.Add("controller: preset ignored → 2 retries → failure with reason", async () =>
        {
            var (port, ctl, _) = Make();
            port.IgnorePresetCommands = true;
            var r = await ctl.ApplyAsync("G435_FPS", 0, CancellationToken.None);
            Assert.True(!r.Ok, "fails");
            Assert.Equal(3, r.Attempts, "1 + 2 retries");
            Assert.Equal("G435_General", r.Actual, "read");
            Assert.True(r.FailureReason!.Contains("G435_General", StringComparison.Ordinal), "reason: " + r.FailureReason);
            Assert.Equal(3, port.SentPresets().Count, "three commands, not endless");

            var (port2, ctl2, _) = Make();
            var r2 = await ctl2.ApplyAsync("G435_DoesNotExist", 0, CancellationToken.None);
            Assert.Equal(FxSoundController.ReasonUnknownPreset, r2.FailureReason, "unknown preset");
            Assert.Equal(3, port2.SentPresets().Count, "three attempts");
        });

        Program.Add("controller: FxSound closed → nothing sent", async () =>
        {
            var (port, ctl, _) = Make();
            port.MainRunning = false;
            var r = await ctl.ApplyAsync("G435_FPS", 0, CancellationToken.None);
            Assert.Equal(FxSoundController.ReasonClosed, r.FailureReason, "reason");
            Assert.Equal(0, port.Sent.Count, "nothing sent (not even --status)");
            Assert.Null(await ctl.QueryAsync(CancellationToken.None), "query without FxSound → null");
            var e = await ctl.EnsureAsync("G435_FPS", 0, CancellationToken.None);
            Assert.Equal(FxSoundController.ReasonClosed, e.FailureReason, "watchdog");
            Assert.Equal(0, port.Sent.Count, "nothing sent");
            Assert.Equal(0, port.StartCalls, "does not start it");
        });

        Program.Add("controller: FxSound elevated → nothing sent, elevated reason", async () =>
        {
            var (port, ctl, _) = Make();
            port.Elevated = true;
            var r = await ctl.ApplyAsync("G435_FPS", 0, CancellationToken.None);
            Assert.True(!r.Ok && r.FailureReason!.StartsWith("elevated", StringComparison.Ordinal), "reason: " + r.FailureReason);
            Assert.Equal(0, port.Sent.Count, "nothing sent");
            Assert.Null(await ctl.QueryAsync(CancellationToken.None), "the query does not send either");
            Assert.Equal(0, port.Sent.Count, "nothing sent after querying");
            port.Elevated = null; // unknown: try
            Assert.True((await ctl.ApplyAsync("G435_FPS", 0, CancellationToken.None)).Ok, "unknown elevation → sent");
        });

        Program.Add("controller: timeout → never killed, blocked until it exits", async () =>
        {
            var (port, ctl, _) = Make();
            port.HangNext = true;
            var r = await ctl.ApplyAsync("G435_FPS", 0, CancellationToken.None);
            Assert.Equal(FxSoundController.ReasonTimeout, r.FailureReason, "reason");
            Assert.Equal(1, r.Attempts, "no retries after hanging");
            Assert.True(port.HasPendingLaunched, "hung process remains (not killed)");
            var sentBefore = port.Sent.Count;
            var next = ctl.ApplyAsync("G435_RPG", 0, CancellationToken.None);
            await Task.Delay(250);
            Assert.True(!next.IsCompleted, "waits for it to exit");
            Assert.Equal(sentBefore, port.Sent.Count, "sends nothing meanwhile");
            port.HasPendingLaunched = false; // the hung process exited by itself
            var r2 = await next;
            Assert.True(r2.Ok, "continues after the exit: " + r2.FailureReason);

            // if it never exits: fails within the total timeout
            var (port3, ctl3, _) = Make(Fast with { Total = TimeSpan.FromMilliseconds(400) });
            port3.HasPendingLaunched = true;
            var r3 = await ctl3.ApplyAsync("G435_FPS", 0, CancellationToken.None);
            Assert.Equal(FxSoundController.ReasonBusy, r3.FailureReason, "busy");
            Assert.Equal(0, port3.Sent.Count, "nothing sent");
        });

        Program.Add("controller: two decisions during a command in progress → only the latest is sent", async () =>
        {
            var (port, ctl, _) = Make();
            port.CommandDelay = TimeSpan.FromMilliseconds(80);
            var a = ctl.ApplyAsync("G435_FPS", 0, CancellationToken.None);
            await Task.Delay(20);
            var b = ctl.ApplyAsync("G435_RPG", 0, CancellationToken.None);
            var c = ctl.ApplyAsync("G435_Musica", 0, CancellationToken.None);
            var rb = await b;
            Assert.Equal(FxSoundController.ReasonSuperseded, rb.FailureReason, "the middle one is superseded");
            Assert.True((await a).Ok, "the one in progress finishes");
            Assert.True((await c).Ok, "the latest is applied");
            var presets = port.SentPresets();
            Assert.Equal("G435_FPS,G435_Musica", string.Join(',', presets), "commands sent");
            Assert.Equal("G435_Musica", port.Selected, "the latest stays");
        });

        Program.Add("controller: QueryAsync does not overlap with commands", async () =>
        {
            var (port, ctl, _) = Make();
            port.CommandDelay = TimeSpan.FromMilliseconds(30);
            var tasks = new List<Task>();
            for (var i = 0; i < 4; i++)
            {
                tasks.Add(ctl.QueryAsync(CancellationToken.None));
                tasks.Add(ctl.ApplyAsync(i % 2 == 0 ? "G435_FPS" : "G435_Voz", 0, CancellationToken.None));
                tasks.Add(Task.Run(() => ctl.QueryAsync(CancellationToken.None)));
            }
            await Task.WhenAll(tasks);
            Assert.Equal(1, port.MaxConcurrent, "never two FxSound.exe processes at once");
            var st = await ctl.QueryAsync(CancellationToken.None);
            Assert.True(st is not null, "query returns a state");
        });

        Program.Add("controller: total timeout", async () =>
        {
            var (port, ctl, _) = Make(Fast with { Total = TimeSpan.FromMilliseconds(500), Retry2 = TimeSpan.FromMilliseconds(400) });
            port.StatusNeverUpdates = true; // FxSound exits from --status but does not write the file
            var sw = Stopwatch.StartNew();
            var r = await ctl.ApplyAsync("G435_FPS", 0, CancellationToken.None);
            Assert.True(!r.Ok, "fails");
            Assert.True(r.FailureReason == FxSoundController.ReasonTotal || r.FailureReason == FxSoundController.ReasonNoStatus,
                "reason: " + r.FailureReason);
            Assert.True(sw.ElapsedMilliseconds < 1500, $"respects the timeout ({sw.ElapsedMilliseconds} ms)");
        });

        Program.Add("controller: EnsureAsync re-applies only if it differs", async () =>
        {
            var (port, ctl, _) = Make();
            port.Selected = "G435_FPS";
            var same = await ctl.EnsureAsync("G435_FPS", 0, CancellationToken.None);
            Assert.True(same.Ok && same.Attempts == 0, "already active: no command");
            Assert.Equal(0, port.SentPresets().Count, "only --status");
            port.Selected = "G435_Musica"; // FxSound overrode the preset
            var fixedUp = await ctl.EnsureAsync("G435_FPS", 0, CancellationToken.None);
            Assert.True(fixedUp.Ok && fixedUp.Attempts == 1, "re-applied");
            Assert.Equal("G435_FPS", port.Selected, "back to FPS");
            port.Leveling = 1.5; // same preset, different leveling → also re-applies
            var lv = await ctl.EnsureAsync("G435_FPS", 0, CancellationToken.None);
            Assert.True(lv.Ok && lv.Attempts == 1, "leveling re-applied");
            Assert.Equal(0.0, port.Leveling, "leveling 0");
        });
        Program.Add("controller: EnsureAsync with FxSound not responding → no commands", async () =>
        {
            var (port, ctl, _) = Make();
            port.StatusNeverUpdates = true; // --status exits fine but status.json does not change (FxSound hung)
            var r = await ctl.EnsureAsync("G435_FPS", 0, CancellationToken.None);
            Assert.True(!r.Ok, "fails");
            Assert.Equal(FxSoundController.ReasonNoStatus, r.FailureReason, "reason");
            Assert.Equal(0, port.SentPresets().Count, "no preset command, only the --status");
            Assert.Equal(1, port.Sent.Count, "a single --status");

            var (port2, ctl2, _) = Make();
            port2.HangNext = true; // the --status hangs
            var r2 = await ctl2.EnsureAsync("G435_FPS", 0, CancellationToken.None);
            Assert.Equal(FxSoundController.ReasonTimeout, r2.FailureReason, "status hung");
            Assert.Equal(0, port2.SentPresets().Count, "no commands");
        });

        RegisterExclusive();
    }

    // ---- RunExclusiveAsync (task 3c) ----
    private static void RegisterExclusive()
    {
        Program.Add("exclusive: blocks an engine apply and releases it afterwards", async () =>
        {
            var (port, ctl, _) = Make();
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var op = ctl.RunExclusiveAsync(async s =>
            {
                entered.TrySetResult();
                await s.SendAsync(FxCommand.SelectPreset("G435_FPS"), CancellationToken.None);
                await hold.Task;
                return await s.QueryAsync(CancellationToken.None);
            }, TimeSpan.FromSeconds(10), CancellationToken.None);
            await entered.Task;
            var apply = ctl.ApplyAsync("G435_Video", 0, CancellationToken.None);
            await Task.Delay(80);
            Assert.True(!apply.IsCompleted, "apply held while the session runs");
            Assert.Equal(0, port.Sent.Count(a => a.StartsWith("--power=1", StringComparison.Ordinal)), "apply not sent yet");
            hold.SetResult();
            var st = await op;
            Assert.Equal("G435_FPS", st?.SelectedPreset, "session result");
            var r = await apply;
            Assert.True(r.Ok, "apply ok afterwards: " + r.FailureReason);
            Assert.Equal("G435_Video", port.Selected, "apply ran after the session");
            Assert.Equal(1, port.MaxConcurrent, "never two commands at once");
        });

        Program.Add("exclusive: the newest apply requested meanwhile wins", async () =>
        {
            var (port, ctl, _) = Make();
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var op = ctl.RunExclusiveAsync(async _ => { await hold.Task; return 0; }, TimeSpan.FromSeconds(10), CancellationToken.None);
            await Task.Delay(20);
            var a1 = ctl.ApplyAsync("G435_FPS", 0, CancellationToken.None);
            var a2 = ctl.ApplyAsync("G435_Cine", 0, CancellationToken.None);
            hold.SetResult();
            await op;
            Assert.Equal(FxSoundController.ReasonSuperseded, (await a1).FailureReason, "first superseded");
            Assert.True((await a2).Ok, "latest applied");
            Assert.Equal("G435_Cine", string.Join(",", port.SentPresets()), "only the latest sent");
        });

        Program.Add("exclusive: waits for the command in flight before starting", async () =>
        {
            var (port, ctl, _) = Make();
            port.CommandDelay = TimeSpan.FromMilliseconds(60);
            var apply = ctl.ApplyAsync("G435_FPS", 0, CancellationToken.None);
            await Task.Delay(10);
            var sentAtStart = -1;
            await ctl.RunExclusiveAsync(_ => { sentAtStart = port.Sent.Count; return Task.FromResult(0); },
                TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.True(apply.IsCompleted && (await apply).Ok, "apply finished first");
            Assert.True(sentAtStart >= 2, $"apply + status done before the body ({sentAtStart})");
        });

        Program.Add("exclusive: timeout → TimeoutException, session closed, controller still usable", async () =>
        {
            var (port, ctl, _) = Make();
            IFxSession? kept = null;
            var never = new TaskCompletionSource<int>();
            var timedOut = false;
            try
            {
                await ctl.RunExclusiveAsync(s => { kept = s; return never.Task; }, TimeSpan.FromMilliseconds(100), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                timedOut = true;
            }
            Assert.True(timedOut, "timed out");
            var late = await kept!.SendAsync(FxCommand.SelectPreset("G435_FPS"), CancellationToken.None);
            Assert.Null(late, "a closed session sends nothing");
            Assert.Equal(0, port.SentPresets().Count, "nothing sent");
            Assert.True((await ctl.ApplyAsync("G435_Video", 0, CancellationToken.None)).Ok, "controller usable");
        });

        Program.Add("exclusive: cancel while queued; body exceptions reach the caller", async () =>
        {
            var (port, ctl, _) = Make();
            port.CommandDelay = TimeSpan.FromMilliseconds(80);
            var apply = ctl.ApplyAsync("G435_FPS", 0, CancellationToken.None);
            await Task.Delay(20); // the apply is in flight: the session queues behind it
            using var cts = new CancellationTokenSource();
            var ran = false;
            var op = ctl.RunExclusiveAsync(_ => { ran = true; return Task.FromResult(1); }, TimeSpan.FromSeconds(5), cts.Token);
            await cts.CancelAsync();
            var canceled = false;
            try
            {
                await op;
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            Assert.True(canceled, "canceled");
            await apply;
            await WaitIdle(ctl);
            Assert.True(!ran, "the body never ran");
            var message = "";
            try
            {
                await ctl.RunExclusiveAsync<int>(_ => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (InvalidOperationException ex)
            {
                message = ex.Message;
            }
            Assert.Equal("boom", message, "exception propagated");
            Assert.True((await ctl.ApplyAsync("G435_Video", 0, CancellationToken.None)).Ok, "loop alive");
        });

        Program.Add("exclusive: FxSound closed → the session sends nothing (a command would start it)", async () =>
        {
            var (port, ctl, _) = Make();
            port.MainRunning = false;
            var (exit, st) = await ctl.RunExclusiveAsync(async s =>
                (await s.SendAsync(FxCommand.SelectPreset("G435_FPS"), CancellationToken.None), await s.QueryAsync(CancellationToken.None)),
                TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Null(exit, "not sent");
            Assert.Null(st, "no status");
            Assert.Equal(0, port.Sent.Count, "nothing at all");
        });
    }

    private static async Task<bool> WaitIdle(FxSoundController ctl)
    {
        for (var i = 0; i < 50 && ctl.IsBusy; i++) await Task.Delay(10);
        return !ctl.IsBusy;
    }
}
