namespace FxAuto.Core.Updates;

/// <summary>What the tray says about updates: the one balloon per version and the "Update to X…" menu entry.</summary>
public static class UpdateNotice
{
    /// <summary>True when an automatic check found a version that has not been announced yet
    /// (<paramref name="notifiedVersion"/> = <see cref="UpdateState.NotifiedVersion"/>). Manual checks never show a
    /// balloon: the user is already looking at the result in About.</summary>
    public static bool ShouldAnnounce(UpdateCheckResult result, string? notifiedVersion)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result is { Automatic: true, Kind: UpdateCheckKind.UpdateAvailable, Latest: { } v }
               && !(SemVersion.TryParse(notifiedVersion, out var seen) && seen == v);
    }

    /// <summary>The version waiting to be installed, or null. The last check of this run decides; before any check
    /// (just started), the latest version an earlier run saw counts when it is newer than the running one and allowed
    /// by <paramref name="includePrereleases"/>. An error result keeps what was known before it.</summary>
    public static SemVersion? Pending(SemVersion current, UpdateCheckResult? last, string? latestKnownVersion, bool includePrereleases)
    {
        ArgumentNullException.ThrowIfNull(current);
        switch (last?.Kind)
        {
            case UpdateCheckKind.UpdateAvailable: return last.Latest;
            case UpdateCheckKind.UpToDate: return null;
        }
        if (!SemVersion.TryParse(latestKnownVersion, out var known)) return null;
        if (known.IsPreRelease && !includePrereleases) return null;
        return known > current ? known : null;
    }

    /// <summary>Release notes (GitHub Markdown) as plain text for a read-only box: headings lose their #, list
    /// bullets become "•", bold/code marks and link targets are dropped, line ends become CRLF. Never null.</summary>
    public static string PlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var sb = new System.Text.StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var body = line.TrimStart();
            var indent = line.Length - body.Length;
            if (body.StartsWith('#')) body = body.TrimStart('#').TrimStart();
            else if (body.StartsWith("- ", StringComparison.Ordinal) || body.StartsWith("* ", StringComparison.Ordinal) || body.StartsWith("+ ", StringComparison.Ordinal))
                body = new string(' ', indent) + "• " + body[2..].TrimStart();
            body = body.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
            body = System.Text.RegularExpressions.Regex.Replace(body, @"\[([^\]]*)\]\([^)]*\)", "$1", System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                                                                TimeSpan.FromMilliseconds(200));
            sb.Append(body).Append("\r\n");
        }
        return sb.ToString().Trim('\r', '\n');
    }

    /// <summary>One-time move of the update settings from update-state.json to config.json: when the config has no
    /// <c>general.updates</c> and the state file still holds settings written by an older build, returns them to be
    /// saved in the config; otherwise null (the config, or the defaults, already rule).</summary>
    public static Config.UpdatePreferences? SettingsToMigrate(Config.UpdatePreferences? configured, UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (configured is not null || !state.SettingsInFile) return null;
        return new Config.UpdatePreferences(state.CheckAutomatically, state.IncludePrereleases);
    }
}
