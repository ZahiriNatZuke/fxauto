using System.Diagnostics;

namespace FxAuto.Core.FxSound;

/// <summary>Controller timings (defaults from the plan; measured in docs/pruebas-fxsound.md: command ~70 ms,
/// switch + verification ~217 ms).</summary>
public sealed record FxControllerTimings
{
    public TimeSpan PerCommand { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan Total { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan StatusPoll { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan StatusWait { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan Retry1 { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan Retry2 { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Poll interval while waiting for a hung launched process to exit.</summary>
    public TimeSpan PendingPoll { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>Leveling tolerance when verifying.</summary>
    public double LevelingTolerance { get; init; } = 0.05;
    /// <summary>Upper bound of one exclusive session (RunExclusiveAsync), whatever the caller asks.</summary>
    public TimeSpan ExclusiveMax { get; init; } = TimeSpan.FromSeconds(60);

    public static readonly FxControllerTimings Default = new();
}

/// <summary>Applies and verifies presets. Thread-safe: one command in progress + one pending that wins.</summary>
/// <remarks>
/// Never kills or relaunches FxSound. Sends nothing if FxSound is closed (a command would start it) or elevated
/// (a non-elevated command opens a second FxSound instance, see tests 3.10).
/// </remarks>
public sealed class FxSoundController
{
    public const string ReasonSuperseded = "superseded by a newer command";
    public const string ReasonClosed = "FxSound closed";
    public const string ReasonElevated = "elevated: FxSound runs as administrator and FxAuto does not; nothing is sent";
    public const string ReasonTimeout = "FxSound not responding (command timeout reached)";
    public const string ReasonBusy = "FxSound not responding (a previous command has not finished)";
    public const string ReasonTotal = "total timeout reached";
    public const string ReasonUnknownPreset = "preset does not exist in FxSound";
    public const string ReasonNoStatus = "FxSound not responding (status.json was not updated)";
    public const string ReasonCanceled = "canceled";

    /// <summary>Reasons meaning FxSound is open but does not answer (hung, e.g. by a dialog).</summary>
    public static bool IsUnresponsive(string? reason)
        => reason is ReasonTimeout or ReasonBusy or ReasonNoStatus or ReasonTotal;

    private enum Kind { Apply, Ensure }

    private sealed class Request(Kind kind, string preset, double leveling, CancellationToken ct)
    {
        public Kind Kind { get; } = kind;
        public string Preset { get; } = preset;
        public double Leveling { get; } = leveling;
        public CancellationToken Ct { get; } = ct;
        public TaskCompletionSource<ApplyResult> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class QueryWaiter(CancellationToken ct)
    {
        public CancellationToken Ct { get; } = ct;
        public TaskCompletionSource<FxStatus?> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly IFxSoundPort _port;
    private readonly IClock _clock;
    private readonly Action<string, object> _log;
    private readonly FxControllerTimings _t;
    private readonly object _gate = new();
    private Request? _pending;
    private readonly List<QueryWaiter> _queries = [];
    private readonly Queue<Func<Task>> _exclusives = new();
    private bool _busy;

    public FxSoundController(IFxSoundPort port, IClock clock, Action<string, object> log,
                             FxControllerTimings? timings = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _t = timings ?? FxControllerTimings.Default;
    }

    public Task<ApplyResult> ApplyAsync(string preset, double leveling, CancellationToken ct)
        => EnqueueApply(new Request(Kind.Apply, preset, leveling, ct));

    /// <summary>Queries the state and re-applies if it differs.</summary>
    public Task<ApplyResult> EnsureAsync(string preset, double leveling, CancellationToken ct)
        => EnqueueApply(new Request(Kind.Ensure, preset, leveling, ct));

    /// <summary>Reads the state (same lock). null if FxSound is closed, elevated or not responding.</summary>
    public Task<FxStatus?> QueryAsync(CancellationToken ct)
    {
        var w = new QueryWaiter(ct);
        bool start;
        lock (_gate)
        {
            _queries.Add(w);
            start = !_busy;
            _busy = true;
        }
        if (start) _ = Task.Run(RunLoopAsync, CancellationToken.None);
        return w.Tcs.Task;
    }

    /// <summary>A command is in progress or pending.</summary>
    public bool IsBusy
    {
        get { lock (_gate) return _busy; }
    }

    /// <summary>Runs <paramref name="body"/> with FxSound taken exclusively: it waits for the command in flight,
    /// then no engine command, watchdog or query interleaves until the body ends. Apply/Ensure requests made meanwhile
    /// are held (the newest pending still wins) and run afterwards. One session per preset step list;
    /// <paramref name="timeout"/> = 15 s × expected commands, capped by
    /// <see cref="FxControllerTimings.ExclusiveMax"/>. When the timeout passes or <paramref name="ct"/> is canceled,
    /// the session stops sending (its calls return null) and the caller gets a <see cref="TimeoutException"/> or
    /// an <see cref="OperationCanceledException"/> even if the body has not returned yet.</summary>
    public Task<T> RunExclusiveAsync<T>(Func<IFxSession, Task<T>> body, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (timeout <= TimeSpan.Zero || timeout > _t.ExclusiveMax) timeout = _t.ExclusiveMax;
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled(ct);
            return tcs.Task;
        }
        // canceled while queued (behind a command in flight): the caller is released at once, the body never runs
        var queued = ct.Register(() => tcs.TrySetCanceled(ct));

        async Task Run()
        {
            await queued.DisposeAsync().ConfigureAwait(false);
            if (tcs.Task.IsCompleted || ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            var session = new Session(this, _clock.Now + timeout, limit.Token);
            _log("exclusive", new { begin = true, timeoutMs = (long)timeout.TotalMilliseconds });
            var sw = Stopwatch.StartNew();
            try
            {
                var work = Task.Run(() => body(session), CancellationToken.None);
                var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var reg = limit.Token.Register(() => stop.TrySetResult());
                var first = await Task.WhenAny(work, stop.Task).ConfigureAwait(false);
                if (first == work)
                {
                    tcs.TrySetResult(await work.ConfigureAwait(false));
                }
                else
                {
                    // the session is closed (no more commands reach FxSound); the body is no longer awaited
                    session.Close();
                    _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct);
                    else tcs.TrySetException(Expired(timeout));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                tcs.TrySetException(Expired(timeout));
            }
#pragma warning disable CA1031 // the body's failure goes to the caller; the loop must not die
            catch (Exception ex)
#pragma warning restore CA1031
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                session.Close();
                _log("exclusive", new { end = true, ms = sw.ElapsedMilliseconds, calls = session.Calls });
            }
        }

        bool start;
        lock (_gate)
        {
            _exclusives.Enqueue(Run);
            start = !_busy;
            _busy = true;
        }
        if (start) _ = Task.Run(RunLoopAsync, CancellationToken.None);
        return tcs.Task;
    }

    private static TimeoutException Expired(TimeSpan timeout)
        => new($"exclusive FxSound session timeout ({timeout.TotalSeconds:0.#} s) reached");

    /// <summary>Session handed to an exclusive body. Same safety rules as the engine path: nothing is sent to a
    /// closed or elevated FxSound, a hung launched process is waited for and never killed.</summary>
    private sealed class Session(FxSoundController owner, DateTimeOffset deadline, CancellationToken limit) : IFxSession
    {
        private volatile bool _closed;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Close() => _closed = true;

        public async Task<int?> SendAsync(string args, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(args);
            var c = owner;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit);
            if (_closed || linked.IsCancellationRequested) return null;
            string? refused = !c._port.IsMainRunning() ? ReasonClosed : c._port.IsElevated() == true ? ReasonElevated : null;
            if (refused is not null)
            {
                c._log("exclusive-send", new { cmd = args, sent = false, reason = refused });
                return null;
            }
            try
            {
                if (!await c.WaitNoPendingAsync(deadline, linked.Token).ConfigureAwait(false))
                {
                    c._log("exclusive-send", new { cmd = args, sent = false, reason = ReasonBusy });
                    return null;
                }
                // narrow the check→send race (a command to a closed FxSound would start it)
                if (_closed || !c._port.IsMainRunning()) return null;
                Interlocked.Increment(ref _calls);
                var sw = Stopwatch.StartNew();
                var exit = await c._port.SendAsync(args, Shorter(c._t.PerCommand, deadline - c._clock.Now), linked.Token)
                    .ConfigureAwait(false);
                c._log("exclusive-send", new { cmd = args, exit, ms = sw.ElapsedMilliseconds });
                return exit;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null; // session timeout
            }
        }

        public async Task<FxStatus?> QueryAsync(CancellationToken ct)
        {
            var c = owner;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit);
            if (_closed || linked.IsCancellationRequested) return null;
            if (!c._port.IsMainRunning() || c._port.IsElevated() == true) return null;
            try
            {
                var dl = Earlier(c._clock.Now + c._t.Total, deadline);
                if (!await c.WaitNoPendingAsync(dl, linked.Token).ConfigureAwait(false)) return null;
                if (_closed) return null;
                Interlocked.Increment(ref _calls);
                return await c.ReadStatusAsync(dl, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null; // session timeout
            }
        }
    }

    private Task<ApplyResult> EnqueueApply(Request r)
    {
        ArgumentNullException.ThrowIfNull(r.Preset);
        Request? old;
        bool start;
        lock (_gate)
        {
            old = _pending;
            _pending = r;
            start = !_busy;
            _busy = true;
        }
        if (old is not null)
        {
            _log("superseded", new { preset = old.Preset, by = r.Preset });
            old.Tcs.TrySetResult(new ApplyResult(false, old.Preset, null, ReasonSuperseded, 0, 0));
        }
        if (start) _ = Task.Run(RunLoopAsync, CancellationToken.None);
        return r.Tcs.Task;
    }

    // A single loop at a time: first the pending command (the latest wins), then the grouped queries.
    private async Task RunLoopAsync()
    {
        while (true)
        {
            Request? r;
            List<QueryWaiter>? qs = null;
            Func<Task>? exclusive = null;
            lock (_gate)
            {
                if (_exclusives.Count > 0) exclusive = _exclusives.Dequeue();
            }
            if (exclusive is not null)
            {
                // pending Apply/Ensure and queries stay queued until the exclusive body ends
                await exclusive().ConfigureAwait(false);
                continue;
            }
            lock (_gate)
            {
                r = _pending;
                _pending = null;
                if (r is null && _queries.Count > 0)
                {
                    qs = [.. _queries];
                    _queries.Clear();
                }
                if (r is null && qs is null)
                {
                    if (_exclusives.Count > 0) continue;
                    _busy = false;
                    return;
                }
            }

            if (r is not null)
            {
                ApplyResult res;
                try
                {
                    res = await ExecuteAsync(r).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    res = new ApplyResult(false, r.Preset, null, ReasonCanceled, 0, 0);
                }
#pragma warning disable CA1031 // the loop must not die: any port failure becomes a result
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _log("error", new { preset = r.Preset, error = ex.Message });
                    res = new ApplyResult(false, r.Preset, null, "error: " + ex.Message, 0, 0);
                }
                r.Tcs.TrySetResult(res);
            }
            else
            {
                FxStatus? st = null;
                try
                {
                    st = await QueryCoreAsync(_clock.Now + _t.Total, CancellationToken.None, qs!).ConfigureAwait(false);
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _log("error", new { query = true, error = ex.Message });
                }
                foreach (var q in qs!) q.Tcs.TrySetResult(st);
            }
        }
    }

    private async Task<FxStatus?> QueryCoreAsync(DateTimeOffset deadline, CancellationToken ct, List<QueryWaiter> qs)
    {
        if (qs.TrueForAll(q => q.Ct.IsCancellationRequested)) return null;
        if (!_port.IsMainRunning() || _port.IsElevated() == true) return null;
        if (!await WaitNoPendingAsync(deadline, ct).ConfigureAwait(false)) return null;
        return await ReadStatusAsync(deadline, ct).ConfigureAwait(false);
    }

    private async Task<ApplyResult> ExecuteAsync(Request r)
    {
        var sw = Stopwatch.StartNew();
        var start = _clock.Now;
        var deadline = start + _t.Total;
        var ct = r.Ct;
        ApplyResult Fail(string reason, string? actual, int attempts)
        {
            _log("failure", new { preset = r.Preset, reason, read = actual, attempts, ms = sw.ElapsedMilliseconds });
            return new ApplyResult(false, r.Preset, actual, reason, attempts, sw.ElapsedMilliseconds);
        }

        if (ct.IsCancellationRequested) return Fail(ReasonCanceled, null, 0);
        if (!_port.IsMainRunning()) return Fail(ReasonClosed, null, 0);
        if (_port.IsElevated() == true) return Fail(ReasonElevated, null, 0);

        FxStatus? last = null;
        if (r.Kind == Kind.Ensure)
        {
            if (!await WaitNoPendingAsync(deadline, ct).ConfigureAwait(false)) return Fail(ReasonBusy, null, 0);
            last = await ReadStatusAsync(deadline, ct).ConfigureAwait(false);
            if (last is null)
            {
                // FxSound does not answer --status: re-applying would send commands that can never be verified.
                if (!_port.IsMainRunning()) return Fail(ReasonClosed, null, 0);
                return Fail(_port.HasPendingLaunched ? ReasonTimeout : ReasonNoStatus, null, 0);
            }
            if (Matches(last, r, out _))
            {
                _log("watchdog", new { preset = r.Preset, ok = true, ms = sw.ElapsedMilliseconds });
                return new ApplyResult(true, r.Preset, last.SelectedPreset, null, 0, sw.ElapsedMilliseconds);
            }
            _log("watchdog", new { preset = r.Preset, ok = false, read = last?.SelectedPreset });
        }

        string? reason = null;
        var attempts = 0;
        TimeSpan[] retryDelays = [TimeSpan.Zero, _t.Retry1, _t.Retry2];
        foreach (var delay in retryDelays)
        {
            if (delay > TimeSpan.Zero)
            {
                if (!await DelayWithinAsync(delay, deadline, ct).ConfigureAwait(false)) return Fail(ReasonTotal, last?.SelectedPreset, attempts);
                // FxSound may have closed or become elevated between attempts
                if (!_port.IsMainRunning()) return Fail(ReasonClosed, last?.SelectedPreset, attempts);
                if (_port.IsElevated() == true) return Fail(ReasonElevated, last?.SelectedPreset, attempts);
            }
            if (_clock.Now >= deadline) return Fail(ReasonTotal, last?.SelectedPreset, attempts);
            if (!await WaitNoPendingAsync(deadline, ct).ConfigureAwait(false)) return Fail(ReasonBusy, last?.SelectedPreset, attempts);

            // Check→send race: if the user closes FxSound between the check and the send, the command would open
            // it. The window is milliseconds and accepted; it is checked again right before to narrow it.
            if (!_port.IsMainRunning()) return Fail(ReasonClosed, last?.SelectedPreset, attempts);
            attempts++;
            var args = FxCommand.Apply(r.Preset, r.Leveling);
            var timeout = Shorter(_t.PerCommand, deadline - _clock.Now);
            var sent = Stopwatch.StartNew();
            var exit = await _port.SendAsync(args, timeout, ct).ConfigureAwait(false);
            _log("apply", new { cmd = args, attempt = attempts, exit, ms = sent.ElapsedMilliseconds });
            if (exit is null) return Fail(ReasonTimeout, last?.SelectedPreset, attempts); // never killed; HasPendingLaunched blocks

            last = await ReadStatusAsync(deadline, ct).ConfigureAwait(false);
            if (last is null)
            {
                reason = ReasonNoStatus;
                continue;
            }
            if (Matches(last, r, out var why))
            {
                if (last.SelectedModified == true)
                    _log("warning", new { preset = r.Preset, warning = "the preset shows as modified in FxSound" });
                _log("verify", new { expected = r.Preset, read = last.SelectedPreset, ok = true, attempts, ms = sw.ElapsedMilliseconds });
                return new ApplyResult(true, r.Preset, last.SelectedPreset, null, attempts, sw.ElapsedMilliseconds);
            }
            _log("verify", new { expected = r.Preset, read = last.SelectedPreset, ok = false, reason = why, attempts });
            reason = why;
        }

        // Probable reason
        if (last is not null && !last.UserPresets.Concat(last.BuiltInPresets).Contains(r.Preset, StringComparer.Ordinal)
            && (last.UserPresets.Count + last.BuiltInPresets.Count) > 0)
            reason = ReasonUnknownPreset;
        else if (_port.IsElevated() == true)
            reason = ReasonElevated;
        return Fail(reason ?? ReasonNoStatus, last?.SelectedPreset, attempts);
    }

    private bool Matches(FxStatus st, Request r, out string? why)
    {
        why = null;
        if (!string.Equals(st.SelectedPreset, r.Preset, StringComparison.Ordinal)) why = $"read preset {st.SelectedPreset ?? "(none)"}";
        else if (!st.Power) why = "processing off";
        else if (st.VolumeLeveling is double lv && Math.Abs(lv - r.Leveling) > _t.LevelingTolerance)
            why = $"read leveling {FxCommand.FormatNumber(lv)}";
        return why is null;
    }

    /// <summary>Requests --status and waits for a status.json write time different from the previous one and after the send.</summary>
    private async Task<FxStatus?> ReadStatusAsync(DateTimeOffset deadline, CancellationToken ct)
    {
        if (!_port.IsMainRunning()) return null; // never --status against a closed FxSound: it would start it
        var (_, t0) = _port.ReadStatusFile();
        var sentAt = _clock.Now;
        var exit = await _port.SendAsync(FxCommand.Status, Shorter(_t.PerCommand, deadline - _clock.Now), ct).ConfigureAwait(false);
        if (exit is null)
        {
            _log("status", new { ok = false, reason = "timeout reached" });
            return null;
        }
        var waitUntil = Earlier(_clock.Now + _t.StatusWait, deadline);
        var rereads = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (json, w) = _port.ReadStatusFile();
            if (json is not null && w is DateTimeOffset written && written != t0 && written >= sentAt)
            {
                var st = FxStatusParser.TryParse(json, written);
                var (_, w2) = _port.ReadStatusFile();
                if (st is not null && w2 == written) return st;
                if (++rereads >= 3)
                {
                    _log("status", new { ok = false, reason = "status.json unreadable or changing" });
                    return null;
                }
            }
            if (_clock.Now >= waitUntil) return null;
            await Task.Delay(_t.StatusPoll, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for a hung launched process to exit (never killed). false if the deadline passes.</summary>
    private async Task<bool> WaitNoPendingAsync(DateTimeOffset deadline, CancellationToken ct)
    {
        while (_port.HasPendingLaunched)
        {
            if (_clock.Now >= deadline) return false;
            await Task.Delay(_t.PendingPoll, ct).ConfigureAwait(false);
        }
        return true;
    }

    private async Task<bool> DelayWithinAsync(TimeSpan delay, DateTimeOffset deadline, CancellationToken ct)
    {
        if (_clock.Now + delay >= deadline) return false;
        await Task.Delay(delay, ct).ConfigureAwait(false);
        return true;
    }

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b)
    {
        var m = a < b ? a : b;
        return m < TimeSpan.Zero ? TimeSpan.Zero : m;
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
