namespace FxAuto.Core.Updates;

/// <summary>When the automatic update check runs: once every 24 h, the first one a few minutes after start-up, with an
/// exponential back-off after errors and never before a GitHub rate-limit window ends.</summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    /// <summary>Back-off after <paramref name="failures"/> consecutive errors: 30 min, 1 h, 2 h, 4 h, then 6 h.</summary>
    public static TimeSpan Backoff(int failures)
    {
        if (failures <= 0) return TimeSpan.Zero;
        var ticks = FirstBackoff.Ticks * Math.Pow(2, Math.Min(failures - 1, 10));
        return ticks >= MaxBackoff.Ticks ? MaxBackoff : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>Next automatic check, or null when automatic checks are off.</summary>
    public static DateTimeOffset? NextCheckAt(UpdateState state, DateTimeOffset appStartedAt)
    {
        if (!state.CheckAutomatically) return null;
        DateTimeOffset due = state.ConsecutiveFailures > 0 && state.LastCheckAt is { } failedAt
            ? failedAt + Backoff(state.ConsecutiveFailures)
            : state.LastSuccessAt is { } ok ? ok + Interval : DateTimeOffset.MinValue;
        if (state.RetryNotBefore is { } rl && rl > due) due = rl;
        var earliest = appStartedAt + StartupDelay;
        return due > earliest ? due : earliest;
    }

    public static bool IsDue(UpdateState state, DateTimeOffset appStartedAt, DateTimeOffset now)
        => NextCheckAt(state, appStartedAt) is { } at && at <= now;

    /// <summary>State after a check: success clears the error count; an error increments it; a rate limit records
    /// the window without counting as an error.</summary>
    public static UpdateState After(UpdateState s, UpdateCheckResult r)
    {
        var next = s with { LastCheckAt = r.At };
        if (r.RetryAfter is { } until) return next with { RetryNotBefore = until };
        if (r.Kind == UpdateCheckKind.Error) return next with { ConsecutiveFailures = s.ConsecutiveFailures + 1 };
        return next with { LastSuccessAt = r.At, ConsecutiveFailures = 0, RetryNotBefore = null,
                           LatestKnownVersion = r.Latest?.ToString() ?? s.LatestKnownVersion };
    }
}
