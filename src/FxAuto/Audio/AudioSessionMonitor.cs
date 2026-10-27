using System.Runtime.InteropServices;
using FxAuto.Core.Audio;
using static FxAuto.Audio.CoreAudio;

namespace FxAuto.Audio;

/// <summary>
/// WASAPI session monitor (task 4a). Owns a dedicated MTA thread that holds every COM object: endpoint enumerator,
/// one <c>IAudioSessionManager2</c> per active render endpoint, the sessions and their meters, and the
/// <see cref="ProcessTree"/> cache. COM callbacks (endpoint changes, session created, session state changed,
/// session disconnected) only set a flag and wake the thread; registration and release happen on the thread.
/// <para>Posting: while a non-excluded session on the FxSound virtual endpoint is <c>Active</c>, one snapshot every
/// <c>pollMs</c> (≤ 4 Hz) with peaks of those sessions only; when that ends, one final "all quiet" snapshot; while
/// quiet, a snapshot only when the structure changes (sessions, states, FxSound render endpoint). Idle wake-ups:
/// 1 s state check (FxSound.exe's render session → <see cref="AudioSnapshot.FxSoundRenderEndpointId"/>) and a 10 s
/// safety re-enumeration.</para>
/// <para><see cref="Sampled"/> is raised on the monitor thread: handlers must only enqueue (never block).</para>
/// </summary>
internal sealed class AudioSessionMonitor : IAudioSessionMonitor
{
    private const int IdleCheckMs = 1000;
    private const int SafetyResyncMs = 10_000;

    private readonly ProcessTree _tree;
    private readonly int _pollMs;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string, object>? _log;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private int _endpointsDirty = 1;   // 1 = rebuild endpoints (start, device change, Refresh, disconnect)
    private int _sessionsDirty;        // 1 = re-enumerate sessions (OnSessionCreated)
    private int _statesDirty = 1;      // 1 = re-read session states (OnStateChanged); unregistered sessions are polled

    // Monitor-thread state.
    private IMMDeviceEnumerator? _enumerator;
    private EndpointClient? _endpointClient;
    private readonly Dictionary<string, TrackedEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private string? _fxSoundEndpointId;
    private long _lastFullSyncMs;
    private long _lastPostMs = long.MinValue / 2;
    private bool _activePosted;        // last posted snapshot had active metered sessions → owe a "quiet" one
    private string? _lastKey;
    private readonly SamplingPace _pace;

    public AudioSessionMonitor(ProcessTree tree, int pollMs = 250, Func<DateTimeOffset>? now = null,
        Action<string, object>? log = null)
    {
        _tree = tree;
        _pollMs = Math.Max(250, pollMs);
        _pace = new SamplingPace(_pollMs);
        _now = now ?? (() => DateTimeOffset.Now);
        _log = log;
    }

    public event Action<AudioSnapshot>? Sampled;

    /// <summary>Last initialisation/runtime error (diagnostics); null when healthy.</summary>
    public string? LastError { get; private set; }

    /// <summary>Counters for the probe / Status (read from any thread; approximate).</summary>
    public long WakeCount { get; private set; }
    public long PostCount { get; private set; }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "FxAuto audio monitor" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Refresh()
    {
        Interlocked.Exchange(ref _endpointsDirty, 1);
        Wake();
    }

    public void Dispose()
    {
        _stop = true;
        Wake();
        if (_thread is { } t && t != Thread.CurrentThread && !t.Join(TimeSpan.FromSeconds(3)))
        {
            _log?.Invoke("audio.monitor.stop_timeout", new { });
            return;                                   // leave _wake alive: the thread may still use it
        }
        _wake.Dispose();
    }

    private void Wake()
    {
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    // ───────────────────────── monitor thread ─────────────────────────

    private void Run()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator, throwOnError: true)!;
            _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
            _endpointClient = new EndpointClient(this);
            int hr = _enumerator.RegisterEndpointNotificationCallback(_endpointClient);
            if (hr != 0)
            {
                _endpointClient = null;                // still works: Refresh() and the 10 s re-enumeration
                Fail($"RegisterEndpointNotificationCallback 0x{hr:X8}");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException)
        {
            Fail("enumerator: " + ex.Message);
            return;
        }

