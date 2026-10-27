namespace FxAuto.Foreground;

/// <summary>
/// Lifetime of the PIDs the engine cares about (sticky games and preset source processes).
/// With SYNCHRONIZE the process exit is awaited (immediate notification); if denied, it is checked with a
/// cached Toolhelp snapshot refreshed every 3 s, plus the creation time when it can be read.
/// Only the coordinator thread uses it, except the exit notification, which goes through <c>onExited</c>.
/// </summary>
internal sealed class ProcessLifetime : IDisposable
{
    private sealed class Watch(int pid, nint handle, RegisteredWaitHandle? reg, WaitHandle? wait)
    {
        public int Pid { get; } = pid;
        public nint Handle { get; } = handle;
        public RegisteredWaitHandle? Registration { get; } = reg;
        public WaitHandle? WaitHandle { get; } = wait;
    }

    private readonly Action<int> _onExited;
    private readonly Dictionary<int, Watch> _watches = [];
    private readonly HashSet<int> _snapshotOnly = [];
    private Dictionary<int, string> _snapshot = [];
    private DateTimeOffset _snapshotAt = DateTimeOffset.MinValue;
    private readonly Dictionary<int, DateTimeOffset?> _startCache = [];

    public ProcessLifetime(Action<int> onExited) => _onExited = onExited;

    /// <summary>Watches the exit of these PIDs (already watched ones stay as they are; extra ones are released).</summary>
    public void Track(IEnumerable<int> pids)
    {
        var wanted = new HashSet<int>(pids.Where(p => p > 0));
        foreach (var pid in _watches.Keys.Where(p => !wanted.Contains(p)).ToList()) Release(pid);
        _snapshotOnly.RemoveWhere(p => !wanted.Contains(p));
        foreach (var pid in wanted)
        {
            if (_watches.ContainsKey(pid) || _snapshotOnly.Contains(pid)) continue;
            nint h = Native.OpenProcess(Native.SYNCHRONIZE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h == 0) h = Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)pid);
            if (h == 0)
            {
                _snapshotOnly.Add(pid);
                continue;
            }
            var wh = new ProcessWaitHandle(h);
            int captured = pid;
            var reg = ThreadPool.RegisterWaitForSingleObject(wh, (_, _) => _onExited(captured), null,
                                                             Timeout.Infinite, executeOnlyOnce: true);
            _watches[pid] = new Watch(pid, h, reg, wh);
        }
    }

    /// <summary>Is that PID still alive (and the same process, if its start time is known)?</summary>
    public bool IsAlive(int pid, DateTimeOffset? start)
    {
        if (pid <= 0) return false;
        if (_watches.TryGetValue(pid, out var w))
            return Native.WaitForSingleObject(w.Handle, 0) != Native.WAIT_OBJECT_0;

        var now = DateTimeOffset.Now;
        if (now - _snapshotAt > TimeSpan.FromSeconds(3))
        {
            _snapshot = Native.SnapshotProcesses();
            _snapshotAt = now;
            _startCache.Clear();
        }
        if (!_snapshot.ContainsKey(pid)) return false;
        if (start is null) return true;

        // Recycled PID: compare the creation time if it can be read (without it, trust the PID).
        if (!_startCache.TryGetValue(pid, out var actual))
        {
            actual = null;
            nint h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h != 0)
            {
                try { actual = Native.GetStartTime(h); } finally { Native.CloseHandle(h); }
            }
            _startCache[pid] = actual;
        }
        return actual is null || Math.Abs((actual.Value - start.Value).TotalSeconds) < 2;
    }

    private void Release(int pid)
    {
        if (!_watches.Remove(pid, out var w)) return;
        w.Registration?.Unregister(null);
        w.WaitHandle?.Dispose();   // closes the handle
    }

    public void Dispose()
    {
        foreach (var pid in _watches.Keys.ToList()) Release(pid);
    }

    // WaitHandle over an owned process handle (closed when released).
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(nint handle)
            => SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, ownsHandle: true);
    }
}
