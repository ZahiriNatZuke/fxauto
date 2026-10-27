using System.Text;
using FxAuto.Core.Audio;

namespace FxAuto.Audio;

/// <summary>WASAPI <c>AudioSessionState</c> values.</summary>
internal static class SessionStates
{
    public const int Inactive = 0, Active = 1, Expired = 2;
}

/// <summary>One audio session as read by the monitor, before exclusions. <see cref="Peak"/> is only meaningful
/// for metered sessions (active, on the FxSound virtual endpoint); 0 otherwise.</summary>
internal sealed record RawSession(string EndpointId, int Pid, string? Exe, int State, bool IsSystemSounds,
    string? SessionIdentifier, int RootPid, string? RootExe, string? RootPath, string? RootAumid, float Peak);

/// <summary>
/// Pure rules that turn raw sessions into an <see cref="AudioSnapshot"/> (task 4a + spike outcomes): FxSound.exe,
/// system sounds and FxAuto itself are excluded from <see cref="AudioSnapshot.Sessions"/>; FxSound.exe's active
/// session on a physical endpoint gives <see cref="AudioSnapshot.FxSoundRenderEndpointId"/>; peaks only for active
/// sessions on the FxSound virtual endpoint. No Win32 here, so the test project compiles this file directly.
/// </summary>
internal static class AudioSnapshotBuilder
{
    public const string FxSoundExe = "FxSound.exe";

    public static bool IsFxSound(RawSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return ProcessTreeLogic.SameExe(s.Exe, FxSoundExe) || ProcessTreeLogic.SameExe(s.RootExe, FxSoundExe)
            || (s.SessionIdentifier?.Contains(@"\" + FxSoundExe, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Never a candidate source: system sounds (pid 0), FxSound.exe, FxAuto's own process.</summary>
    public static bool IsExcluded(RawSession s, int ownPid)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.IsSystemSounds || s.Pid <= 0 || s.Pid == ownPid || s.RootPid == ownPid || IsFxSound(s);
    }

    /// <summary>Whether the monitor must read this session's peak meter.</summary>
    public static bool NeedsMetering(RawSession s, string? fxSoundEndpointId, int ownPid)
    {
        ArgumentNullException.ThrowIfNull(s);
        return fxSoundEndpointId is not null && s.State == SessionStates.Active
            && string.Equals(s.EndpointId, fxSoundEndpointId, StringComparison.OrdinalIgnoreCase)
            && !IsExcluded(s, ownPid);
    }

    /// <summary>Physical endpoint where FxSound.exe is rendering now (its active session outside its own virtual
    /// endpoint); null when FxSound has no active render session. Lowest id wins if there are several.</summary>
    public static string? FxSoundRenderEndpoint(IEnumerable<RawSession> sessions, string? fxSoundEndpointId)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return sessions
            .Where(s => s.State == SessionStates.Active && !s.IsSystemSounds && IsFxSound(s)
                && !string.Equals(s.EndpointId, fxSoundEndpointId, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.EndpointId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static AudioSnapshot Build(DateTimeOffset at, string? fxSoundEndpointId, IReadOnlyCollection<RawSession> sessions,
        int ownPid)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var samples = sessions
            .Where(s => s.State != SessionStates.Expired && !IsExcluded(s, ownPid))
            .Select(s => new SessionSample(s.EndpointId, s.Pid, s.RootPid, s.RootExe, s.RootPath, s.RootAumid,
                NeedsMetering(s, fxSoundEndpointId, ownPid) ? Math.Clamp(s.Peak, 0f, 1f) : 0f,
                s.State == SessionStates.Active))
            .OrderBy(s => s.EndpointId, StringComparer.Ordinal).ThenBy(s => s.Pid)
            .ToList();
        return new AudioSnapshot(at, fxSoundEndpointId, FxSoundRenderEndpoint(sessions, fxSoundEndpointId), samples);
    }

    /// <summary>True when some non-excluded session on the FxSound endpoint is active (the monitor then samples at
    /// PollMs; otherwise it sleeps on notifications).</summary>
    public static bool AnyMeteredActive(AudioSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.FxSoundEndpointId is not null && s.Sessions.Any(x => x.Active
            && string.Equals(x.EndpointId, s.FxSoundEndpointId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Everything but peaks and time: a change means the quiet monitor must post a new snapshot.</summary>
    public static string StructureKey(AudioSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var sb = new StringBuilder();
        sb.Append(s.FxSoundEndpointId).Append('|').Append(s.FxSoundRenderEndpointId);
        foreach (var x in s.Sessions)
            sb.Append('|').Append(x.EndpointId).Append(':').Append(x.Pid).Append(':').Append(x.RootPid)
              .Append(':').Append(x.Active ? 'A' : 'i');
        return sb.ToString();
    }
}

/// <summary>
/// Adaptive sampling pace of the audio monitor while metered sessions are active (CPU). It samples at
/// <c>fastMs</c> (pollMs) while something changes (the set of recently audible roots, i.e. audible in the last
/// <c>holdMs</c> so short pauses in speech do not count, or the session structure) and for <c>stableMs</c> after
/// the last change; then at <c>slowMs</c> (500 ms, or pollMs if that is slower). A source that starts is therefore
/// seen within 500 ms and is then sampled at pollMs through its attack; a stop is seen at most
/// <c>slowMs - fastMs</c> later. Pure; monitor thread only.
/// </summary>
internal sealed class SamplingPace(int fastMs, int slowMs = 500, int stableMs = 3000, int holdMs = 1000,
    double peakThreshold = 0.003)
{
    private readonly int _fast = Math.Max(1, fastMs);
    private readonly int _slow = Math.Max(Math.Max(1, fastMs), slowMs);
    private readonly Dictionary<int, long> _lastAudibleMs = [];
    private readonly HashSet<int> _audible = [];
    private string? _key;
    private long _changedAtMs;
    private bool _started;

    /// <summary>Recently audible roots (diagnostics and tests).</summary>
    public IReadOnlyCollection<int> AudibleRoots => _audible;

    /// <summary>Feeds the posted snapshot and returns the delay until the next sample.</summary>
    public int Next(long nowMs, AudioSnapshot snap, string structureKey)
    {
        ArgumentNullException.ThrowIfNull(snap);
        foreach (var x in snap.Sessions)
        {
            if (!x.Active || x.Peak <= peakThreshold) continue;
            if (snap.FxSoundEndpointId is not null
                && !string.Equals(x.EndpointId, snap.FxSoundEndpointId, StringComparison.OrdinalIgnoreCase)) continue;
            _lastAudibleMs[x.RootPid] = nowMs;
        }
        bool changed = !_started || !string.Equals(structureKey, _key, StringComparison.Ordinal);
        foreach (var (pid, at) in _lastAudibleMs.ToList())
        {
            bool recent = nowMs - at <= holdMs;
            if (!recent) _lastAudibleMs.Remove(pid);
            if (recent ? _audible.Add(pid) : _audible.Remove(pid)) changed = true;
        }
        _started = true;
        _key = structureKey;
        if (changed) _changedAtMs = nowMs;
        return nowMs - _changedAtMs < stableMs ? _fast : _slow;
    }
}
