namespace FxAuto.Audio;

/// <summary>One row of a Toolhelp process snapshot (pid, parent pid, exe file name).</summary>
internal readonly record struct ProcEntry(int Pid, int ParentPid, string Exe);

/// <summary>
/// Pure child → app-root climbing over a process snapshot (plan "Key algorithms" 2). No Win32 here, so the test
/// project compiles this file directly.
/// </summary>
internal static class ProcessTreeLogic
{
    public const string WebView2Exe = "msedgewebview2.exe";
    public const int MaxDepth = 64;

    /// <summary>
    /// Root of <paramref name="pid"/>: first climbs past <c>msedgewebview2.exe</c> ancestors to the first
    /// non-WebView2 process (the host app), then to the highest contiguous ancestor with the same exe (browser
    /// audio utility → main browser process; Electron children → main process).
    /// </summary>
    /// <param name="startTime">Process creation time (any monotonic unit, e.g. FILETIME) or null when unknown.
    /// Guards PID reuse: a "parent" created after its child is not the real parent.</param>
    /// <returns>(pid, null) when the pid is not in the snapshot.</returns>
    public static (int RootPid, string? RootExe) Climb(int pid, IReadOnlyDictionary<int, ProcEntry> map,
        Func<int, long?> startTime)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(startTime);
        if (!map.TryGetValue(pid, out var cur)) return (pid, null);

        var visited = new HashSet<int> { pid };
        while (IsWebView2(cur.Exe) && TryParent(cur, map, startTime, visited, _ => true, out var p)) cur = p;
        string exe = cur.Exe;
        while (TryParent(cur, map, startTime, visited, e => SameExe(e, exe), out var p)) cur = p;
        return (cur.Pid, cur.Exe);
    }

    public static bool IsWebView2(string? exe) => SameExe(exe, WebView2Exe);

    public static bool SameExe(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool TryParent(ProcEntry child, IReadOnlyDictionary<int, ProcEntry> map, Func<int, long?> startTime,
        HashSet<int> visited, Func<string, bool> accept, out ProcEntry parent)
    {
        parent = default;
        int ppid = child.ParentPid;
        if (ppid <= 4 || ppid == child.Pid || visited.Count >= MaxDepth) return false;   // 0 idle, 4 System
        if (!map.TryGetValue(ppid, out var p) || !accept(p.Exe)) return false;
        if (visited.Contains(ppid)) return false;
        long? cs = startTime(child.Pid), ps = startTime(ppid);
        if (cs is long c && ps is long s && s > c) return false;                         // PID reused
        visited.Add(ppid);
        parent = p;
        return true;
    }
}
