using FxAuto.Core;
using FxAuto.Core.Arbitration;
using FxAuto.Core.Audio;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.FxSound;
using FxAuto.Core.Presets;

namespace FxAuto.App;

// Platform-neutral on purpose (no WinForms, no P/Invoke): this file is also compiled into tests/FxAuto.Tests
// (linked) so the coordinator's pure decisions are unit-tested (CoordinatorLogicTests).

/// <summary>Preset FxAuto wants active, with its volume leveling and a readable reason (never a window title).</summary>
internal sealed record PresetTarget(string Preset, double Leveling, string Why);

/// <summary>
/// When a command must be sent for the expected preset:
/// nothing is sent while the expected preset is already verified, while a command for it is in flight (30 s
/// expiry), or while waiting after a failure (backoff 5 s → 30 s → 120 s, or the given wait). Not thread-safe:
/// coordinator thread only.
/// </summary>
internal sealed class ApplyGate
{
    private static readonly TimeSpan InFlightExpiry = TimeSpan.FromSeconds(30);

    private readonly IClock _clock;
    private string? _verified;
    private string? _inFlight;
    private DateTimeOffset _inFlightAt;
    private string? _failPreset;
    private int _failCount;
    private DateTimeOffset _retryAt;
    private string? _lastFailure;

    public ApplyGate(IClock clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>What should be active (null = nothing to impose: paused, editing, unknown device…).</summary>
    public PresetTarget? Expected { get; private set; }

    /// <summary>Last preset verified as applied (null if unknown).</summary>
    public string? Verified => _verified;

    /// <summary>Reason of the last failure of the expected preset (null if none).</summary>
    public string? LastFailure => _failPreset is not null && _failPreset == Expected?.Preset ? _lastFailure : null;

    /// <summary>Replaces the target. Same preset and leveling: only the reason changes.</summary>
    public void SetExpected(PresetTarget? target) => Expected = target;

    /// <summary>The target to send now, or null. Marks it in flight.</summary>
    public PresetTarget? TryEmit()
    {
        var now = _clock.Now;
        if (!WouldEmit(now)) return null;
        _inFlight = Expected!.Preset;
        _inFlightAt = now;
        _verified = null;          // while the command is on its way, what is active is unknown
        return Expected;
    }

    public bool WouldEmit(DateTimeOffset now)
    {
        var exp = Expected;
        if (exp is null) return false;
        if (string.Equals(exp.Preset, _verified, StringComparison.Ordinal)) return false;
        if (_inFlight == exp.Preset && now - _inFlightAt < InFlightExpiry) return false;
        if (_failPreset == exp.Preset && now < _retryAt) return false;
        return true;
    }

    /// <summary>Next instant at which <see cref="TryEmit"/> may return something (retry after a failure).</summary>
    public DateTimeOffset? NextDueAt
    {
        get
        {
            var exp = Expected;
            if (exp is null || exp.Preset == _verified) return null;
            if (_failPreset == exp.Preset) return _retryAt;
            if (_inFlight == exp.Preset) return _inFlightAt + InFlightExpiry;
            return null;
        }
    }

    public void MarkApplied(string preset)
    {
        _verified = preset;
        if (_inFlight == preset) _inFlight = null;
        if (_failPreset == preset) ClearFailure();
    }

    /// <summary>Backoff if the failed preset is the expected one; otherwise only releases the in-flight command.</summary>
    public void MarkFailed(string preset, string reason, TimeSpan? wait = null)
    {
        if (_inFlight == preset) _inFlight = null;
        if (Expected?.Preset != preset) return;
        _verified = null;
        if (_failPreset != preset)
        {
            _failPreset = preset;
            _failCount = 0;
        }
        _failCount++;
        _lastFailure = reason;
        var delay = wait ?? _failCount switch
        {
            1 => TimeSpan.FromSeconds(5),
            2 => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromSeconds(120),
        };
        _retryAt = _clock.Now + delay;
    }

    /// <summary>What status.json says is active. The expected preset counts as verified only if it is selected, on,
    /// unmodified and with the expected leveling. Anything else that is selected clears the verification.
    /// true when the expected preset was marked verified.</summary>
    public bool ObserveStatus(FxStatus st, double levelingTolerance = 0.05)
    {
        ArgumentNullException.ThrowIfNull(st);
        if (_inFlight is not null) return false;          // a command is on its way: what was read may be stale
        var exp = Expected;
        bool ok = !string.IsNullOrEmpty(st.SelectedPreset) && st.Power && st.SelectedModified != true;
        if (ok && exp is not null && string.Equals(st.SelectedPreset, exp.Preset, StringComparison.Ordinal)
            && (st.VolumeLeveling is not double lv || Math.Abs(lv - exp.Leveling) <= levelingTolerance))
        {
            _verified = st.SelectedPreset;
            if (_failPreset == st.SelectedPreset) ClearFailure();
            return true;
        }
        if (!string.Equals(st.SelectedPreset, _verified, StringComparison.Ordinal) || !ok) _verified = null;
        return false;
    }

    /// <summary>A preset was renamed in FxSound: the expected, verified and in-flight names follow it, and a failure
    /// recorded for the old name (which no longer exists) is forgotten.</summary>
    public void RenamePreset(string from, string to)
    {
        if (Expected is { } exp && string.Equals(exp.Preset, from, StringComparison.Ordinal)) Expected = exp with { Preset = to };
        if (string.Equals(_verified, from, StringComparison.Ordinal)) _verified = to;
        if (string.Equals(_inFlight, from, StringComparison.Ordinal)) _inFlight = to;
        if (string.Equals(_failPreset, from, StringComparison.Ordinal)) ClearFailure();
    }

    /// <summary>Forgets the verified preset (FxSound restarted, preset deleted…): the next emit re-applies.</summary>
    public void ResetApplied()
    {
        _verified = null;
        _inFlight = null;
    }

    private void ClearFailure()
    {
        _failPreset = null;
        _failCount = 0;
        _lastFailure = null;
    }
}

/// <summary>How the main FxSound process changed between two liveness checks.</summary>
/// <summary>Mode and the preset names it holds (see <see cref="CoordinatorLogic.FollowPresetOp"/>).</summary>
internal sealed record ModeNames(Mode Mode, Mode ModeBeforeEdit, string? Locked, string? Editing, string? EditPending);

internal enum FxProcessChange { None, Opened, Closed, Restarted }

/// <summary>Pure helpers of the coordinator's decision cycle.</summary>
internal static class CoordinatorLogic
{
    /// <summary>Candidates for the arbiter: every root the detector reports as playing, plus the foreground root
    /// (playing or not). A silent foreground root that the detector does not know is added only when it is a game
    /// (REQ-104 "game loading"). Exited roots are left out.</summary>
    public static IReadOnlyList<CandidateSource> BuildCandidates(IReadOnlyList<PlayingState> states, int? foregroundRoot,
        Func<int, SourceClass> classify, Func<int, DateTimeOffset?> processStart, ISet<int>? exited = null)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(classify);
        ArgumentNullException.ThrowIfNull(processStart);
        var list = new List<CandidateSource>();
        bool fgSeen = false;
        foreach (var s in states)
        {
            if (exited is not null && exited.Contains(s.RootPid)) continue;
            bool fg = foregroundRoot == s.RootPid;
            if (fg) fgSeen = true;
            if (!s.Playing && !fg) continue;
            list.Add(new CandidateSource(s.RootPid, classify(s.RootPid), fg, s.Playing, s.AudibleSince, s.LastAudibleAt,
                processStart(s.RootPid)));
        }
        if (!fgSeen && foregroundRoot is int root && (exited is null || !exited.Contains(root)))
        {
            var c = classify(root);
            if (c.IsGame) list.Add(new CandidateSource(root, c, true, false, null, null, processStart(root)));
        }
        return list;
    }

