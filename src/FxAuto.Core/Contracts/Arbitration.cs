using FxAuto.Core.Classification;

namespace FxAuto.Core.Arbitration;

// Layer contract (plan.md "Layer contract", frozen in task 3).

/// <summary>A source the arbiter can choose (REQ-104).</summary>
public sealed record CandidateSource(int RootPid, SourceClass Class, bool IsForeground, bool Playing,
    DateTimeOffset? AudibleSince, DateTimeOffset? LastAudibleAt, DateTimeOffset? ProcessStart);

/// <summary>Priority level that produced the decision, highest first.</summary>
public enum WinLevel { Locked, Editing, Hold, ForegroundGamePlaying, ForegroundGameLoading,
    ForegroundPlaying, BackgroundGamePlaying, BackgroundMostRecent, IdleGrace, IdleDefault }

/// <summary>What happens with sources of category <c>call</c>. Ignore: <c>call</c> never wins.</summary>
public enum CallPolicy { Ignore, Category }

/// <summary>Arbiter timings.</summary>
public sealed record ArbiterTimings(int GameLoadingMaxMs = 60000, int IdleGraceMs = 60000, int MinDwellMs = 5000,
    CallPolicy CallPolicy = CallPolicy.Ignore);

/// <summary>Arbiter result: the winner (null = none), the level and a readable reason.</summary>
public sealed record ArbiterDecision(CandidateSource? Winner, WinLevel Level, string Reason)
{
    /// <summary><see cref="Reason"/> as data for the UI (translated there); the log keeps <see cref="Reason"/>.</summary>
    public Localization.Msg? ReasonText { get; init; }
}
