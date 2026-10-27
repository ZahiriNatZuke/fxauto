using System.Diagnostics;
using FxAuto.Core;
using FxAuto.Core.FxSound;

namespace FxAuto.FxSound;

/// <summary>
/// Real access to FxSound. Non-negotiable rules:
/// - Never kills or relaunches FxSound; a command that does not exit is left alive and logged.
/// - Arguments written by hand (never ArgumentList), UseShellExecute=false, no hidden windows.
/// - PIDs launched by FxAuto are recorded: they do not count as "main FxSound" unless reclassified
///   (still alive after 3 s with a JUCE window: FxSound was closed and the command opened it). If there is another
///   FxSound.exe not launched by FxAuto (e.g. the hung main one), it is not reclassified: it stays pending.
/// - Preset operations and "Edit in FxSound" (the only command without --run_minimized, FxCommand.EditInFxSound)
///   go through the same SendAsync; the port never adds or strips arguments.
/// Thread-safe (the controller calls it from the pool).
/// </summary>
internal sealed class FxSoundPort : IFxSoundPort, IDisposable
{
    public const string DefaultExe = @"C:\Program Files\FxSound LLC\FxSound\FxSound.exe";
    private static readonly TimeSpan ReclassifyAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    private sealed record Launched(int Pid, DateTimeOffset At, string Arguments);

    private readonly Func<string?> _exePath;
    private readonly Action<string, object> _log;
    private readonly object _gate = new();
    private readonly Dictionary<int, Launched> _launched = [];
    private readonly HashSet<int> _abandoned = [];   // hung >30 s: neither main nor blocking
    private readonly HashSet<int> _reclassifyBlocked = [];   // already logged as "not reclassifiable" (logged once)

    public FxSoundPort(Func<string?> exePath, Action<string, object> log)
    {
        _exePath = exePath;
        _log = log;
    }

    public string ExePath => _exePath() is { Length: > 0 } p ? p : DefaultExe;

    /// <summary>FxSound's data folder (status.json, FxSound.settings, Presets\, AutoSave\).</summary>
    public static string DataFolder => PresetFileStore.DefaultDataFolder;

    public static string StatusFilePath => Path.Combine(DataFolder, "status.json");

    /// <summary>Built-in presets folder next to the configured FxSound.exe (read-only for FxAuto).</summary>
    public string? FactsoftFolder => PresetFileStore.FactsoftFolderOf(ExePath);

    /// <summary>Preset file store bound to this port's FxSound path (REQ-107 file part).</summary>
    public PresetFileStore CreatePresetFileStore() => PresetFileStore.ForCurrentUser(() => ExePath, _log);

    /// <summary>PIDs of the FxSound.exe processes that are not commands launched by FxAuto.</summary>
    public List<int> MainPids()
    {
        Housekeep();
        var result = new List<int>();
        foreach (var (pid, name) in Native.SnapshotProcesses())
        {
            if (!name.Equals("FxSound.exe", StringComparison.OrdinalIgnoreCase)) continue;
            lock (_gate)
            {
                if (_launched.ContainsKey(pid) || _abandoned.Contains(pid)) continue;
            }
            result.Add(pid);
        }
        return result;
    }

    public bool IsMainRunning() => MainPids().Count > 0;

    private const uint WaitTimeout = 0x00000102;
    private readonly object _aliveGate = new();
    private nint _aliveHandle;          // SYNCHRONIZE handle of the main FxSound being watched (0 = none)
    private int _alivePid;
    private bool _disposed;