    /// <summary>Roots whose playing state changed between two detector outputs (true = started playing).</summary>
    public static IReadOnlyList<(int RootPid, bool Playing)> PlayingChanges(IReadOnlyDictionary<int, bool> before,
        IReadOnlyList<PlayingState> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<(int, bool)>();
        var seen = new HashSet<int>();
        foreach (var s in after)
        {
            seen.Add(s.RootPid);
            bool was = before.TryGetValue(s.RootPid, out var b) && b;
            if (was != s.Playing) changes.Add((s.RootPid, s.Playing));
        }
        foreach (var (pid, was) in before)
            if (was && !seen.Contains(pid)) changes.Add((pid, false));
        return changes;
    }

    /// <summary>Snapshot sessions minus the ignored apps (case-insensitive exe names).</summary>
    public static AudioSnapshot WithoutIgnored(AudioSnapshot s, IReadOnlyCollection<string> ignoredApps)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(ignoredApps);
        if (ignoredApps.Count == 0 || s.Sessions.All(x => !IsListed(x.RootExe, ignoredApps))) return s;
        return s with { Sessions = s.Sessions.Where(x => !IsListed(x.RootExe, ignoredApps)).ToList() };
    }

    /// <summary>exe file name is in the list (case-insensitive).</summary>
    public static bool IsListed(string? exe, IReadOnlyCollection<string> list)
        => !string.IsNullOrEmpty(exe) && list.Any(x => string.Equals(x?.Trim(), exe, StringComparison.OrdinalIgnoreCase));

    /// <summary>A foreground window that must not change the foreground source: unreadable/elevated, no exe,
    /// FxAuto itself, an ignored app or a launcher.</summary>
    public static bool IsNeutralForeground(ForegroundInfo fg, int ownPid, IReadOnlyCollection<string> ignoredApps,
        IReadOnlyCollection<string> launchers)
    {
        ArgumentNullException.ThrowIfNull(fg);
        return fg.Unreadable || string.IsNullOrEmpty(fg.Exe) || fg.Pid == ownPid
            || IsListed(fg.Exe, ignoredApps) || IsListed(fg.Exe, launchers);
    }

    /// <summary>Key used for "once per device" notifications of unknown devices (stable across endpoint ids).</summary>
    public static string UnknownDeviceKey(string? adapter, string friendlyName, Core.Devices.FormFactor ff)
        => (adapter is null ? friendlyName : Core.Devices.DeviceKeys.NormalizeAdapter(adapter)).ToLowerInvariant() + "|" + ff;

    /// <summary>Remaining pause (null = not paused or until restart): Paused with an end in the past returns to Auto.</summary>
    public static bool PauseExpired(Mode mode, DateTimeOffset? pausedUntil, DateTimeOffset now)
        => mode == Mode.Paused && pausedUntil is { } until && now >= until;

    /// <summary>True when two detector outputs give the arbiter the same inputs (same roots in the same order, same
    /// exe, playing flag, AudibleSince and "ever audible"): then an audio sample changed nothing worth a decision
    /// (CPU). LastAudibleAt moves on every sample while playing; only whether it is set matters to the arbiter, and
    /// the release instant it drives is a timed due (<c>SoundingDetector.NextDueAt</c>).</summary>
    public static bool SameDecisionStates(IReadOnlyList<PlayingState> before, IReadOnlyList<PlayingState> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Count != after.Count) return false;
        for (int i = 0; i < before.Count; i++)
        {
            var a = before[i];
            var b = after[i];
            if (a.RootPid != b.RootPid || a.Playing != b.Playing || a.AudibleSince != b.AudibleSince
                || a.LastAudibleAt.HasValue != b.LastAudibleAt.HasValue
                || !string.Equals(a.Exe, b.Exe, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>When a tab read may be requested for a browser: when the tracker wants it (<paramref name="trackerDue"/>,
    /// null = never), but not sooner than <paramref name="minInterval"/> after the previous request.</summary>
    public static DateTimeOffset? TabRequestDue(DateTimeOffset? trackerDue, DateTimeOffset? lastRequest, TimeSpan minInterval)
    {
        if (trackerDue is not { } due) return null;
        if (lastRequest is { } last && last + minInterval > due) return last + minInterval;
        return due;
    }

    /// <summary>When the coordinator timer must fire next: the earliest pending instant, not sooner than
    /// <paramref name="minDelay"/> from now (overdue work is retried at that pace, as the old fixed tick did) and not
    /// later than <paramref name="maxDelay"/> (safety against clock jumps). null = nothing pending: the timer stops.</summary>
    public static DateTimeOffset? NextWake(DateTimeOffset now, TimeSpan minDelay, TimeSpan maxDelay,
        params DateTimeOffset?[] dues)
    {
        var e = Earliest(dues);
        if (e is not { } at) return null;
        if (at < now + minDelay) return now + minDelay;
        if (at > now + maxDelay) return now + maxDelay;
        return at;
    }

    /// <summary>Main FxSound change between two checks: a different PID while it was running counts as closed and
    /// opened again (re-apply, re-query, re-check elevation).</summary>
    public static FxProcessChange FxChange(bool wasRunning, int? oldPid, int? newPid)
    {
        if (newPid is null) return wasRunning ? FxProcessChange.Closed : FxProcessChange.None;
        if (!wasRunning) return FxProcessChange.Opened;
        return oldPid is int old && old != newPid ? FxProcessChange.Restarted : FxProcessChange.None;
    }

    /// <summary>Roots that kept playing and became audible again after at least <paramref name="gap"/> without any
    /// audible sample (in a browser: a tab paused and another one, or the next video, started).</summary>
    public static IReadOnlyList<int> ResumedAfterSilence(IReadOnlyList<PlayingState> before,
        IReadOnlyList<PlayingState> after, TimeSpan gap)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        List<int>? list = null;
        foreach (var a in after)
        {
            if (!a.Playing || a.LastAudibleAt is not { } now) continue;
            foreach (var b in before)
            {
                if (b.RootPid != a.RootPid) continue;
                if (b.Playing && b.LastAudibleAt is { } prev && now - prev >= gap) (list ??= []).Add(a.RootPid);
                break;
            }
        }
        return list ?? (IReadOnlyList<int>)[];
    }

    /// <summary>Per root (filtered by <paramref name="include"/>): a signature of its audio sessions (PID and active
    /// state), to notice sessions appearing, disappearing or starting/stopping.</summary>
    public static Dictionary<int, int> SessionSignatures(AudioSnapshot snap, Func<int, bool> include)
    {
        ArgumentNullException.ThrowIfNull(snap);
        ArgumentNullException.ThrowIfNull(include);
        var result = new Dictionary<int, int>();
        var included = new Dictionary<int, bool>();
        foreach (var s in snap.Sessions)
        {
            if (!included.TryGetValue(s.RootPid, out var inc)) included[s.RootPid] = inc = include(s.RootPid);
            if (!inc) continue;
            result.TryGetValue(s.RootPid, out var h);
            result[s.RootPid] = HashCode.Combine(h, s.Pid, s.Active);
        }
        return result;
    }

    /// <summary>Roots present in both maps whose signature changed (a root that just appeared or left is handled by
    /// the start/stop of playback).</summary>
    public static IReadOnlyList<int> ChangedSignatures(IReadOnlyDictionary<int, int> before, IReadOnlyDictionary<int, int> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        List<int>? list = null;
        foreach (var (root, sig) in after)
            if (before.TryGetValue(root, out var old) && old != sig) (list ??= []).Add(root);
        return list ?? (IReadOnlyList<int>)[];
    }

    /// <summary>Preset names the coordinator keeps between decisions after a preset operation: renames carry the
    /// locked, edited and edit-pending presets to their new names; deleting the locked preset ends the lock (back
    /// to Auto). <c>LockEnded</c> is the deleted locked preset (null if the lock did not end).</summary>
    public static (ModeNames Next, string? LockEnded) FollowPresetOp(ModeNames cur, PresetOpKind kind,
        IReadOnlyList<(string From, string To)> done)
    {
        ArgumentNullException.ThrowIfNull(cur);
        ArgumentNullException.ThrowIfNull(done);
        if (kind is PresetOpKind.Rename)
        {
            var next = cur;
            foreach (var (from, to) in done)
            {
                if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) continue;
                next = next with
                {
                    Locked = next.Locked == from ? to : next.Locked,
                    Editing = next.Editing == from ? to : next.Editing,
                    EditPending = next.EditPending == from ? to : next.EditPending,
                };
            }
            return (next, null);
        }
        if (kind is PresetOpKind.Delete && cur.Locked is { } locked && done.Any(d => d.From == locked))
        {
            return (cur with
            {
                Mode = cur.Mode == Mode.Locked ? Mode.Auto : cur.Mode,
                ModeBeforeEdit = cur.ModeBeforeEdit == Mode.Locked ? Mode.Auto : cur.ModeBeforeEdit,
                Locked = null,
            }, locked);
        }
        return (cur, null);
    }

    /// <summary>User mode to persist in runtime-state.json: the mode to go back to after an edit, Locked with its
    /// preset, or Paused with an end. Auto and "Paused until restart" persist nothing (null).</summary>
    public static SavedMode? ToSaved(Mode mode, Mode modeBeforeEdit, string? locked, DateTimeOffset? pausedUntil)
        => (mode == Mode.Editing ? modeBeforeEdit : mode) switch
        {
            Mode.Locked when !string.IsNullOrEmpty(locked) => new SavedMode(Mode.Locked, locked, null),
            Mode.Paused when pausedUntil is not null => new SavedMode(Mode.Paused, null, pausedUntil),
            _ => null,
        };

    /// <summary>Mode to start in from the persisted one: Locked only when its preset still exists
    /// (<paramref name="presetExists"/>; otherwise Auto and <c>LockDropped</c> names it), Paused only while its end
    /// is in the future. Anything else starts in Auto.</summary>
    public static (Mode Mode, string? Locked, DateTimeOffset? PausedUntil, string? LockDropped) RestoreMode(
        SavedMode? saved, DateTimeOffset now, Func<string, bool> presetExists)
    {
        ArgumentNullException.ThrowIfNull(presetExists);
        switch (saved)
        {
            case { Mode: Mode.Locked, LockedPreset: { Length: > 0 } p }:
                return presetExists(p) ? (Mode.Locked, p, null, null) : (Mode.Auto, null, null, p);
            case { Mode: Mode.Paused, PausedUntil: { } until } when until > now:
                return (Mode.Paused, null, until, null);
            default:
                return (Mode.Auto, null, null, null);
        }
    }

    /// <summary>Earliest of several optional instants.</summary>
    public static DateTimeOffset? Earliest(params DateTimeOffset?[] xs)
    {
        DateTimeOffset? best = null;
        foreach (var x in xs)
            if (x is { } v && (best is null || v < best)) best = v;
        return best;
    }
}

/// <summary>"Once per key" de-duplication for notifications (one session).</summary>
internal sealed class OnceSet
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>true the first time <paramref name="key"/> is seen.</summary>
    public bool First(string key) => _seen.Add(key);

    /// <summary>true when <paramref name="key"/> was already seen.</summary>
    public bool Seen(string key) => _seen.Contains(key);

    public void Forget(string key) => _seen.Remove(key);
}
