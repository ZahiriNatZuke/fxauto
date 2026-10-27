using FxAuto.Core.Arbitration;
using FxAuto.Core.Audio;
using FxAuto.Core.Classification;
using C = FxAuto.Core.Classification.Categories;

namespace FxAuto.Core.Config;

/// <summary>Built-in default config: taxonomy implicit, built-in sources
/// (browsers, sites by title segment, players, calls, a set of known games and the generic Steam folder),
/// no devices, no assignments (the first start detects devices and proposes them).</summary>
public static class DefaultConfig
{
    /// <summary>Origin values of <see cref="SourceConfig.Origin"/>.</summary>
    // "migrated" is still accepted (configs written by earlier builds use it); FxAuto never writes it for new sources.
    public const string OriginBuiltIn = "builtIn", OriginUser = "user", OriginMigrated = "migrated";

    /// <summary>Default FxSound path.</summary>
    public const string FxSoundPath = @"C:\Program Files\FxSound LLC\FxSound\FxSound.exe";

    /// <summary>Accessible-name markers of an audible browser tab (spike 4c: Chrome in Spanish and English).</summary>
    public static readonly IReadOnlyList<string> AudibleTabMarkers = ["reproducción de audio", "Audio playing"];

    /// <summary>Sports keywords of the "any site" sports source. Each entry is a regex
    /// alternative; the whole group is wrapped in <c>\b(?:…)\b</c>.</summary>
    internal static readonly IReadOnlyList<string> SportsKeywords =
    [
        "F[úu]tbol", "Cl[áa]sico", "LaLiga", "La Liga", "Champions League", "Champions", "Premier League", "Serie A",
        "Bundesliga", "Libertadores", "Sudamericana", "Copa del Rey", @"Jornada \d+", "Partido", "NBA", "NFL", "F1",
    ];

    /// <summary>The " vs " alternative of the sports regex (already bounded by its spaces).</summary>
    internal const string VersusAlternative = @" vs\.? ";

    /// <summary><c>\b(?:F[úu]tbol|…|F1)\b| vs\.? </c>, matched case-insensitively by the classifier.</summary>
    public static readonly string SportsRegex = @"\b(?:" + string.Join('|', SportsKeywords) + @")\b|" + VersusAlternative;

    /// <summary>Executables of the built-in browser sources.</summary>
    public static readonly IReadOnlyList<string> BrowserExes = ["chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe"];

    /// <summary>A new default config. Every call returns new instances.</summary>
    public static FxAutoConfig Create() => new(
        SchemaVersion: ConfigJson.CurrentSchemaVersion,
        General: CreateGeneral(),
        Presets: new Dictionary<string, PresetMeta>(StringComparer.Ordinal),
        Devices: [],
        Sources: CreateSources(),
        Assignments: [],
        IgnoredApps: [.. IgnoredApps],
        Launchers: [.. Launchers],
        GameFolderExclusions: [.. GameFolderExclusions]);

    /// <summary>Default general settings (REQ-112: StartFxSoundIfClosed false; privacy: LogTitles false).</summary>
    public static GeneralSettings CreateGeneral() => new(
        FxSoundPath: FxSoundPath,
        StartWithWindows: false,
        StartFxSoundIfClosed: false,
        LogLevel: "info",
        LogTitles: false,
        StartupGraceSeconds: 60,
        CheckIntervalSeconds: 60,
        Detection: new SoundingTimings(),
        IdleGraceSeconds: 60,
        MinDwellMs: 5000,
        GameLoadingMaxMs: 60000,
        CallPolicy: CallPolicy.Ignore,
        UnknownDevice: "keepCurrent",
        ReadBrowserTabs: true,
        AudibleTabMarkers: [.. AudibleTabMarkers]);

    internal static readonly IReadOnlyList<string> IgnoredApps =
    [
        "LockApp.exe", "FxSound.exe", "FxAuto.exe", "GameBar.exe", "GameBarFTServer.exe", "GameBarPresenceWriter.exe",
        "NVIDIA Overlay.exe", "NVIDIA Share.exe", "GameOverlayUI.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe", "ShellHost.exe", "TextInputHost.exe",
        "ScreenClippingHost.exe", "ApplicationFrameHost.exe",
    ];

    internal static readonly IReadOnlyList<string> Launchers =
    [
        "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "EpicWebHelper.exe", "Battle.net.exe",
        "RiotClientServices.exe", "EADesktop.exe", "UbisoftConnect.exe", "upc.exe", "GalaxyClient.exe", "PlayGTAV.exe",
        "REDprelauncher.exe", "REDlauncher.exe", "u4-l.exe", "tll-l.exe", "WardogsLauncher-Shipping.exe",
    ];

