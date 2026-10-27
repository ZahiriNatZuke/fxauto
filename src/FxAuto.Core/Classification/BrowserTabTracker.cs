namespace FxAuto.Core.Classification;

/// <summary>REQ-103 background tabs, per browser root PID: UIA audible tab &gt; classification latched when the browser
/// started playing &gt; browser default. The latch is released when the browser stops playing (the detector reports
/// that after <c>ReleaseMs</c>). Titles are kept in memory only; <see cref="SourceClass.How"/> never contains one.
/// Not thread-safe: coordinator thread only.</summary>
public sealed class BrowserTabTracker
{
    private readonly ISourceClassifier _c;
    private readonly IClock _clock;
    private readonly Dictionary<int, State> _states = [];

    private sealed class State
    {
        public string Exe = "";
        public string? ActiveTitle;
        public IReadOnlyList<BrowserTab>? Tabs;      // null = not read (or the read failed) since the latch
        public DateTimeOffset? TabsReadAt;           // last OnTabs call while playing (also failed reads)
        public DateTimeOffset? StaleAt;              // something that can move the audible tab changed (null = no)
        public DateTimeOffset? RequestedAt;          // last read request sent by the coordinator (this playback)
        public long? PendingRequestId;               // id of that request; results of other requests are dropped
        public int ReadsSincePlaying;                // reads applied since the browser started playing
        public bool Playing;
        public SourceClass? Latched;
        public string? LatchedTitle;
    }

    public BrowserTabTracker(ISourceClassifier c, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(clock);
        _c = c;
        _clock = clock;
    }

