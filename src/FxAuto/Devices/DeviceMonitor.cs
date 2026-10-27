using System.Runtime.InteropServices;
using FxAuto.Core.Devices;
using Timer = System.Threading.Timer;

namespace FxAuto.Devices;

/// <summary>
/// Render endpoint monitor (plan "Threading model", REQ-105). IMMNotificationClient callbacks only re-arm a timer
/// (never block, never touch COM, never register/unregister). 750 ms after the last callback the monitor reads a full
/// snapshot on the thread pool (MTA) and raises <see cref="Changed"/> if it differs from the last one raised; a
/// second read ~3 s later absorbs FxSound's transient default switch (research 02 §4.2) and is raised only if
/// something changed again. Read-only: it never changes a device or a Windows default.
/// </summary>
internal sealed class DeviceMonitor : IDeviceMonitor
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);
    public static readonly TimeSpan SecondReadDelay = TimeSpan.FromSeconds(3);

    private readonly Action<string, object>? _log;
    private readonly object _gate = new();
    private readonly object _readGate = new();
    private readonly Timer _first;
    private readonly Timer _second;
    private EndpointInterop.IMMDeviceEnumerator? _enumerator;
    private Client? _client;
    private EndpointSnapshot _last = new(DateTimeOffset.MinValue, [], null, null);
    private EndpointSnapshot? _lastRaised;
    private long _callbacks;
    private bool _started;
    private bool _disposed;

    public event Action<EndpointSnapshot>? Changed;

    public DeviceMonitor(Action<string, object>? log = null)
    {
        _log = log;
        _first = new Timer(_ => OnTimer(second: false), null, Timeout.Infinite, Timeout.Infinite);
        _second = new Timer(_ => OnTimer(second: true), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Callbacks received since Start (diagnostics / probe).</summary>
    public long CallbackCount => Interlocked.Read(ref _callbacks);

    /// <summary>Last snapshot read (the initial one is read synchronously by <see cref="Start"/>).</summary>
    public EndpointSnapshot Snapshot()
    {
        lock (_gate) return _last;
    }

    /// <summary>Creates the enumerator on an MTA pool thread, reads the first snapshot and registers the
    /// notification client. Does not raise <see cref="Changed"/> for the first snapshot.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
        }
        var t = Task.Run(() =>
        {
            try
            {
                var e = EndpointEnumerator.Create();
                var snap = EndpointEnumerator.Read(e, err => _log?.Invoke("devices.read.error", new { error = err }));
                var client = new Client(this);
                int hr = e.RegisterEndpointNotificationCallback(client);
                lock (_gate)
                {
                    _enumerator = e;
                    _last = snap;
                    _lastRaised = snap;
                    if (hr == 0) _client = client;
                }
                if (hr != 0) _log?.Invoke("devices.register.error", new { hr = $"0x{hr:X8}" });
                _log?.Invoke("devices.start", Describe(snap));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException)
            {
                _log?.Invoke("devices.start.error", new { error = ex.Message });
            }
        });
        if (!t.Wait(TimeSpan.FromSeconds(5))) _log?.Invoke("devices.start.timeout", new { });
    }

    /// <summary>Called from COM callbacks: only (re)arms the debounce timer.</summary>
    private void Notify()
    {
        Interlocked.Increment(ref _callbacks);
        lock (_gate)
        {
            if (_disposed) return;
            _first.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer(bool second)
    {
        EndpointSnapshot snap;
        bool raise;
        lock (_readGate)
        {
            EndpointInterop.IMMDeviceEnumerator? e;
            lock (_gate)
            {
                if (_disposed) return;
                e = _enumerator;
                // The first read (re)schedules the confirmation read.
                if (!second) _second.Change(SecondReadDelay, Timeout.InfiniteTimeSpan);
            }
            if (e is null) return;
            try
            {
                snap = EndpointEnumerator.Read(e, err => _log?.Invoke("devices.read.error", new { error = err }));
            }
            catch (COMException ex)
            {
                _log?.Invoke("devices.read.error", new { error = ex.Message, hr = $"0x{ex.HResult:X8}" });
                return;
            }
            lock (_gate)
            {
                if (_disposed) return;
                _last = snap;
                raise = !EndpointMapping.SameContent(_lastRaised, snap);
                if (raise) _lastRaised = snap;
            }
        }
        if (!raise) return;
        _log?.Invoke(second ? "devices.changed.confirm" : "devices.changed", Describe(snap));
        try
        {
            Changed?.Invoke(snap);
        }
#pragma warning disable CA1031 // a failing handler must not kill the timer thread
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Invoke("devices.handler.error", new { error = ex.Message });
        }
    }

    private static object Describe(EndpointSnapshot s) => new
    {
        defaultMultimedia = s.DefaultMultimediaId,
        defaultConsole = s.DefaultConsoleId,
        endpoints = s.Endpoints.Select(e => new { id = e.EndpointId, name = e.FriendlyName, adapter = e.Adapter,
            formFactor = e.FormFactor.ToString(), bus = e.Bus, state = e.State, fxsound = e.IsFxSoundVirtual }).ToList(),
    };

    public void Dispose()
    {
        EndpointInterop.IMMDeviceEnumerator? e;
        Client? c;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            e = _enumerator;
            c = _client;
            _enumerator = null;
            _client = null;
        }
        _first.Dispose();
        _second.Dispose();
        if (e is null) return;
        // Unregister on an MTA thread, never from inside a callback; capped wait.
        var t = Task.Run(() =>
        {
            lock (_readGate)
            {
                try
                {
                    if (c is not null) e.UnregisterEndpointNotificationCallback(c);
                }
                catch (COMException) { }
                EndpointInterop.Release(e);
            }
        });
        t.Wait(TimeSpan.FromSeconds(2));
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Client(DeviceMonitor owner) : EndpointInterop.IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState) { owner.Notify(); return 0; }
        public int OnDeviceAdded(string deviceId) { owner.Notify(); return 0; }
        public int OnDeviceRemoved(string deviceId) { owner.Notify(); return 0; }

        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
        {
            if (flow == EndpointInterop.ERender) owner.Notify();
            return 0;
        }

        // Fires very often (volume, format...): only the names the snapshot carries matter.
        public int OnPropertyValueChanged(string deviceId, EndpointInterop.PropertyKey key)
        {
            if (IsNameKey(key)) owner.Notify();
            return 0;
        }

        private static bool IsNameKey(EndpointInterop.PropertyKey k)
            => Same(k, EndpointInterop.PkeyFriendlyName) || Same(k, EndpointInterop.PkeyDeviceDesc)
               || Same(k, EndpointInterop.PkeyInterfaceFriendlyName);

        private static bool Same(EndpointInterop.PropertyKey a, EndpointInterop.PropertyKey b)
            => a.FormatId == b.FormatId && a.PropertyId == b.PropertyId;
    }
}
