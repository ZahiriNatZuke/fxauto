namespace FxAuto.Core.Updates;

public enum UpdateCheckKind { UpdateAvailable, UpToDate, Error }

/// <summary>Kind of <see cref="UpdateCheckKind.Error"/>, for the UI text (the English <see cref="UpdateCheckResult.Error"/>
/// goes to the log).</summary>
public enum UpdateCheckError { None, RateLimited, Timeout, Network, Http, BadAnswer }

/// <summary>Result of an update check, published to the UI. For <see cref="UpdateCheckKind.UpdateAvailable"/>:
/// <see cref="Latest"/>, <see cref="Notes"/> (release body, Markdown), <see cref="ReleaseUrl"/> and both assets are set.
/// <see cref="RetryAfter"/> is set when GitHub limited the request (403/429).</summary>
public sealed record UpdateCheckResult(UpdateCheckKind Kind, SemVersion Current, DateTimeOffset At)
{
    public SemVersion? Latest { get; init; }
    public string? Title { get; init; }
    public string? Notes { get; init; }
    public string? ReleaseUrl { get; init; }
    public ReleaseAsset? Installer { get; init; }
    public ReleaseAsset? Checksum { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? RetryAfter { get; init; }
    /// <summary>True for the daily automatic check, false for a check the user asked for.</summary>
    public bool Automatic { get; init; }
    public UpdateCheckError ErrorKind { get; init; }
    /// <summary>HTTP status of an <see cref="UpdateCheckError.Http"/> or rate-limit error.</summary>
    public int? HttpStatus { get; init; }
}

/// <summary>Decides what a fetched release means for the running version.</summary>
public static class UpdateDecision
{
    /// <summary>The newest non-draft release with a SemVer tag; pre-releases only when
    /// <paramref name="includePrerelease"/>.</summary>
    public static (ReleaseInfo Release, SemVersion Version)? PickLatest(IEnumerable<ReleaseInfo> releases, bool includePrerelease)
    {
        (ReleaseInfo, SemVersion)? best = null;
        foreach (var r in releases)
        {
            if (r.Draft || !SemVersion.TryParse(r.TagName, out var v)) continue;
            if (!includePrerelease && (r.Prerelease || v.IsPreRelease)) continue;
            if (best is null || v > best.Value.Item2) best = (r, v);
        }
        return best;
    }

    /// <summary>Compares <paramref name="release"/> (null = the repository has no releases) with
    /// <paramref name="current"/>. A newer version without both installer and checksum assets is an error (a release
    /// still being uploaded is retried later).</summary>
    public static UpdateCheckResult Evaluate(SemVersion current, ReleaseInfo? release, bool includePrerelease,
                                             string owner, string repo, DateTimeOffset now)
    {
        if (release is null) return new(UpdateCheckKind.UpToDate, current, now);
        if (!SemVersion.TryParse(release.TagName, out var latest))
            return new(UpdateCheckKind.Error, current, now) { Error = $"The release tag \"{release.TagName}\" is not a version." };
        if (release.Draft || (!includePrerelease && (release.Prerelease || latest.IsPreRelease)) || latest <= current)
            return new(UpdateCheckKind.UpToDate, current, now) { Latest = latest };

        var (installer, checksum) = UpdateAssets.Select(release, latest, owner, repo);
        if (installer is null || checksum is null)
            return new(UpdateCheckKind.Error, current, now)
            {
                Latest = latest,
                ReleaseUrl = release.HtmlUrl,
                Error = installer is null ? $"Release {release.TagName} has no installer." : $"Release {release.TagName} has no checksum file.",
            };
        return new(UpdateCheckKind.UpdateAvailable, current, now)
        {
            Latest = latest,
            Title = string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
            Notes = release.Body,
            ReleaseUrl = release.HtmlUrl,
            Installer = installer,
            Checksum = checksum,
        };
    }

    /// <summary>When a 403/429 from the GitHub API means "rate limited", the instant to retry after; null when the
    /// status is not a rate limit. Uses <c>Retry-After</c> (seconds), then <c>x-ratelimit-reset</c> (epoch seconds)
    /// when <c>x-ratelimit-remaining</c> is 0, else one hour. Capped at 24 h.</summary>
    public static DateTimeOffset? RateLimitedUntil(int status, string? retryAfter, string? remaining, string? reset,
                                                   DateTimeOffset now)
    {
        if (status is not (403 or 429)) return null;
        DateTimeOffset until = now.AddHours(1);
        if (long.TryParse(retryAfter, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var secs))
            until = now.AddSeconds(Math.Max(secs, 1));
        else if (remaining?.Trim() == "0" &&
                 long.TryParse(reset, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var epoch))
            until = DateTimeOffset.FromUnixTimeSeconds(epoch) is var r && r > now ? r.AddSeconds(5) : now.AddMinutes(1);
        var max = now.AddHours(24);
        return until > max ? max : until;
    }
}