    /// <summary>Safety re-read of a playing browser when nothing was seen changing (CPU: UIA reads are event-driven:
    /// start of playback plus a follow-up, focus/title changes, audio sessions of the browser changing and audio
    /// resuming after a silence, which is what switching the playing tab in a background browser looks like).</summary>
    public TimeSpan TabReReadInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Second read after the first one of each playback (the first read can come before the page updated its
    /// tab name or audible marker).</summary>
    public TimeSpan FollowUpReadDelay { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Re-read after a failed read (null tabs: timeout, no tab strip, no window).</summary>
    public TimeSpan FailedReadRetryInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Active tab title from the browser's window titles (most recently focused window).</summary>
    public void OnBrowserTitle(int rootPid, string exe, string? activeTitle)
    {
        var s = Get(rootPid, exe);
        s.ActiveTitle = string.IsNullOrWhiteSpace(activeTitle) ? null : activeTitle;
    }

    /// <summary>Tabs read by UIA (null = the read failed). Each tab title must already be cleaned
    /// (<see cref="TitleSegments.CleanTabName"/>) and Audible computed with the configured markers.</summary>
    public void OnTabs(int rootPid, IReadOnlyList<BrowserTab>? tabs) => Apply(Get(rootPid, null), tabs);

    /// <summary>Result of the request <paramref name="requestId"/>. Dropped (false) unless it answers the last request
    /// sent during the current playback: a result of a request sent before the browser started (or stopped and
    /// started again) playing says nothing about the playing tab.</summary>
    public bool OnTabs(int rootPid, IReadOnlyList<BrowserTab>? tabs, long requestId)
    {
        if (!_states.TryGetValue(rootPid, out var s) || !s.Playing || s.PendingRequestId != requestId) return false;
        s.PendingRequestId = null;
        Apply(s, tabs);
        return true;
    }

    private void Apply(State s, IReadOnlyList<BrowserTab>? tabs)
    {
        s.Tabs = tabs;
        s.TabsReadAt = _clock.Now;
        s.ReadsSincePlaying++;
        // A change seen before the request was sent is covered by this read; a later one still needs a read.
        if (s.StaleAt is { } st && (s.RequestedAt is not { } rq || st <= rq)) s.StaleAt = null;
    }

    /// <summary>The coordinator sent a read request for this browser now.</summary>
    public void OnTabsRequested(int rootPid)
    {
        if (_states.TryGetValue(rootPid, out var s)) s.RequestedAt = _clock.Now;
    }

    /// <summary>The coordinator sent the read request <paramref name="requestId"/> for this browser now.</summary>
    public void OnTabsRequested(int rootPid, long requestId)
    {
        if (!_states.TryGetValue(rootPid, out var s)) return;
        s.RequestedAt = _clock.Now;
        s.PendingRequestId = requestId;
    }

    /// <summary>Something that can move the audible tab changed (focus entered or left the browser, its window title
    /// changed, its audio sessions changed, its audio resumed after a silence): a playing browser is read again as
    /// soon as possible. Ignored while it is not playing.</summary>
    public void MarkStale(int rootPid)
    {
        if (_states.TryGetValue(rootPid, out var s) && s.Playing) s.StaleAt = _clock.Now;
    }

    /// <summary>Latches the classification when audio starts; releases it when the browser stops playing.</summary>
    public void OnPlayingChanged(int rootPid, bool playing)
    {
        var s = Get(rootPid, null);
        if (playing == s.Playing) return;
        s.Playing = playing;
        s.RequestedAt = null;       // requests of an earlier playback do not count for this one
        s.PendingRequestId = null;
        s.ReadsSincePlaying = 0;
        if (playing)
        {
            s.LatchedTitle = s.ActiveTitle;
            s.Latched = _c.Classify(Context(rootPid, s, s.ActiveTitle, null));
            s.Tabs = null;          // tabs read before the audio started say nothing about the playing tab
            s.TabsReadAt = null;
            s.StaleAt = null;
        }
        else
        {
            s.Latched = null;
            s.LatchedTitle = null;
            s.Tabs = null;
            s.TabsReadAt = null;
            s.StaleAt = null;
        }
    }

    /// <summary>True when the coordinator should read the UIA tabs of this browser now (see <see cref="NextTabReadAt"/>).</summary>
    public bool NeedsTabRead(int rootPid) => NextTabReadAt(rootPid) is { } due && due <= _clock.Now;

    /// <summary>When the UIA tabs of this browser should be read next (null = never: not playing). A browser is read
    /// when it starts playing, again <see cref="FollowUpReadDelay"/> after that first read, when it was marked stale
    /// (<see cref="MarkStale"/>), after a failed read every <see cref="FailedReadRetryInterval"/>, and otherwise only
    /// every <see cref="TabReReadInterval"/>.</summary>
    public DateTimeOffset? NextTabReadAt(int rootPid)
    {
        if (!_states.TryGetValue(rootPid, out var s) || !s.Playing) return null;
        if (s.TabsReadAt is not { } readAt) return DateTimeOffset.MinValue;      // not read since it started playing
        if (s.StaleAt is { } stale) return stale;
        if (s.ReadsSincePlaying == 1) return readAt + FollowUpReadDelay;
        return readAt + (s.Tabs is null ? FailedReadRetryInterval : TabReReadInterval);
    }

    /// <summary>Forgets a browser process (exited).</summary>
    public void Forget(int rootPid) => _states.Remove(rootPid);

    public SourceClass Current(int rootPid, string exe)
    {
        var s = Get(rootPid, exe);

        // 1. UIA: an audible tab (one, or the best of several).
        var tabTitle = TabSelection.AudibleTitle(s.Tabs, s.ActiveTitle, s.LatchedTitle);
        if (tabTitle is not null)
        {
            var c = _c.Classify(Context(rootPid, s, tabTitle, null));
            return c with { How = "audible tab: " + c.How };
        }

        // 2. Latched when the browser started playing (if it was classified).
        if (s.Playing && s.Latched is not null && s.Latched.Category != Categories.Unknown)
            return s.Latched with { How = "latched: " + s.Latched.How };

        // Not playing: what the active tab says (used for a foreground browser that is not playing yet).
        if (!s.Playing && s.ActiveTitle is not null)
        {
            var c = _c.Classify(Context(rootPid, s, s.ActiveTitle, null));
            if (c.Category != Categories.Unknown) return c;
        }

        // 3. Browser default (title unknown).
        var d = _c.Classify(Context(rootPid, s, null, null));
        return d with { How = d.How == "browser default" ? d.How : "browser default: " + d.How };
    }

    private State Get(int rootPid, string? exe)
    {
        if (!_states.TryGetValue(rootPid, out var s)) _states[rootPid] = s = new State();
        if (!string.IsNullOrEmpty(exe)) s.Exe = exe;
        return s;
    }

    private static SourceContext Context(int rootPid, State s, string? title, IReadOnlyList<BrowserTab>? tabs)
        => new(rootPid, s.Exe, null, null, title, tabs);
}
