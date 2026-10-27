namespace FxAuto.Core.FxSound;

// Layer contract (plan.md "Layer contract", frozen in task 3).

/// <summary>Useful content of FxSound's status.json.</summary>
/// <remarks>The newer fields are init properties (not positional) so the short constructor keeps working;
/// FxStatusParser fills them.</remarks>
public sealed record FxStatus(string? Version, string? SelectedPreset, bool Power, bool? SelectedModified,
    double? VolumeLeveling, string? SelectedOutput, IReadOnlyList<string> UserPresets,
    IReadOnlyList<string> BuiltInPresets, DateTimeOffset FileWrittenAt)
{
    /// <summary>status.json <c>output_devices[]</c>: the exact names <c>--output</c> accepts.</summary>
    public IReadOnlyList<string> OutputDevices { get; init; } = [];
    /// <summary>Preset name → <c>modified</c> flag, for every listed preset.</summary>
    public IReadOnlyDictionary<string, bool> ModifiedByPreset { get; init; } = new Dictionary<string, bool>();
    /// <summary>Effects and EQ bands of the active preset, to compare a copy with its source. null = not read.</summary>
    public FxDsp? Dsp { get; init; }
}

/// <summary>DSP values read from status.json: effect name → value, and EQ band gains in order.</summary>
public sealed record FxDsp(IReadOnlyDictionary<string, double> Effects, IReadOnlyList<double> Bands)
{
    /// <summary>equalizer.master_gain (null = not present). Added in task 3c.</summary>
    public double? MasterGain { get; init; }
}

/// <summary>Exclusive FxSound session handed out by <c>FxSoundController.RunExclusiveAsync</c>: no other command
/// (engine preset, watchdog, query) runs while the body holds it.</summary>
public interface IFxSession
{
    /// <summary>Sends one command (built with FxCommand). null = timeout reached; the process is never killed.</summary>
    Task<int?> SendAsync(string args, CancellationToken ct);
    /// <summary>--status and wait for a fresh status.json. null if FxSound did not answer.</summary>
    Task<FxStatus?> QueryAsync(CancellationToken ct);
}

/// <summary>Access to FxSound. Implemented by the Windows layer; tests use a fake one.</summary>
public interface IFxSoundPort
{
    /// <summary>Main FxSound instance is open (excludes PIDs launched by FxAuto).</summary>
    bool IsMainRunning();
    /// <summary>null if it cannot be determined.</summary>
    bool? IsElevated();
    /// <summary>Starts FxSound with "--run_minimized" only, normal window.</summary>
    Task<bool> StartMainMinimizedAsync(CancellationToken ct);
    /// <summary>Launches FxSound.exe with these arguments and waits for it to exit. null = timeout reached; never kills.</summary>
    Task<int?> SendAsync(string arguments, TimeSpan timeout, CancellationToken ct);
    /// <summary>Content and write time of status.json (null if it does not exist or cannot be read).</summary>
    (string? Json, DateTimeOffset? WrittenAt) ReadStatusFile();
    /// <summary>A process launched by FxAuto has not exited yet.</summary>
    bool HasPendingLaunched { get; }
}

/// <summary>Result of applying/verifying a preset.</summary>
public sealed record ApplyResult(bool Ok, string Expected, string? Actual, string? FailureReason,
                                 int Attempts, long Ms);
