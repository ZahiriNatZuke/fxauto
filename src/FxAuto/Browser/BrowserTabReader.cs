using System.Diagnostics;
using System.Runtime.InteropServices;
using FxAuto.App;
using FxAuto.Core.Classification;

namespace FxAuto.Browser;

/// <summary>
/// Read-only browser tab reader (REQ-103, spike outcomes 4c). UI Automation through COM (<c>CUIAutomation8</c> /
/// <c>IUIAutomation2</c>, declarations in UiaInterop.cs, no NuGet) with ConnectionTimeout and TransactionTimeout of
/// 300 ms. For each visible top-level browser window of the process tree it walks the tab strip with the
/// ControlView tree walker and a cache request (ControlType, Name, SelectionItem.IsSelected: one cross-process call
/// per element), breadth first, <b>never entering <c>Document</c> subtrees</b> (web content), depth cap 12. It only
/// reads properties: no Invoke, no focus, no Select, no pattern that changes state. Every element of a read is
/// released in <c>finally</c>; the automation object, walker and cache request live as long as the reader. Chrome and Edge (class <c>Chrome_WidgetWin_1</c>); Firefox best effort
/// (<c>MozillaWindowClass</c>; its tab names carry no audible marker, so its tabs are never Audible).
/// Used only from the <see cref="TabReadWorker"/> MTA thread (window titles may be read from any thread).
/// </summary>
internal sealed class UiaBrowserTabReader : IBrowserTabReader, IDisposable
{
    public const int MaxDepth = 12;
    public const uint UiaTimeoutMs = 300;
    private const int MaxElementsPerWindow = 3000;
    /// <summary>Soft cap per read: past it the read gives up (null). The hard 1 s cap is the worker's.</summary>
    private static readonly TimeSpan SoftBudget = TimeSpan.FromMilliseconds(700);

    private static readonly string[] BrowserWindowClasses = ["Chrome_WidgetWin_1", "MozillaWindowClass"];

    private volatile IReadOnlyList<string> _markers;
    private readonly object _uiaGate = new();
    // Created lazily on the reader (MTA) thread and kept for the reader lifetime (re-creating the walker and the
    // cache request per read only adds native churn); released in Dispose.
    private IUIAutomation2? _uia;
    private IUIAutomationTreeWalker? _walker;
    private IUIAutomationCacheRequest? _cache;
    private bool _uiaFailed;
    private bool _disposed;

    public UiaBrowserTabReader(IReadOnlyList<string> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        _markers = markers;
    }

