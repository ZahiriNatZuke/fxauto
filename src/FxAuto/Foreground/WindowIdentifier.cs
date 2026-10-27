using System.Runtime.InteropServices;
using FxAuto.Core;

namespace FxAuto.Foreground;

/// <summary>
/// Turns an hwnd into <see cref="ForegroundInfo"/>: exe, path, title, AUMID, PID and start time.
/// Only opens processes with PROCESS_QUERY_LIMITED_INFORMATION. If it cannot, it takes the exe name from a
/// Toolhelp snapshot (without opening the process) and marks the window as unreadable.
/// </summary>
internal static class WindowIdentifier
{
    public static ForegroundInfo Identify(nint hwnd)
    {
        string title = Native.GetTitle(hwnd);
        Native.GetWindowThreadProcessId(hwnd, out uint upid);
        int pid = (int)upid;

        var p = ReadProcess(pid);

        // Store apps: the frame window belongs to ApplicationFrameHost; the real app is the CoreWindow child.
        if (p.Exe is not null && p.Exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            int realPid = FindCoreWindowPid(hwnd, pid);
            if (realPid != 0)
            {
                pid = realPid;
                p = ReadProcess(realPid);
            }
        }

        return new ForegroundInfo(hwnd, p.Exe, p.FullPath, title, p.Aumid, pid, p.Start, p.Unreadable);
    }

    internal readonly record struct ProcInfo(string? Exe, string? FullPath, string? Aumid, DateTimeOffset? Start,
                                             bool Unreadable, string? Why);

    /// <summary>Reads exe, path, AUMID and start time. Unreadable if it cannot be opened or is elevated.</summary>
    public static ProcInfo ReadProcess(int pid)
    {
        if (pid <= 0) return new ProcInfo(null, null, null, null, true, "no pid");
        nint h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == 0)
        {
            int err = Marshal.GetLastWin32Error();
            string? exe = ExeFromSnapshot(pid);
            return new ProcInfo(exe, null, null, null, true, $"OpenProcess error {err}");
        }
        try
        {
            string? path = Native.GetImagePath(h);
            string? exe = path is not null ? Path.GetFileName(path) : ExeFromSnapshot(pid);
            string? aumid = Native.GetAumid(h);
            var start = Native.GetStartTime(h);
            // Elevated process (or inaccessible token): the spec says not to change the preset for that window.
            bool elevated = Native.IsTokenElevated(h) == true;
            bool unreadable = path is null || elevated;
            return new ProcInfo(exe, path, aumid, start, unreadable, elevated ? "elevated" : path is null ? "no path" : null);
        }
        finally { Native.CloseHandle(h); }
    }

    private static string? ExeFromSnapshot(int pid)
        => Native.SnapshotProcesses().TryGetValue(pid, out var name) ? name : null;

    private static int FindCoreWindowPid(nint frame, int framePid)
    {
        int found = 0;
        Native.EnumChildWindows(frame, (child, _) =>
        {
            if (Native.GetClass(child) == "Windows.UI.Core.CoreWindow")
            {
                Native.GetWindowThreadProcessId(child, out uint cpid);
                if (cpid != 0 && cpid != framePid)
                {
                    found = (int)cpid;
                    return false;
                }
            }
            return true;
        }, 0);
        return found;
    }

    /// <summary>Every running process, so the engine can recover sticky games that are already open.</summary>
    public static List<ForegroundInfo> RunningProcesses()
    {
        var list = new List<ForegroundInfo>();
        foreach (var (pid, name) in Native.SnapshotProcesses())
        {
            if (pid <= 4) continue;
            var p = ReadProcess(pid);
            list.Add(new ForegroundInfo(0, p.Exe ?? name, p.FullPath, null, p.Aumid, pid, p.Start, p.Unreadable));
        }
        return list;
    }
}