    internal static readonly IReadOnlyList<string> GameFolderExclusions =
    [
        "_CommonRedist", "Steamworks Shared", "Steam Controller Configs", "Wallpaper Engine", "Lossless Scaling", "SteamVR",
        "UnityCrashHandler*", "CrashReport*", "crashpad_handler.exe", "crs-handler.exe", "EpicWebHelper.exe",
        "*Launcher*.exe", "*prelauncher*.exe", "vconsole2.exe", "D3D12StateObjectCompiler.exe", "d3dconfig.exe",
    ];

    private static List<SourceConfig> CreateSources() =>
    [
        // Browsers: default category when no site matches (REQ-103).
        Browser("chrome", "Google Chrome", "chrome.exe"),
        Browser("edge", "Microsoft Edge", "msedge.exe"),
        Browser("firefox", "Mozilla Firefox", "firefox.exe"),
        Browser("brave", "Brave", "brave.exe"),
        Browser("opera", "Opera", "opera.exe"),

        // Sites, by whole title segment. More specific (site + regex, "*" + regex) wins over a plain site.
        Site("youtube-music", "YouTube Music", C.Music, "YouTube Music"),
        Site("spotify-web", "Spotify (web)", C.Music, "Spotify"),
        Site("soundcloud", "SoundCloud", C.Music, "SoundCloud"),
        Site("apple-music", "Apple Music", C.Music, "Apple Music"),
        Site("deezer", "Deezer", C.Music, "Deezer"),
        Site("tidal-web", "TIDAL (web)", C.Music, "TIDAL"),
        Site("bandcamp", "Bandcamp", C.Music, "Bandcamp"),
        Site("youtube", "YouTube", C.Video, "YouTube"),
        Site("vimeo", "Vimeo", C.Video, "Vimeo"),
        Site("tiktok", "TikTok", C.Video, "TikTok"),
        Site("twitch", "Twitch", C.LiveStream, "Twitch"),
        Site("kick", "Kick", C.LiveStream, "Kick"),
        Site("netflix", "Netflix", C.MovieOrSeries, "Netflix"),
        Site("prime-video", "Prime Video", C.MovieOrSeries, "Prime Video"),
        Site("disney-plus", "Disney+", C.MovieOrSeries, "Disney+"),
        Site("max", "Max", C.MovieOrSeries, "Max", "HBO Max"),
        Site("crunchyroll", "Crunchyroll", C.MovieOrSeries, "Crunchyroll"),
        Site("apple-tv", "Apple TV", C.MovieOrSeries, "Apple TV", "Apple TV+"),
        Site("paramount-plus", "Paramount+", C.MovieOrSeries, "Paramount+"),
        Site("plex", "Plex", C.MovieOrSeries, "Plex"),
        Site("dazn", "DAZN", C.Sports, "DAZN"),
        Site("sports-streaming", "Sports streaming", C.Sports,
            "ESPN", "ESPN+", "fuboTV", "NBA League Pass", "MLB.TV", "NFL+", "FIFA+"),
        SiteRegex("sports-keywords", "Sports matches (any site)", C.Sports, SportsRegex, "*"),
        Site("google-meet", "Google Meet", C.Call, "Google Meet", "Meet"),
        Site("teams-web", "Microsoft Teams (web)", C.Call, "Microsoft Teams"),
        Site("discord-web", "Discord (web)", C.Call, "Discord"),
        Site("whatsapp-web", "WhatsApp (web)", C.Call, "WhatsApp"),
        Site("zoom-web", "Zoom (web)", C.Call, "Zoom"),

        // Apps.
        App("spotify", "Spotify", C.Music, ["Spotify.exe"], ["SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"]),
        App("music-players", "Music players", C.Music, ["foobar2000.exe", "MusicBee.exe", "AIMP.exe", "TIDAL.exe", "AppleMusic.exe"]),
        App("media-player-music", "Media Player (Store, music)", C.Music, [], ["Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"]),
        App("vlc", "VLC", C.MovieOrSeries, ["vlc.exe"]),
        App("mpc", "MPC-HC / MPC-BE", C.MovieOrSeries, ["mpc-hc64.exe", "mpc-hc.exe", "mpc-be64.exe", "mpc-be.exe"]),
        App("potplayer", "PotPlayer", C.MovieOrSeries, ["PotPlayerMini64.exe", "PotPlayerMini.exe"]),
        App("mpv", "mpv", C.MovieOrSeries, ["mpv.exe"]),
        App("windows-media-player", "Windows Media Player", C.MovieOrSeries,
            ["Microsoft.Media.Player.exe", "wmplayer.exe", "Video.UI.exe"],
            ["Microsoft.ZuneVideo_8wekyb3d8bbwe!Microsoft.ZuneVideo"]),
        App("movie-apps", "Movie apps (Store)", C.MovieOrSeries, [],
            ["4DF9E0F8.Netflix_mcm4njqhnhss8!Netflix.App", "Disney.37853FC22B2CE_6rarf9sa4v8jt!App",
             "AmazonVideo.PrimeVideo_pwbj9vvecjh7j!App"]),
        App("discord", "Discord", C.Call, ["Discord.exe"]),
        App("teams", "Microsoft Teams", C.Call, ["Teams.exe", "ms-teams.exe"]),
        App("zoom", "Zoom", C.Call, ["Zoom.exe"]),
        App("whatsapp", "WhatsApp", C.Call, ["WhatsApp.exe"], ["5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App"]),

        // Known games.
        Game("pubg", "PUBG", C.GameFps, ["TslGame.exe"]),
        Game("portal-2", "Portal 2", C.GameFps, ["portal2.exe"]),
        Game("portal", "Portal", C.GameFps, ["hl2.exe"], [@"\steamapps\common\Portal\"]),
        Game("wardogs", "Wardogs", C.GameFps, ["WardogsClient-Win64-Shipping.exe"]),
        Game("arc-raiders", "Arc Raiders", C.GameTps, ["PioneerGame.exe"]),
        Game("god-of-war-ragnarok", "God of War Ragnarok", C.GameAction, ["GoWR.exe"]),
        Game("gta-v", "GTA V", C.GameAction, ["GTA5.exe", "GTA5_Enhanced.exe"]),
        Game("it-takes-two", "It Takes Two", C.GameAction, ["ItTakesTwo.exe"]),
        Game("rise-of-the-tomb-raider", "Rise of the Tomb Raider", C.GameAction, ["ROTTR.exe"]),
        Game("uncharted-legacy-of-thieves", "Uncharted Legacy of Thieves", C.GameAction, ["u4.exe", "tll.exe"]),
        Game("unravel", "Unravel", C.GameAction, ["Unravel.exe"]),
        Game("the-witcher-3", "The Witcher 3", C.GameRpg, ["witcher3.exe"]),
        Game("valheim", "Valheim", C.GameRpg, ["valheim.exe"]),
        Game("dispatch", "Dispatch", C.GameRpg, ["Dispatch-Win64-Shipping.exe"]),
        Game("project-zomboid", "Project Zomboid", C.GameHorror, ["ProjectZomboid64.exe", "ProjectZomboid32.exe"]),
        Game("reanimal", "REANIMAL", C.GameHorror, ["REANIMAL.exe"]),
        Game("euro-truck-simulator-2", "Euro Truck Simulator 2", C.GameRacing, ["eurotrucks2.exe"]),
        Game("satisfactory", "Satisfactory", C.GameStrategy, ["FactoryGameSteam-Win64-Shipping.exe", "FactoryGameSteam.exe"]),
        Game("governor-of-poker-3", "Governor of Poker 3", C.GameStrategy, ["GOP.exe"]),
        Game("dota-2", "Dota 2", C.GameMoba, ["dota2.exe"]),
        // Safety net for any other Steam game (loses to a specific source).
        Game("steam-generic", "Any Steam game", C.GameAction, [], [@"\steamapps\common\"], fallback: true),
    ];

    private static SourceMatch Match(IReadOnlyList<string>? exe = null, IReadOnlyList<string>? aumid = null,
        IReadOnlyList<string>? folder = null, IReadOnlyList<string>? site = null, string? regex = null)
        => new(exe ?? [], aumid ?? [], folder ?? [], site ?? [], regex);

    private static SourceConfig Browser(string id, string name, string exe)
        => new(id, name, SourceKind.Browser, Match(exe: [exe]), C.Video, false, OriginBuiltIn);

    private static SourceConfig Site(string id, string name, string category, params string[] sites)
        => new(id, name, SourceKind.Site, Match(site: sites), category, false, OriginBuiltIn);

    private static SourceConfig SiteRegex(string id, string name, string category, string regex, params string[] sites)
        => new(id, name, SourceKind.Site, Match(site: sites, regex: regex), category, false, OriginBuiltIn);

    private static SourceConfig App(string id, string name, string category, string[] exe, string[]? aumid = null)
        => new(id, name, SourceKind.App, Match(exe: exe, aumid: aumid), category, false, OriginBuiltIn);

    private static SourceConfig Game(string id, string name, string category, string[] exe, string[]? folder = null,
        bool fallback = false)
        => new(id, name, SourceKind.Game, Match(exe: exe, folder: folder), category, fallback, OriginBuiltIn);
}