        int timeout = 0;
        while (true)
        {
            _wake.WaitOne(timeout);
            if (_stop) break;
            WakeCount++;
            try { timeout = Tick(); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
            {
                // Device invalidated mid-call, etc.: rebuild everything on the next tick.
                Fail("tick: " + ex.Message);
                Interlocked.Exchange(ref _endpointsDirty, 1);
                timeout = IdleCheckMs;
            }
        }
        Cleanup();
    }

    private int Tick()
    {
        long nowMs = Environment.TickCount64;
        // CPU: states come from OnStateChanged; they are re-read on that event, after a re-enumeration and for
        // sessions whose notification could not be registered (instead of every session on every sample).
        bool readStates = Interlocked.Exchange(ref _statesDirty, 0) == 1;
        if (Interlocked.Exchange(ref _endpointsDirty, 0) == 1)
        {
            Interlocked.Exchange(ref _sessionsDirty, 0);
            RebuildEndpoints();
            _lastFullSyncMs = nowMs;
            readStates = true;
        }
        else if (Interlocked.Exchange(ref _sessionsDirty, 0) == 1 || nowMs - _lastFullSyncMs >= SafetyResyncMs)
        {
            foreach (var ep in _endpoints.Values.ToList()) SyncSessions(ep);
            _lastFullSyncMs = nowMs;
            readStates = true;
        }

        // States + retry roots that were not in the process snapshot yet.
        foreach (var ep in _endpoints.Values)
        {
            foreach (var s in ep.Sessions.Values)
            {
                if ((readStates || !s.Registered) && s.Control.GetState(out int st) == 0) s.State = st;
                if (!s.Root.Complete) s.Root = _tree.ResolveInfo(s.Pid);
            }
            foreach (var key in ep.Sessions.Where(kv => kv.Value.State == SessionStates.Expired).Select(kv => kv.Key).ToList())
            {
                ep.Sessions[key].Dispose();
                ep.Sessions.Remove(key);
            }
        }

        // One pass (CPU): peaks are only read for metered sessions, and the structure key ignores peaks.
        var raws = CollectRaw(readPeaks: true);
        bool anyActive = raws.Any(r => AudioSnapshotBuilder.NeedsMetering(r, _fxSoundEndpointId, _ownPid));
        var snap = AudioSnapshotBuilder.Build(_now(), _fxSoundEndpointId, raws, _ownPid);
        string key2 = AudioSnapshotBuilder.StructureKey(snap);
        bool due = anyActive || _activePosted || key2 != _lastKey;
        if (!due) return IdleCheckMs;

        long since = Environment.TickCount64 - _lastPostMs;
        if (since < _pollMs) return (int)(_pollMs - since);     // ≤ 4 Hz, whatever woke us

        _lastPostMs = Environment.TickCount64;
        _activePosted = anyActive;
        _lastKey = key2;
        PostCount++;
        try { Sampled?.Invoke(snap); }
#pragma warning disable CA1031 // a faulty subscriber must not kill the monitor thread
        catch (Exception ex) { _log?.Invoke("audio.monitor.subscriber_error", new { error = ex.Message }); }
#pragma warning restore CA1031
        // CPU: pollMs while the set of audible roots or the structure changed recently, a slower pace once stable.
        return anyActive ? _pace.Next(_lastPostMs, snap, key2) : IdleCheckMs;
    }

    private List<RawSession> CollectRaw(bool readPeaks)
    {
        var list = new List<RawSession>();
        foreach (var ep in _endpoints.Values)
        {
            foreach (var s in ep.Sessions.Values)
            {
                var raw = new RawSession(ep.Id, s.Pid, s.Root.Exe, s.State, s.IsSystemSounds, s.SessionIdentifier,
                    s.Root.RootPid, s.Root.RootExe, s.Root.RootPath, s.Root.RootAumid, 0f);
                if (readPeaks && AudioSnapshotBuilder.NeedsMetering(raw, _fxSoundEndpointId, _ownPid))
                {
                    s.Meter ??= s.Control as IAudioMeterInformation;
                    if (s.Meter is not null && s.Meter.GetPeakValue(out float peak) == 0) raw = raw with { Peak = peak };
                }
                list.Add(raw);
            }
        }
        return list;
    }

