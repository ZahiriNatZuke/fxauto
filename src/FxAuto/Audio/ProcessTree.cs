namespace FxAuto.Audio;

/// <summary>Resolved app root of a session process.</summary>
/// <param name="Complete">False when the pid was not in the process snapshot yet (snapshot refresh rate-limited);
/// the caller retries later. Incomplete results are not cached.</param>
internal sealed record ProcessRoot(int Pid, string? Exe, int RootPid, string? RootExe, string? RootPath,
    string? RootAumid, bool Complete);

/// <summary>
/// Child PID → app root (highest ancestor with the same exe; past msedgewebview2.exe to the host), from a cached
/// Toolhelp snapshot refreshed at most every 3 s and only when an unknown PID appears. PID reuse guarded with the
/// process creation time. Not thread-safe: used only by the audio monitor thread.
/// </summary>
internal sealed class ProcessTree
{
    private static readonly TimeSpan MinRefresh = TimeSpan.FromSeconds(3);
    private const int MaxCache = 512;

    private readonly Func<IReadOnlyDictionary<int, ProcEntry>> _snapshot;
    private readonly Func<int, long?> _startTime;
    private readonly Func<int, (string? Path, string? Aumid)> _pathAndAumid;
    private readonly Func<long> _nowMs;
    private readonly Func<long> _nowFileTime;
    private long _snapshotFileTime = long.MinValue;
    private IReadOnlyDictionary<int, ProcEntry> _map = new Dictionary<int, ProcEntry>();
    private long _snapshotAtMs;
    private bool _hasSnapshot;
    private readonly Dictionary<int, (long? Start, ProcessRoot Root)> _cache = [];

    public ProcessTree() : this(TakeSnapshot, Native.GetCreationFileTime, Native.GetPathAndAumid,
        () => Environment.TickCount64, () => DateTime.UtcNow.ToFileTimeUtc()) { }

    /// <summary>Injectable sources (tests / probes). <paramref name="nowFileTime"/> must use the same unit as
    /// <paramref name="startTime"/> (UTC FILETIME in production).</summary>
    public ProcessTree(Func<IReadOnlyDictionary<int, ProcEntry>> snapshot, Func<int, long?> startTime,
        Func<int, (string? Path, string? Aumid)> pathAndAumid, Func<long> nowMs, Func<long> nowFileTime)
    {
        _nowFileTime = nowFileTime;
        _snapshot = snapshot;
        _startTime = startTime;
        _pathAndAumid = pathAndAumid;
        _nowMs = nowMs;
    }

    /// <summary>Number of Toolhelp snapshots taken (diagnostics).</summary>
    public int SnapshotCount { get; private set; }

    public (int RootPid, string? RootExe) Resolve(int pid)
    {
        var r = ResolveInfo(pid);
        return (r.RootPid, r.RootExe);
    }

    public ProcessRoot ResolveInfo(int pid)
    {
        if (pid <= 4) return new ProcessRoot(pid, null, pid, null, null, null, true);
        long? start = _startTime(pid);
        if (_cache.TryGetValue(pid, out var hit) && hit.Start == start) return hit.Root;

        // A process created after the snapshot is unknown even if its (reused) pid is in the map.
        bool known = _map.ContainsKey(pid) && !(start is long st && st > _snapshotFileTime);
        if (!known && (!_hasSnapshot || _nowMs() - _snapshotAtMs >= (long)MinRefresh.TotalMilliseconds))
        {
            _snapshotFileTime = _nowFileTime();
            _map = _snapshot();
            _snapshotAtMs = _nowMs();
            _hasSnapshot = true;
            SnapshotCount++;
            _cache.Clear();                          // ancestors may have changed; cheap to rebuild
            known = _map.ContainsKey(pid) && !(start is long st2 && st2 > _snapshotFileTime);
        }
        if (!known)
        {
            var (path, aumid) = _pathAndAumid(pid);
            string? exe = path is null ? null : Path.GetFileName(path);
            return new ProcessRoot(pid, exe, pid, exe, path, aumid, false);
        }

        var (rootPid, rootExe) = ProcessTreeLogic.Climb(pid, _map, _startTime);
        var (rootPath, rootAumid) = _pathAndAumid(rootPid);
        var root = new ProcessRoot(pid, _map[pid].Exe, rootPid, rootExe, rootPath, rootAumid, true);
        if (_cache.Count >= MaxCache) _cache.Clear();
        _cache[pid] = (start, root);
        return root;
    }

    private static Dictionary<int, ProcEntry> TakeSnapshot()
    {
        var raw = Native.SnapshotProcessParents();
        var map = new Dictionary<int, ProcEntry>(raw.Count);
        foreach (var (pid, (parent, exe)) in raw) map[pid] = new ProcEntry(pid, parent, exe);
        return map;
    }
}
