using FxAuto.Core.Classification;

namespace FxAuto.Browser;

// Platform-neutral on purpose (no WinForms, no UIA, no P/Invoke): this file is also compiled into
// tests/FxAuto.Tests (linked) so the timeout / disable logic is unit-tested with a fake reader.

/// <summary>Turns a UIA tab accessible name into a <see cref="BrowserTab"/> (Title cleaned, Audible by markers).</summary>
internal static class BrowserTabNames
{
    public static BrowserTab ToTab(string accessibleName, bool selected, IReadOnlyList<string> markers)
    {
        ArgumentNullException.ThrowIfNull(accessibleName);
        ArgumentNullException.ThrowIfNull(markers);
        return new BrowserTab(TitleSegments.CleanTabName(accessibleName, markers),
            TitleSegments.IsAudibleTabName(accessibleName, markers), selected);
    }
}

/// <summary>Pure parent-chain walk over a process snapshot (platform-neutral; used by BrowserWindows.Find).</summary>
internal static class ProcessTreeWalk
{
    /// <summary>True when following the parents of <paramref name="pid"/> reaches <paramref name="rootPid"/>
    /// (bounded: snapshots can contain cycles through reused PIDs).</summary>
    public static bool IsDescendant(IReadOnlyDictionary<int, (int ParentPid, string Exe)> parents, int rootPid, int pid)
    {
        ArgumentNullException.ThrowIfNull(parents);
        int cur = pid;
        for (int i = 0; i < 64; i++)
        {
            if (cur == rootPid) return true;
            if (!parents.TryGetValue(cur, out var p) || p.ParentPid == cur || p.ParentPid <= 0) return false;
            cur = p.ParentPid;
        }
        return false;
    }
}

/// <summary>
/// Runs tab reads on its own dedicated MTA thread (plan "Threading model", spike outcomes 4c):
/// one pending request at a time (a newer request replaces the pending one, which gets no result);
/// a request not answered within <c>abandonAfter</c> (1 s in production) is abandoned: its result is posted at
/// that moment with <c>Tabs = null</c> and the late UIA result is ignored. After <c>maxConsecutiveTimeouts</c> (3)
/// consecutive timeouts tab reading is disabled (requests are answered at once with <c>Tabs = null</c> and the
/// window titles) until <see cref="Reenable"/> (Settings) or a restart. A hung reader thread is left behind:
/// <see cref="Reenable"/> starts a fresh one.
/// </summary>
internal sealed class TabReadWorker : IDisposable
{
    /// <summary>Result callback. Called on the reader thread, the watchdog timer thread or the caller thread
    /// (disabled case); it must only enqueue.</summary>
    public delegate void ResultHandler(long requestId, int rootPid, IReadOnlyList<BrowserTab>? tabs,
        IReadOnlyList<string> windowTitles);

    private sealed record PendingRead(long Id, int RootPid, long DeadlineMs);

    private readonly Func<int, (IReadOnlyList<BrowserTab>? Tabs, IReadOnlyList<string> Titles)> _read;
    private readonly Func<int, IReadOnlyList<string>> _windowTitles;
    private readonly ResultHandler _post;
    private readonly long _abandonMs;
    private readonly int _maxTimeouts;
    private readonly Func<long> _nowMs;

    private readonly object _gate = new();
    private readonly List<PendingRead> _outstanding = [];    // pending + in flight, not answered yet (≤ 2)
    private readonly System.Threading.Timer _watchdog;
    private PendingRead? _pending;
    private long _nextId;
    private int _generation;
    private bool _busy;                                  // the current-generation thread is inside a read
    private bool _disabled;
    private bool _disposed;
    private int _consecutiveTimeouts;

    public TabReadWorker(Func<int, IReadOnlyList<BrowserTab>?> readTabs, Func<int, IReadOnlyList<string>> windowTitles,
        ResultHandler post, TimeSpan abandonAfter, int maxConsecutiveTimeouts = 3, Func<long>? nowMs = null)
        : this(SeparateReads(readTabs, windowTitles), windowTitles, post, abandonAfter, maxConsecutiveTimeouts, nowMs)
    {
    }

