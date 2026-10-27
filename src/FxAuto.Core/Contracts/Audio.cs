namespace FxAuto.Core.Audio;

// Layer contract (plan.md "Layer contract", frozen in task 3).

/// <summary>One audio session sample, already mapped to its root process.</summary>
public sealed record SessionSample(string EndpointId, int Pid, int RootPid, string? RootExe, string? RootPath,
    string? RootAumid, float Peak, bool Active);

/// <summary>Immutable snapshot posted by the audio monitor. Sessions exclude FxSound, system sounds and FxAuto.
/// FxSoundRenderEndpointId = physical endpoint where FxSound.exe renders (REQ-105 cross-check).</summary>
public sealed record AudioSnapshot(DateTimeOffset At, string? FxSoundEndpointId,
    string? FxSoundRenderEndpointId,
    IReadOnlyList<SessionSample> Sessions);

/// <summary>Sounding detection timings (REQ-101).</summary>
public sealed record SoundingTimings(int PollMs = 250, double PeakThreshold = 0.003, double AttackRatio = 0.6,
    int AttackMs = 1500, int BurstIgnoreMs = 2000, int ReleaseMs = 8000, int GameReleaseMs = 20000);

/// <summary>Playing state of one root process after hysteresis.</summary>
public sealed record PlayingState(int RootPid, string? Exe, bool Playing, DateTimeOffset? AudibleSince,
    DateTimeOffset? LastAudibleAt);
