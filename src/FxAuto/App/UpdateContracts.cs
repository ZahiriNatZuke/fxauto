// Update engine contract between the app (UpdateService) and the settings window (About). Its own file so the test
// project and tools/settings-preview can link it without the rest of the layer contract.

namespace FxAuto.App;

/// <summary>Progress of an installer download (<see cref="TotalBytes"/> 0 = unknown).</summary>
internal sealed record DownloadProgress(long BytesReceived, long TotalBytes);

/// <summary>Why a download did not give a verified installer (<see cref="None"/> = it did).</summary>
internal enum DownloadFailure { None, NoUpdate, Untrusted, NoChecksum, Mismatch, Failed, Cancelled }

/// <summary>Outcome of <see cref="IUpdateController.DownloadAndVerifyAsync"/>: on success <see cref="InstallerPath"/>
/// is the verified installer and <see cref="Sha256"/> its hash; otherwise <see cref="Error"/> says why in English
/// (log) and <see cref="Failure"/> classifies it for the UI (the file is gone).</summary>
internal sealed record DownloadResult(bool Ok, Core.Updates.SemVersion? Version, string? InstallerPath, string? Sha256,
                                      string? Error, bool Cancelled = false)
{
    public DownloadFailure Failure { get; init; } = Cancelled ? DownloadFailure.Cancelled : Ok ? DownloadFailure.None : DownloadFailure.Failed;
}

/// <summary>The update engine as the settings window sees it (implemented by UpdateService; a fake in the
/// settings preview). <see cref="CheckCompleted"/> is raised on a pool thread: subscribers marshal to the UI.</summary>
internal interface IUpdateController
{
    Core.Updates.SemVersion Current { get; }
    Core.Updates.UpdateCheckResult? LastResult { get; }
    /// <summary>When the last check (automatic or manual, any result) ran; null = never.</summary>
    DateTimeOffset? LastCheckAt { get; }
    /// <summary>Version waiting to be installed (see UpdateNotice.Pending), or null.</summary>
    Core.Updates.SemVersion? PendingVersion { get; }
    event Action<Core.Updates.UpdateCheckResult>? CheckCompleted;
    Task<Core.Updates.UpdateCheckResult> CheckAsync(CancellationToken ct = default);
    Task<DownloadResult> DownloadAndVerifyAsync(Core.Updates.UpdateCheckResult update, IProgress<DownloadProgress>? progress = null,
                                                CancellationToken ct = default);
    /// <summary>Starts the verified installer and exits FxAuto; false = nothing was started.</summary>
    bool LaunchInstallerAndExit(DownloadResult download);
}
