using FxAuto.Core.Localization;
namespace FxAuto.Core.Classification;

/// <summary>Fixed category taxonomy of REQ-102. The ids are part of the contract.</summary>
public static class Categories
{
    public const string Music = "music", MusicVideo = "music-video", Podcast = "podcast", Video = "video",
        LiveStream = "live-stream", Sports = "sports", MovieOrSeries = "movie-or-series", Call = "call",
        GameFps = "game-fps", GameTps = "game-tps", GameRacing = "game-racing", GameSports = "game-sports",
        GameStrategy = "game-strategy", GameRpg = "game-rpg", GameAction = "game-action",
        GameHorror = "game-horror", GameMoba = "game-moba", Unknown = "unknown", Hold = "hold";

    // id, display name, preset label (used in preset names: "G435 - Sports TV", etc.).
    private static readonly (string Id, string Display, string Label)[] Table =
    [
        (Music, "Music", "Music"),
        (MusicVideo, "Music video", "Music Video"),
        (Podcast, "Podcast", "Podcast"),
        (Video, "Video", "Video"),
        (LiveStream, "Live stream", "Live Stream"),
        (Sports, "Sports", "Sports TV"),
        (MovieOrSeries, "Movie or series", "Movie"),
        (Call, "Call", "Voice"),
        (GameFps, "Game: FPS", "FPS"),
        (GameTps, "Game: TPS", "TPS"),
        (GameRacing, "Game: Racing", "Racing"),
        (GameSports, "Game: Sports", "Sports Game"),
        (GameStrategy, "Game: Strategy", "Strategy"),
        (GameRpg, "Game: RPG", "RPG"),
        (GameAction, "Game: Action", "Action"),
        (GameHorror, "Game: Horror", "Horror"),
        (GameMoba, "Game: MOBA", "MOBA"),
        (Unknown, "Unknown", "General"),
        (Hold, "Hold (no change)", "Hold"),
    ];

    private static readonly string[] AllIds = Table.Select(t => t.Id).ToArray();

    /// <summary>Every category id, in display order.</summary>
    public static IReadOnlyList<string> All => AllIds;

    /// <summary>True when <paramref name="id"/> is one of the known ids (case-sensitive, ids are lowercase).</summary>
    public static bool IsKnown(string? id) => id is not null && Array.IndexOf(AllIds, id) >= 0;

    /// <summary>"game-*".</summary>
    public static bool IsGame(string id) => id is not null && id.StartsWith("game-", StringComparison.Ordinal);

    /// <summary>"Game: FPS", "Music"… in the UI language (catalog key "Category." + id); unknown ids are returned
    /// unchanged. The English name is the Display column.</summary>
    public static string DisplayName(string id) => IsKnown(id) ? Loc.T("Category." + id) : id;

    /// <summary>Label used in preset names: "FPS", "Music", "Sports TV"…; unknown ids are returned unchanged.</summary>
    public static string PresetLabel(string id)
    {
        foreach (var t in Table) if (t.Id == id) return t.Label;
        return id;
    }

    /// <summary>Background tie-break rank (REQ-104): movie-or-series/sports &gt; video/live-stream/music-video &gt;
    /// music/podcast &gt; unknown.</summary>
    internal static int BackgroundRank(string id) => id switch
    {
        MovieOrSeries or Sports => 3,
        Video or LiveStream or MusicVideo => 2,
        Unknown => 0,
        _ => 1,
    };
}
