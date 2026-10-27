using FxAuto.Core;

namespace FxAuto.Foreground;

/// <summary>
/// Foreground detector. Own thread with a message loop:
/// - EVENT_SYSTEM_FOREGROUND and EVENT_SYSTEM_MINIMIZEEND (WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS).
/// - EVENT_OBJECT_NAMECHANGE only for the process/thread of the foreground window, re-registered on every focus
///   change and filtered to that window's OBJID_WINDOW (browser tabs).
/// - Fallback poll every 1.5 s (hwnd + title) in case an event is lost.
/// The delegates live in static fields so the GC does not collect them while the hook is alive.
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    public enum Kind { Foreground, TitleChanged }

    private static ForegroundWatcher? _current;
    private static readonly Native.WinEventDelegate ForegroundProc = OnForegroundEvent;
    private static readonly Native.WinEventDelegate NameProc = OnNameEvent;

    private const nuint PollTimerId = 1;
    private const uint PollMs = 1500;

    private readonly Action<Kind, ForegroundInfo, DateTimeOffset> _sink;
    private readonly Action<string, object> _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private uint _threadId;

    private nint _hookFg, _hookMin, _hookName;
    private nint _lastHwnd;
    private string _lastTitle = "";
    private ForegroundInfo? _lastInfo;
    private uint _lastOwnerPid;             // owner of _lastHwnd when _lastInfo was read (Store apps: the frame host)
    private uint _nameHookPid, _nameHookTid;

    public ForegroundWatcher(Action<Kind, ForegroundInfo, DateTimeOffset> sink, Action<string, object> log)
    {
        _sink = sink;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "FxAuto.Hook" };
    }

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _current, this, null) is not null)
            throw new InvalidOperationException("There can be only one ForegroundWatcher.");
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        const uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
        _hookFg = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0,
                                         ForegroundProc, 0, 0, flags);
        _hookMin = Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZEEND, Native.EVENT_SYSTEM_MINIMIZEEND, 0,
                                          ForegroundProc, 0, 0, flags);
        if (_hookFg == 0 || _hookMin == 0) _log("hook.error", new { foreground = _hookFg != 0, minimize = _hookMin != 0 });
        Native.SetTimer(0, PollTimerId, PollMs, 0);
        _started.Set();

        // First read: the window that is already in front at startup.
        Check(Native.GetForegroundWindow(), DateTimeOffset.Now, force: true);

        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == Native.WM_TIMER && msg.hwnd == 0)
            {
                Poll();
                continue;
            }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        Native.KillTimer(0, PollTimerId);
        if (_hookName != 0) Native.UnhookWinEvent(_hookName);
        if (_hookMin != 0) Native.UnhookWinEvent(_hookMin);
        if (_hookFg != 0) Native.UnhookWinEvent(_hookFg);
        _hookFg = _hookMin = _hookName = 0;
    }

    private static void OnForegroundEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var self = _current;
        if (self is null) return;
        try
        {
            // MINIMIZEEND carries the restored window; what counts is the one really in front.
            nint fg = Native.GetForegroundWindow();
            self.Check(fg != 0 ? fg : hwnd, DateTimeOffset.Now, force: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            self._log("hook.exception", new { error = ex.Message });
        }
    }

    private static void OnNameEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var self = _current;
        if (self is null || idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF) return;
        if (hwnd != self._lastHwnd) return;
        try
        {
            self.CheckTitle(DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            self._log("hook.exception", new { error = ex.Message });
        }
    }

    private void Poll()
    {
        nint fg = Native.GetForegroundWindow();
        if (fg == 0) return;
        if (fg != _lastHwnd) Check(fg, DateTimeOffset.Now, force: false, fromPoll: true);
        else CheckTitle(DateTimeOffset.Now);
    }

    private void Check(nint hwnd, DateTimeOffset arrivedAt, bool force, bool fromPoll = false)
    {
        if (hwnd == 0) return;   // no foreground window (transition): ignored
        Native.GetWindowThreadProcessId(hwnd, out uint ownerPid);
        if (ownerPid == Environment.ProcessId) return;   // tray menu or another own window
        string title = Native.GetTitle(hwnd);
        if (!force && hwnd == _lastHwnd && title == _lastTitle) return;
        bool sameWindow = hwnd == _lastHwnd;
        _lastHwnd = hwnd;
        _lastTitle = title;
        var info = WindowIdentifier.Identify(hwnd);
        _lastInfo = info;
        _lastOwnerPid = ownerPid;
        if (!sameWindow) RehookNameChange(hwnd);
        if (fromPoll) _log("hook.poll", new { hwnd = (long)hwnd });
        _sink(sameWindow ? Kind.TitleChanged : Kind.Foreground, info, arrivedAt);
    }

    private void CheckTitle(DateTimeOffset arrivedAt)
    {
        nint hwnd = _lastHwnd;
        if (hwnd == 0) return;
        string title = Native.GetTitle(hwnd);
        if (title == _lastTitle) return;
        _lastTitle = title;
        // Same window, same process: only the title changed, so the process is not identified again (CPU: terminals
        // animate their title about once a second). Never for Store apps (the window belongs to ApplicationFrameHost
        // and the real app is resolved from its CoreWindow, which can change) nor for unreadable/elevated windows.
        Native.GetWindowThreadProcessId(hwnd, out uint ownerPid);
        var last = _lastInfo;
        var info = CanReuse(last, hwnd, ownerPid, _lastOwnerPid)
            ? last! with { Title = title }
            : WindowIdentifier.Identify(hwnd);
        _lastInfo = info;
        _lastOwnerPid = ownerPid;
        _sink(Kind.TitleChanged, info, arrivedAt);
    }

    /// <summary>A title change may reuse the last identification only for the same window of the same owner process,
    /// when the identified process is that owner (not a Store app behind ApplicationFrameHost) and it was readable.</summary>
    internal static bool CanReuse(ForegroundInfo? last, nint hwnd, uint ownerPid, uint lastOwnerPid)
        => last is not null && last.Hwnd == hwnd && ownerPid != 0 && ownerPid == lastOwnerPid
           && last.Pid == (int)ownerPid && !last.Unreadable
           && !string.Equals(last.Exe, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

    // NAMECHANGE limited to the process and thread of the foreground window.
    private void RehookNameChange(nint hwnd)
    {
        uint tid = Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _nameHookPid && tid == _nameHookTid && _hookName != 0) return;
        if (_hookName != 0) Native.UnhookWinEvent(_hookName);
        _hookName = 0;
        _nameHookPid = pid;
        _nameHookTid = tid;
        if (pid == 0 || pid == Environment.ProcessId) return;
        _hookName = Native.SetWinEventHook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE, 0, NameProc,
                                           pid, tid, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, 0, 0);
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        Interlocked.CompareExchange(ref _current, null, this);
        _started.Dispose();
    }
}