    /// <summary>Same, with one call that returns the tabs and the window titles together (one window search per
    /// read). <paramref name="windowTitles"/> is still used for the timeout and disabled answers.</summary>
    public TabReadWorker(Func<int, (IReadOnlyList<BrowserTab>? Tabs, IReadOnlyList<string> Titles)> read,
        Func<int, IReadOnlyList<string>> windowTitles, ResultHandler post, TimeSpan abandonAfter,
        int maxConsecutiveTimeouts = 3, Func<long>? nowMs = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(windowTitles);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConsecutiveTimeouts, 1);
        _read = read;
        _windowTitles = windowTitles;
        _post = post;
        _abandonMs = Math.Max(1, (long)abandonAfter.TotalMilliseconds);
        _maxTimeouts = maxConsecutiveTimeouts;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _watchdog = new System.Threading.Timer(_ => OnWatchdog(), null, Timeout.Infinite, Timeout.Infinite);
        StartThread();
    }

    /// <summary>Raised once when the reader gets disabled after consecutive timeouts (not on the coordinator
    /// thread: it must only enqueue).</summary>
    public event Action? Disabled;

    public bool IsDisabled { get { lock (_gate) return _disabled; } }

    public int ConsecutiveTimeouts { get { lock (_gate) return _consecutiveTimeouts; } }

    /// <summary>Asks for the tabs of the browser rooted at <paramref name="rootPid"/>. Returns the request id
    /// that the result will carry.</summary>
    public long Request(int rootPid)
    {
        bool answerNow;
        long id;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id = ++_nextId;
            answerNow = _disabled;
            if (!answerNow)
            {
                if (_pending is not null) _outstanding.Remove(_pending);   // replaced: no result for it
                _pending = new PendingRead(id, rootPid, _nowMs() + _abandonMs);
                _outstanding.Add(_pending);
                ArmWatchdog();
                Monitor.PulseAll(_gate);
            }
        }
        if (answerNow) _post(id, rootPid, null, SafeTitles(rootPid));
        return id;
    }

    /// <summary>Re-enables tab reading after it was disabled (Settings > General). Starts a fresh reader thread if
    /// the current one is still stuck in a read.</summary>
    public void Reenable()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disabled = false;
            _consecutiveTimeouts = 0;
            if (_busy)
            {
                _generation++;          // the stuck thread exits (without posting) whenever it returns
                _busy = false;
                StartThreadLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = null;
            _outstanding.Clear();
            Monitor.PulseAll(_gate);
        }
        _watchdog.Dispose();
    }

    private void StartThread()
    {
        lock (_gate) StartThreadLocked();
    }

    private void StartThreadLocked()
    {
        int gen = _generation;
        var t = new Thread(() => Loop(gen)) { IsBackground = true, Name = "FxAuto browser tab reader" };
        if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }

    private void Loop(int gen)
    {
        while (true)
        {
            PendingRead req;
            lock (_gate)
            {
                while (!_disposed && gen == _generation && _pending is null) Monitor.Wait(_gate);
                if (_disposed || gen != _generation) return;
                req = _pending!;
                _pending = null;
                _busy = true;
            }

            IReadOnlyList<string> titles;
            IReadOnlyList<BrowserTab>? tabs;
#pragma warning disable CA1031 // any reader failure means "tabs unavailable"
            try { (tabs, titles) = _read(req.RootPid); }
            catch (Exception) { tabs = null; titles = SafeTitles(req.RootPid); }
#pragma warning restore CA1031

            bool deliver;
            lock (_gate)
            {
                if (gen != _generation) return;           // replaced by Reenable while stuck
                _busy = false;
                deliver = _outstanding.Remove(req);       // false = abandoned (late) or disposed
                if (deliver) _consecutiveTimeouts = 0;
                ArmWatchdog();
            }
            if (deliver) _post(req.Id, req.RootPid, tabs, titles);
        }
    }

    private void OnWatchdog()
    {
        var expired = new List<PendingRead>();
        bool disabledNow = false;
        lock (_gate)
        {
            if (_disposed) return;
            long now = _nowMs();
            foreach (var r in _outstanding)
                if (r.DeadlineMs <= now) expired.Add(r);
            foreach (var r in expired)
            {
                _outstanding.Remove(r);
                if (ReferenceEquals(_pending, r)) _pending = null;
                _consecutiveTimeouts++;
            }
            if (!_disabled && _consecutiveTimeouts >= _maxTimeouts)
            {
                _disabled = true;
                disabledNow = true;
                if (_pending is not null) { expired.Add(_pending); _outstanding.Remove(_pending); _pending = null; }
            }
            ArmWatchdog();
        }
        if (disabledNow) Disabled?.Invoke();      // before the results: the receiver sees "disabled" first
        foreach (var r in expired) _post(r.Id, r.RootPid, null, SafeTitles(r.RootPid));
    }

    private void ArmWatchdog()
    {
        if (_disposed) return;
        if (_outstanding.Count == 0) { _watchdog.Change(Timeout.Infinite, Timeout.Infinite); return; }
        long first = long.MaxValue;
        foreach (var r in _outstanding) first = Math.Min(first, r.DeadlineMs);
        long due = Math.Max(0, first - _nowMs());
        _watchdog.Change(due, Timeout.Infinite);
    }

    private static Func<int, (IReadOnlyList<BrowserTab>?, IReadOnlyList<string>)> SeparateReads(
        Func<int, IReadOnlyList<BrowserTab>?> readTabs, Func<int, IReadOnlyList<string>> windowTitles)
    {
        ArgumentNullException.ThrowIfNull(readTabs);
        ArgumentNullException.ThrowIfNull(windowTitles);
        return pid =>
        {
            IReadOnlyList<string> titles;
#pragma warning disable CA1031 // titles are best-effort; any reader failure means "tabs unavailable"
            try { titles = windowTitles(pid); }
            catch (Exception) { titles = []; }
            IReadOnlyList<BrowserTab>? tabs;
            try { tabs = readTabs(pid); }
            catch (Exception) { tabs = null; }
#pragma warning restore CA1031
            return (tabs, titles);
        };
    }

    private IReadOnlyList<string> SafeTitles(int rootPid)
    {
#pragma warning disable CA1031 // titles are best-effort
        try { return _windowTitles(rootPid); }
        catch (Exception) { return []; }
#pragma warning restore CA1031
    }
}