    /// <summary>Audible markers (config <c>general.audibleTabMarkers</c>); can be replaced on config change.</summary>
    public IReadOnlyList<string> Markers
    {
        get => _markers;
        set => _markers = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Elements visited by the last <see cref="ReadTabs"/> (diagnostics).</summary>
    public int LastVisited { get; private set; }

    public IReadOnlyList<BrowserTab>? ReadTabs(int rootPid) => ReadTabs(BrowserWindows.Find(rootPid));

    /// <summary>Tabs and window titles from a single window search (CPU: one EnumWindows per read).</summary>
    public (IReadOnlyList<BrowserTab>? Tabs, IReadOnlyList<string> Titles) ReadTabsAndTitles(int rootPid)
    {
        var windows = BrowserWindows.Find(rootPid);
        return (ReadTabs(windows), Titles(windows));
    }

    private IReadOnlyList<BrowserTab>? ReadTabs(List<BrowserWindows.Window> windows)
    {
        LastVisited = 0;
        if (windows.Count == 0) return null;
        var parts = Automation();
        if (parts is null) return null;
        var (uia, walker, cache) = parts.Value;

        var markers = _markers;
        var tabs = new List<BrowserTab>();
        var sw = Stopwatch.StartNew();
        var queue = new Queue<(IUIAutomationElement El, int Depth)>();
        bool anyWindowRead = false;
        try
        {
            foreach (var w in windows)
            {
                if (uia.ElementFromHandle(w.Hwnd, out var root) < 0 || root is null) continue;
                anyWindowRead = true;
                queue.Enqueue((root, 0));
                int visited = 0;
                while (queue.Count > 0)
                {
                    if (sw.Elapsed > SoftBudget) return null;
                    var (el, depth) = queue.Dequeue();
                    try
                    {
                        if (visited > MaxElementsPerWindow) continue;      // drain (and release) the queue
                        if (walker.GetFirstChildElementBuildCache(el, cache, out var child) < 0) continue;
                        while (child is not null)
                        {
                            visited++;
                            IUIAutomationElement? next = null;
                            bool queued = false;
                            try
                            {
                                int? ct = CachedInt(child, UiaIds.ControlTypePropertyId);
                                if (ct == UiaIds.TabItemControlTypeId)
                                {
                                    if (CachedString(child, UiaIds.NamePropertyId) is { Length: > 0 } name)
                                    {
                                        bool selected = CachedBool(child, UiaIds.SelectionItemIsSelectedPropertyId);
                                        tabs.Add(BrowserTabNames.ToTab(name, selected, markers));
                                    }
                                }
                                else if (ct != UiaIds.DocumentControlTypeId && depth + 1 < MaxDepth)
                                {
                                    queue.Enqueue((child, depth + 1));
                                    queued = true;
                                }
                                if (visited <= MaxElementsPerWindow
                                    && walker.GetNextSiblingElementBuildCache(child, cache, out next) < 0)
                                    next = null;
                            }
                            finally
                            {
                                if (!queued) Com.Release(child);
                            }
                            child = next;
                        }
                    }
                    finally { Com.Release(el); }
                }
                LastVisited += visited;
            }
        }
        catch (COMException) { return null; }
        catch (InvalidCastException) { return null; }
        finally
        {
            while (queue.Count > 0) Com.Release(queue.Dequeue().El);
        }
        if (!anyWindowRead || tabs.Count == 0) return null;
        return tabs;
    }

    public IReadOnlyList<string> WindowTitles(int rootPid) => Titles(BrowserWindows.Find(rootPid));

    private static List<string> Titles(List<BrowserWindows.Window> windows)
    {
        var titles = new List<string>(windows.Count);
        foreach (var w in windows)
            if (w.Title.Length > 0) titles.Add(w.Title);
        return titles;
    }

    public void Dispose()
    {
        lock (_uiaGate)
        {
            _disposed = true;
            ReleaseAutomation();
        }
    }

    private (IUIAutomation2 Uia, IUIAutomationTreeWalker Walker, IUIAutomationCacheRequest Cache)? Automation()
    {
        lock (_uiaGate)
        {
            if (_disposed || _uiaFailed) return null;
            if (_uia is not null && _walker is not null && _cache is not null) return (_uia, _walker, _cache);
            try
            {
                var type = Type.GetTypeFromCLSID(UiaIds.ClsidCUIAutomation8, throwOnError: true)!;
                var o = Activator.CreateInstance(type);
                if (o is not IUIAutomation2 uia) { Com.Release(o); return Fail(); }
                _uia = uia;
                uia.put_ConnectionTimeout(UiaTimeoutMs);
                uia.put_TransactionTimeout(UiaTimeoutMs);
                if (uia.get_ControlViewWalker(out _walker) < 0 || _walker is null) return Fail();
                if (uia.CreateCacheRequest(out _cache) < 0 || _cache is null) return Fail();
                if (_cache.AddProperty(UiaIds.ControlTypePropertyId) < 0
                    || _cache.AddProperty(UiaIds.NamePropertyId) < 0
                    || _cache.AddProperty(UiaIds.SelectionItemIsSelectedPropertyId) < 0) return Fail();
                return (_uia, _walker, _cache);
            }
            catch (COMException) { return Fail(); }
            catch (TypeLoadException) { return Fail(); }
        }
    }

    private (IUIAutomation2, IUIAutomationTreeWalker, IUIAutomationCacheRequest)? Fail()
    {
        _uiaFailed = true;
        ReleaseAutomation();
        return null;
    }

    private void ReleaseAutomation()
    {
        if (_cache is not null) Marshal.FinalReleaseComObject(_cache);
        if (_walker is not null) Marshal.FinalReleaseComObject(_walker);
        if (_uia is not null) Marshal.FinalReleaseComObject(_uia);
        _cache = null;
        _walker = null;
        _uia = null;
    }

    private static object? Cached(IUIAutomationElement el, int propertyId)
    {
        if (el.GetCachedPropertyValue(propertyId, out var v) < 0) return null;
        if (v is not null && Marshal.IsComObject(v)) { Com.Release(v); return null; }   // ReservedNotSupportedValue
        return v;
    }

    private static int? CachedInt(IUIAutomationElement el, int id) => Cached(el, id) is int i ? i : null;
    private static string? CachedString(IUIAutomationElement el, int id) => Cached(el, id) as string;
    private static bool CachedBool(IUIAutomationElement el, int id) => Cached(el, id) is true;

    internal static class BrowserWindows
    {
        internal readonly record struct Window(nint Hwnd, int Pid, string ClassName, string Title);

        /// <summary>Maximum age of the cached process snapshot, which is only needed for browser-class windows that do not
        /// belong to the root PID (CPU: a Toolhelp snapshot costs ~3.5 ms here; PIDs can be reused, so it expires).</summary>
        private static readonly TimeSpan SnapshotMinAge = TimeSpan.FromSeconds(30);
        private static readonly object Gate = new();
        private static Dictionary<int, (int ParentPid, string Exe)> _parents = [];
        private static long _parentsAt;
        private static bool _hasParents;

        public static List<Window> Find(int rootPid)
        {
            var found = new List<Window>();
            Native.EnumWindows((hwnd, _) =>
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                var cls = Native.GetClass(hwnd);
                if (Array.IndexOf(BrowserWindowClasses, cls) < 0) return true;
                var title = Native.GetTitle(hwnd);
                // Chrome keeps visible helper windows of the same class without a title: skip them.
                if (title.Length == 0) return true;
                Native.GetWindowThreadProcessId(hwnd, out uint wpid);
                if (!InTree(rootPid, (int)wpid)) return true;
                found.Add(new Window(hwnd, (int)wpid, cls, title));
                return true;
            }, 0);
            return found;
        }

        /// <summary>pid is the root or one of its descendants. The browser windows normally belong to the root itself, so
        /// the cached parent map is only consulted for other processes (e.g. Electron apps share Chrome's window class).</summary>
        private static bool InTree(int rootPid, int pid)
        {
            if (pid == rootPid) return true;
            if (pid <= 4) return false;
            lock (Gate)
            {
                long now = Environment.TickCount64;
                // Expires by age even for known PIDs: a PID can be reused by an unrelated process.
                if (!_hasParents || now - _parentsAt >= (long)SnapshotMinAge.TotalMilliseconds)
                {
                    _parents = Native.SnapshotProcessParents();
                    _parentsAt = now;
                    _hasParents = true;
                }
                return ProcessTreeWalk.IsDescendant(_parents, rootPid, pid);
            }
        }
    }
}

