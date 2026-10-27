namespace FxAuto.Core.FxSound;

/// <summary>Growing wait while FxSound is open but does not respond: 60 s → 120 s → 300 s (cap).
/// Reset on the first success. Not thread-safe: only the coordinator uses it.</summary>
public sealed class UnresponsiveBackoff
{
    private static readonly TimeSpan[] Steps =
        [TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300)];

    /// <summary>Consecutive failures with FxSound not responding.</summary>
    public int Failures { get; private set; }

    /// <summary>Records a failure and returns how long to wait before trying again.</summary>
    public TimeSpan OnFailure()
    {
        Failures++;
        return Steps[Math.Min(Failures, Steps.Length) - 1];
    }

    /// <summary>FxSound answered: back to normal.</summary>
    public void OnSuccess() => Failures = 0;
}