    /// <summary>Cheap periodic liveness check for the coordinator (CPU): while the main FxSound found last time is
    /// alive, its process handle answers without a process snapshot. Only when it exited, could not be opened
    /// (e.g. elevated or protected) or was never found does it fall back to <see cref="MainPids"/>. Returns the PID
    /// of a running main FxSound, or null when none runs. Commands keep using <see cref="IsMainRunning"/>.</summary>
    public int? AliveMainPid()
    {
        lock (_aliveGate)
        {
            if (_aliveHandle != 0)
            {
                if (Native.WaitForSingleObject(_aliveHandle, 0) == WaitTimeout)
                {
                    Housekeep();        // no snapshot unless a launched command is still pending
                    return _alivePid;
                }
                Native.CloseHandle(_aliveHandle);
                _aliveHandle = 0;
                _alivePid = 0;
            }
            var pids = MainPids();
            if (pids.Count == 0) return null;
            if (_disposed) return pids[0];
            nint h = Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)pids[0]);
            if (h != 0)
            {
                _aliveHandle = h;
                _alivePid = pids[0];
            }
            return pids[0];
        }
    }

    /// <summary>Closes the watched FxSound handle (never touches FxSound itself).</summary>
    public void Dispose()
    {
        lock (_aliveGate)
        {
            _disposed = true;
            if (_aliveHandle != 0) Native.CloseHandle(_aliveHandle);
            _aliveHandle = 0;
            _alivePid = 0;
        }
    }

    public bool? IsElevated()
    {
        bool? answer = null;
        foreach (int pid in MainPids())
        {
            nint h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h == 0)
            {
                // Cannot even be queried: probably elevated (or protected).
                if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() == Native.ERROR_ACCESS_DENIED) return true;
                continue;
            }
            try
            {
                bool? e = Native.IsTokenElevated(h);
                if (e == true) return true;
                if (e == false) answer = false;
            }
            finally { Native.CloseHandle(h); }
        }
        return answer;
    }

    public async Task<bool> StartMainMinimizedAsync(CancellationToken ct)
    {
        if (IsMainRunning()) return true;
        Process? p;
        try
        {
            p = Process.Start(new ProcessStartInfo(ExePath) { Arguments = "--run_minimized", UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _log("fxsound.start.error", new { error = ex.Message, exe = ExePath });
            return false;
        }
        if (p is null) return false;
        var until = DateTimeOffset.Now + StartReadyTimeout;
        using (p)
        {
            _log("fxsound.start", new { pid = p.Id });
            // 1) Main JUCE window created.
            bool window = false;
            while (DateTimeOffset.Now < until)
            {
                if (p.HasExited) break;   // it may hand over to another instance that was already starting
                if (Native.ProcessHasWindowClass(p.Id, "JUCE")) { window = true; break; }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            if (!window && !(p.HasExited && IsMainRunning()))
            {
                _log("fxsound.start.no_window", new { pid = p.Id });
                return false;
            }
        }
        // 2) Answers --status: status.json rewritten after the send (same 10 s total cap, REQ-008).
        bool ok = await WaitStatusAnswerAsync(until, ct).ConfigureAwait(false);
        _log(ok ? "fxsound.start.ready" : "fxsound.start.no_response", new { });
        return ok;
    }

    private static readonly TimeSpan StartReadyTimeout = TimeSpan.FromSeconds(10);

    private async Task<bool> WaitStatusAnswerAsync(DateTimeOffset until, CancellationToken ct)
    {
        while (DateTimeOffset.Now < until)
        {
            if (!IsMainRunning()) return false;   // never --status against a closed FxSound
            var (_, t0) = ReadStatusFile();
            var sentAt = DateTimeOffset.Now;
            var left = until - DateTimeOffset.Now;
            if (left <= TimeSpan.Zero) break;
            var exit = await SendAsync(FxCommand.Status, left, ct).ConfigureAwait(false);
            if (exit is null) return false;   // hung: stays pending (never killed)
            while (DateTimeOffset.Now < until)
            {
                var (_, w) = ReadStatusFile();
                // 1 s margin for the file time resolution.
                if (w is DateTimeOffset written && written != t0 && written >= sentAt - TimeSpan.FromSeconds(1)) return true;
                await Task.Delay(100, ct).ConfigureAwait(false);
                if (DateTimeOffset.Now - sentAt > TimeSpan.FromSeconds(2)) break;   // did not write: ask again
            }
        }
        return false;
    }

    public async Task<int?> SendAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        Process? p;
        try
        {
            p = Process.Start(new ProcessStartInfo(ExePath) { Arguments = arguments, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _log("fxsound.command.error", new { error = ex.Message, args = arguments });
            return null;
        }
        if (p is null) return null;

        int pid = p.Id;
        lock (_gate) _launched[pid] = new Launched(pid, DateTimeOffset.Now, arguments);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Timeout reached: NOT killed. It stays recorded; Housekeep decides what it is.
                _log("fxsound.command.timeout", new { pid, args = arguments, ms = (long)timeout.TotalMilliseconds });
                p.Dispose();
                return null;
            }
            int code = p.ExitCode;
            lock (_gate) _launched.Remove(pid);
            p.Dispose();
            return code;
        }
        catch (InvalidOperationException)
        {
            lock (_gate) _launched.Remove(pid);
            p.Dispose();
            return null;
        }
    }

    public (string? Json, DateTimeOffset? WrittenAt) ReadStatusFile()
    {
        string path = StatusFilePath;
        try
        {
            if (!File.Exists(path)) return (null, null);
            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToLocalTime();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return (sr.ReadToEnd(), written);
        }
        catch (IOException) { return (null, null); }
        catch (UnauthorizedAccessException) { return (null, null); }
    }

    public bool HasPendingLaunched
    {
        get
        {
            Housekeep();
            lock (_gate) return _launched.Count > 0;
        }
    }

    /// <summary>PIDs launched by FxAuto that are still alive and not reclassified (for the log).</summary>
    public IReadOnlyList<int> PendingPids
    {
        get { lock (_gate) return _launched.Keys.ToList(); }
    }

    // Reviews launched PIDs: dead → removed; alive >3 s with a JUCE window → main; alive >30 s → abandoned.
    private void Housekeep()
    {
        List<Launched> snapshot;
        lock (_gate) snapshot = _launched.Values.ToList();
        if (snapshot.Count == 0) return;
        var alive = Native.SnapshotProcesses();
        var now = DateTimeOffset.Now;
        foreach (var l in snapshot)
        {
            if (!alive.TryGetValue(l.Pid, out var name) || !name.Equals("FxSound.exe", StringComparison.OrdinalIgnoreCase))
            {
                lock (_gate)
                {
                    _launched.Remove(l.Pid);
                    _reclassifyBlocked.Remove(l.Pid);
                }
                continue;
            }
            var age = now - l.At;
            if (age > ReclassifyAfter && Native.ProcessHasWindowClass(l.Pid, "JUCE"))
            {
                // Only becomes main if there is no other FxSound.exe foreign to FxAuto (e.g. the hung main one):
                // in that case this copy stays pending and, after 30 s, is abandoned.
                int? other = OtherMainPid(alive);
                if (other is null)
                {
                    lock (_gate)
                    {
                        _launched.Remove(l.Pid);
                        _reclassifyBlocked.Remove(l.Pid);
                    }
                    _log("fxsound.reclassified", new { pid = l.Pid, args = l.Arguments });
                    continue;
                }
                bool first;
                lock (_gate) first = _reclassifyBlocked.Add(l.Pid);
                if (first)
                    _log("fxsound.reclassified.no", new { pid = l.Pid, args = l.Arguments, other,
                                                           reason = "another FxSound.exe not launched by FxAuto exists" });
            }
            if (age > GiveUpAfter)
            {
                lock (_gate)
                {
                    _launched.Remove(l.Pid);
                    _reclassifyBlocked.Remove(l.Pid);
                    _abandoned.Add(l.Pid);
                }
                _log("fxsound.unresponsive", new { pid = l.Pid, args = l.Arguments });
            }
        }
    }

    // First live FxSound.exe not launched by FxAuto (neither pending nor abandoned); null if none.
    private int? OtherMainPid(IReadOnlyDictionary<int, string> alive)
    {
        lock (_gate)
        {
            foreach (var (pid, name) in alive)
            {
                if (!name.Equals("FxSound.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (_launched.ContainsKey(pid) || _abandoned.Contains(pid)) continue;
                return pid;
            }
        }
        return null;
    }
}