    private void RebuildEndpoints()
    {
        if (_enumerator is null) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_enumerator.EnumAudioEndpoints(ERender, DeviceStateActive, out var col) == 0 && col is not null)
        {
            try
            {
                col.GetCount(out int n);
                for (int i = 0; i < n; i++)
                {
                    if (col.Item(i, out var dev) != 0 || dev is null) continue;
                    if (dev.GetId(out string id) != 0 || id is null) { Release(dev); continue; }
                    seen.Add(id);
                    if (_endpoints.ContainsKey(id)) { Release(dev); continue; }
                    var ep = OpenEndpoint(id, dev);
                    if (ep is null) { Release(dev); continue; }
                    _endpoints[id] = ep;
                    _log?.Invoke("audio.endpoint.added", new { id, fxSound = ep.IsFxSound });
                }
            }
            finally { Release(col); }
        }
        foreach (var gone in _endpoints.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _endpoints[gone].Dispose();
            _endpoints.Remove(gone);
            _log?.Invoke("audio.endpoint.removed", new { id = gone });
        }
        _fxSoundEndpointId = _endpoints.Values.Where(e => e.IsFxSound).Select(e => e.Id)
            .OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
        foreach (var ep in _endpoints.Values.ToList()) SyncSessions(ep);
    }

    private TrackedEndpoint? OpenEndpoint(string id, IMMDevice dev)
    {
        string? adapter = ReadString(dev, PkeyAdapter);
        string? bus = ReadString(dev, PkeyEnumeratorName);
        bool isFx = string.Equals(adapter?.Trim(), FxSoundAdapter, StringComparison.OrdinalIgnoreCase)
            && (bus is null || string.Equals(bus, "ROOT", StringComparison.OrdinalIgnoreCase));
        var iid = typeof(IAudioSessionManager2).GUID;
        if (dev.Activate(ref iid, ClsCtxAll, 0, out object o) != 0 || o is not IAudioSessionManager2 mgr)
        {
            Release(o);
            return null;
        }
        var ep = new TrackedEndpoint(id, dev, mgr, isFx, new SessionCreatedClient(this));
        // GetSessionEnumerator must be called before OnSessionCreated notifications arrive (research 01 §1.1);
        // SyncSessions right after does it.
        int hr = mgr.RegisterSessionNotification(ep.Notification);
        ep.NotificationRegistered = hr == 0;
        if (hr != 0) _log?.Invoke("audio.endpoint.notify_failed", new { id, hr = $"0x{hr:X8}" });
        return ep;
    }

    private void SyncSessions(TrackedEndpoint ep)
    {
        if (ep.Manager.GetSessionEnumerator(out var se) != 0 || se is null)
        {
            Interlocked.Exchange(ref _endpointsDirty, 1);
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            se.GetCount(out int n);
            for (int j = 0; j < n; j++)
            {
                if (se.GetSession(j, out var ctl) != 0 || ctl is null) continue;
                ctl.GetState(out int state);
                string key = ctl.GetSessionInstanceIdentifier(out string inst) == 0 && inst is not null
                    ? inst
                    : $"#{j}";
                if (state == SessionStates.Expired) { Release(ctl); continue; }
                seen.Add(key);
                if (ep.Sessions.TryGetValue(key, out var known))
                {
                    known.State = state;
                    Release(ctl);                  // balanced with the RCW count taken by GetSession
                    continue;
                }
                ep.Sessions[key] = Track(ctl, state);
            }
        }
        finally { Release(se); }
        foreach (var gone in ep.Sessions.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            ep.Sessions[gone].Dispose();
            ep.Sessions.Remove(gone);
        }
    }

    private TrackedSession Track(IAudioSessionControl2 ctl, int state)
    {
        ctl.GetProcessId(out uint pid);
        bool system = ctl.IsSystemSoundsSession() == 0;
        string? sid = ctl.GetSessionIdentifier(out string s) == 0 ? s : null;
        var root = _tree.ResolveInfo((int)pid);
        var events = new SessionEventsClient(this);
        bool registered = ctl.RegisterAudioSessionNotification(events) == 0;
        return new TrackedSession(ctl, events, registered, (int)pid, system, sid) { State = state, Root = root };
    }

    private void Cleanup()
    {
        foreach (var ep in _endpoints.Values) ep.Dispose();
        _endpoints.Clear();
        if (_enumerator is not null)
        {
            if (_endpointClient is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_endpointClient); }
                catch (COMException) { }
            }
            Release(_enumerator);
            _enumerator = null;
        }
    }

    private void Fail(string error)
    {
        LastError = error;
        _log?.Invoke("audio.monitor.error", new { error });
    }

    // ───────────────────────── tracked objects ─────────────────────────

    private sealed class TrackedEndpoint(string id, IMMDevice device, IAudioSessionManager2 manager, bool isFxSound,
        SessionCreatedClient notification) : IDisposable
    {
        public string Id { get; } = id;
        public IMMDevice Device { get; } = device;
        public IAudioSessionManager2 Manager { get; } = manager;
        public bool IsFxSound { get; } = isFxSound;
        public SessionCreatedClient Notification { get; } = notification;
        public bool NotificationRegistered { get; set; }
        public Dictionary<string, TrackedSession> Sessions { get; } = new(StringComparer.Ordinal);

        public void Dispose()
        {
            foreach (var s in Sessions.Values) s.Dispose();
            Sessions.Clear();
            if (NotificationRegistered)
            {
                try { Manager.UnregisterSessionNotification(Notification); }
                catch (COMException) { }
                NotificationRegistered = false;
            }
            Release(Manager);
            Release(Device);
        }
    }

    private sealed class TrackedSession(IAudioSessionControl2 control, SessionEventsClient events, bool registered,
        int pid, bool isSystemSounds, string? sessionIdentifier) : IDisposable
    {
        public IAudioSessionControl2 Control { get; } = control;
        public IAudioMeterInformation? Meter { get; set; }
        public int Pid { get; } = pid;
        public bool Registered { get; } = registered;
        public bool IsSystemSounds { get; } = isSystemSounds;
        public string? SessionIdentifier { get; } = sessionIdentifier;
        public int State { get; set; }
        public required ProcessRoot Root { get; set; }

        public void Dispose()
        {
            if (Registered)
            {
                try { Control.UnregisterAudioSessionNotification(events); }
                catch (COMException) { }
            }
            Meter = null;                              // same RCW as Control (QueryInterface), released below
            Release(Control);
        }
    }

    // ───────────────────────── COM callbacks: flag + wake only ─────────────────────────

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class EndpointClient(AudioSessionMonitor m) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState) => m.FlagEndpoints();
        public int OnDeviceAdded(string deviceId) => m.FlagEndpoints();
        public int OnDeviceRemoved(string deviceId) => m.FlagEndpoints();
        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId) => 0;
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class SessionCreatedClient(AudioSessionMonitor m) : IAudioSessionNotification
    {
        public int OnSessionCreated(nint newSession) => m.FlagSessions();
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class SessionEventsClient(AudioSessionMonitor m) : IAudioSessionEvents
    {
        public int OnDisplayNameChanged(nint newDisplayName, nint eventContext) => 0;
        public int OnIconPathChanged(nint newIconPath, nint eventContext) => 0;
        public int OnSimpleVolumeChanged(float newVolume, int newMute, nint eventContext) => 0;
        public int OnChannelVolumeChanged(uint channelCount, nint newChannelVolumeArray, uint changedChannel,
            nint eventContext) => 0;
        public int OnGroupingParamChanged(nint newGroupingParam, nint eventContext) => 0;
        public int OnStateChanged(int newState) => m.FlagStates();
        public int OnSessionDisconnected(int disconnectReason) => m.FlagEndpoints();
    }

    private int FlagEndpoints()
    {
        Interlocked.Exchange(ref _endpointsDirty, 1);
        Wake();
        return 0;
    }

    private int FlagStates()
    {
        Interlocked.Exchange(ref _statesDirty, 1);
        Wake();
        return 0;
    }

    private int FlagSessions()
    {
        Interlocked.Exchange(ref _sessionsDirty, 1);
        Wake();
        return 0;
    }
}
