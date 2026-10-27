using System.Runtime.InteropServices;

namespace FxAuto;

// Win32 declarations (only what FxAuto uses). No write or memory-read rights over other processes:
// PROCESS_QUERY_LIMITED_INFORMATION and SYNCHRONIZE, nothing else.
internal static class Native
{
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;
    public const int CHILDID_SELF = 0;

    public const uint WM_TIMER = 0x0113;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_APP = 0x8000;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevation = 20;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    public delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hwnd, int idObject,
                                          int idChild, uint dwEventThread, uint dwmsEventTime);
    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    // ---- user32 ----
    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
                                              WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread,
                                              uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    // InternalGetWindowText does not send WM_GETTEXT: it does not hang on windows that do not respond.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int InternalGetWindowText(nint hWnd, [Out] char[] pString, int cchMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool KillTimer(nint hWnd, nuint uIDEvent);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint hIcon);

    // ---- kernel32 ----
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
                                          uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, [Out] char[] lpExeName,
                                                        ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(nint hProcess, out long lpCreationTime, out long lpExitTime,
                                              out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetApplicationUserModelId(nint hProcess, ref uint applicationUserModelIdLength,
                                                       [Out] char[]? applicationUserModelId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32First(nint hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32Next(nint hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ---- advapi32 ----
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, out int tokenInformation,
                                                  int tokenInformationLength, out int returnLength);

    // ---- helpers ----
    public static string GetClass(nint hwnd)
    {
        var buf = new char[256];
        int n = GetClassName(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    public static string GetTitle(nint hwnd)
    {
        var buf = new char[512];
        int n = InternalGetWindowText(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    /// <summary>Full path of the exe (null if it cannot be read).</summary>
    public static string? GetImagePath(nint hProcess)
    {
        var buf = new char[1024];
        uint size = (uint)buf.Length;
        return QueryFullProcessImageName(hProcess, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
    }

    public static DateTimeOffset? GetStartTime(nint hProcess)
        => GetProcessTimes(hProcess, out long created, out _, out _, out _) && created > 0
            ? DateTimeOffset.FromFileTime(created)
            : null;

    /// <summary>AUMID of a packaged app (null if it is not one).</summary>
    public static string? GetAumid(nint hProcess)
    {
        uint len = 0;
        int rc = GetApplicationUserModelId(hProcess, ref len, null);
        if (rc != ERROR_INSUFFICIENT_BUFFER || len == 0 || len > 4096) return null;
        var buf = new char[len];
        rc = GetApplicationUserModelId(hProcess, ref len, buf);
        if (rc != 0) return null;
        return new string(buf, 0, (int)Math.Max(0, len - 1));
    }

    /// <summary>
    /// true = elevated; false = not elevated; null = could not be opened (access denied to the token is taken as
    /// "probably elevated" and returns true).
    /// </summary>
    public static bool? IsTokenElevated(nint hProcess)
    {
        if (!OpenProcessToken(hProcess, TOKEN_QUERY, out nint token))
            return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? true : null;
        try
        {
            return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _)
                ? elevated != 0
                : null;
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Process snapshot (pid → exe name) without opening any process.</summary>
    public static Dictionary<int, string> SnapshotProcesses()
    {
        var result = new Dictionary<int, string>();
        nint snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == -1 || snap == 0) return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32First(snap, ref e)) return result;
            do { result[(int)e.th32ProcessID] = e.szExeFile; }
            while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return result;
    }

    /// <summary>Top-level windows of a process whose class starts with the given prefix.</summary>
    public static bool ProcessHasWindowClass(int pid, string classPrefix)
    {
        bool found = false;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint wpid);
            if (wpid == pid && GetClass(hwnd).StartsWith(classPrefix, StringComparison.Ordinal))
            {
                found = true;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    // ---- task 4a (audio session monitor): appended ----

    /// <summary>Process snapshot (pid → parent pid + exe name) without opening any process.</summary>
    public static Dictionary<int, (int ParentPid, string Exe)> SnapshotProcessParents()
    {
        var result = new Dictionary<int, (int ParentPid, string Exe)>();
        nint snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == -1 || snap == 0) return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32First(snap, ref e)) return result;
            do { result[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, e.szExeFile); }
            while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return result;
    }

    /// <summary>Creation time (FILETIME) of a process, or null if it cannot be opened.</summary>
    public static long? GetCreationFileTime(int pid)
    {
        if (pid <= 4) return null;
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == 0) return null;
        try { return GetProcessTimes(h, out long created, out _, out _, out _) && created > 0 ? created : null; }
        finally { CloseHandle(h); }
    }

    /// <summary>Full image path and AUMID of a process (nulls if it cannot be opened).</summary>
    public static (string? Path, string? Aumid) GetPathAndAumid(int pid)
    {
        if (pid <= 4) return (null, null);
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == 0) return (null, null);
        try { return (GetImagePath(h), GetAumid(h)); }
        finally { CloseHandle(h); }
    }

    // ---- task 4c (browser tab reader): appended ----

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);
}