/// <summary>
/// What the coordinator uses (task 5 wires it): <see cref="UiaBrowserTabReader"/> behind a
/// <see cref="TabReadWorker"/> (own MTA thread, one pending request, abandoned after 1 s, disabled after 3
/// consecutive timeouts). Results arrive as <see cref="BrowserTabsEvent"/> through <c>post</c>, which must only
/// enqueue (it is called from the reader thread, the watchdog timer thread or the caller thread).
/// The coordinator decides when to ask (<c>BrowserTabTracker.NeedsTabRead</c>) and skips asking when
/// <c>general.readBrowserTabs</c> is false.
/// </summary>
internal sealed class BrowserTabService : IDisposable
{
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromSeconds(1);
    public const int MaxConsecutiveTimeouts = 3;

    private readonly UiaBrowserTabReader _reader;
    private readonly TabReadWorker _worker;

    public BrowserTabService(IReadOnlyList<string> audibleMarkers, Action<BrowserTabsEvent> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        _reader = new UiaBrowserTabReader(audibleMarkers);
        _worker = new TabReadWorker(_reader.ReadTabsAndTitles, _reader.WindowTitles,
            (id, pid, tabs, titles) => post(new BrowserTabsEvent(DateTimeOffset.Now, id, pid, tabs, titles)),
            AbandonAfter, MaxConsecutiveTimeouts);
        _worker.Disabled += () => Disabled?.Invoke();
    }

    /// <summary>Raised once when tab reading gets disabled after consecutive timeouts (must only enqueue).</summary>
    public event Action? Disabled;

    public bool IsDisabled => _worker.IsDisabled;

    /// <summary>The reader itself (for direct synchronous use, e.g. a diagnostics probe).</summary>
    public IBrowserTabReader Reader => _reader;

    public IReadOnlyList<string> AudibleMarkers
    {
        get => _reader.Markers;
        set => _reader.Markers = value;
    }

    /// <summary>Requests a read; the result carries the returned id.</summary>
    public long Request(int rootPid) => _worker.Request(rootPid);

    /// <summary>Settings > General "re-enable tab reading".</summary>
    public void Reenable() => _worker.Reenable();

    public void Dispose()
    {
        _worker.Dispose();
        _reader.Dispose();      // a read still running on a stuck thread gets null (COM objects already released)
    }
}
