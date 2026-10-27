using FxAuto.Core.Arbitration;
using FxAuto.Core.Audio;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;

namespace FxAuto.Tests;

/// <summary>Config built directly for the 3b tests (independent of DefaultConfig, task 3a).</summary>
internal static class ClassTestConfig
{
    public static readonly string[] Markers = ["reproducción de audio", "Audio playing"];

    public static SourceConfig Src(string id, SourceKind kind, string category, string[]? exe = null,
        string[]? aumid = null, string[]? folder = null, string[]? site = null, string? regex = null, bool fallback = false)
        => new(id, id, kind, new SourceMatch(exe ?? [], aumid ?? [], folder ?? [], site ?? [], regex), category, fallback, "builtIn");

    public static GeneralSettings General(int idleGraceSeconds = 60, int minDwellMs = 5000,
        CallPolicy callPolicy = CallPolicy.Ignore)
        => new(null, false, false, "info", false, 10, 60, new SoundingTimings(), idleGraceSeconds, minDwellMs, 60000,
            callPolicy, "keepCurrent", true, Markers);

    public static FxAutoConfig Create(IReadOnlyList<SourceConfig>? extra = null)
    {
        var sources = new List<SourceConfig>
        {
            Src("cs2", SourceKind.Game, Categories.GameFps, exe: ["cs2.exe"]),
            Src("witcher3", SourceKind.Game, Categories.GameRpg, folder: [@"\steamapps\common\The Witcher 3\"]),
            Src("steam-games", SourceKind.Game, Categories.GameAction, folder: [@"\steamapps\common\"], fallback: true),
            Src("spotify", SourceKind.App, Categories.Music, exe: ["Spotify.exe"]),
            Src("spotify-store", SourceKind.App, Categories.Podcast, aumid: ["SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"]),
            Src("discord", SourceKind.App, Categories.Call, exe: ["Discord.exe"]),
            Src("obs", SourceKind.App, Categories.Hold, exe: ["obs64.exe"]),
            Src("vlc", SourceKind.App, Categories.Video, exe: ["vlc*.exe"]),
            Src("browsers", SourceKind.Browser, Categories.Video, exe: ["chrome.exe", "msedge.exe", "firefox.exe"]),
            Src("youtube", SourceKind.Site, Categories.Video, site: ["YouTube"]),
            Src("youtube-music", SourceKind.Site, Categories.Music, site: [" - YouTube Music"]),
            Src("spotify-web", SourceKind.Site, Categories.Music, site: ["Spotify"]),
            Src("netflix", SourceKind.Site, Categories.MovieOrSeries, site: ["Netflix"]),
            Src("sports-youtube", SourceKind.Site, Categories.Sports, site: ["YouTube"],
                regex: @"\b(cl[aá]sico|laliga|la liga)\b|\svs\.?\s"),
            Src("dazn", SourceKind.Site, Categories.Sports, site: ["*"], regex: @"\bDAZN\b"),
            Src("broken", SourceKind.Site, Categories.Podcast, site: ["*"], regex: @"(unclosed"),
        };
        if (extra is not null) sources.AddRange(extra);
        return new FxAutoConfig(2, General(), new Dictionary<string, PresetMeta>(), [], sources, [], [], ["steam.exe"],
            ["_CommonRedist", "UnityCrashHandler*"]);
    }

    public static SourceContext Browser(string? title, string exe = "chrome.exe", IReadOnlyList<BrowserTab>? tabs = null)
        => new(100, exe, @"C:\Program Files\Google\Chrome\Application\" + exe, null, title, tabs);

    public static SourceContext App(string exe, string? path = null, string? aumid = null, string? title = null)
        => new(200, exe, path ?? @"C:\Apps\" + exe, aumid, title, null);
}

internal static class ClassifierTests
{
    private const string Jijantes =
        "JIJANTES | REACCIÓN AL CLÁSICO DE LA LIGA. BARÇA - MADRID JORNADA 10 EN EL SPOTIFY CAMP NOU - YouTube - Google Chrome";

    public static void Register()
    {
        // ---------- Categories ----------
        Program.Add("Categories: taxonomy ids are complete and unique", () =>
        {
            Assert.Equal(19, Categories.All.Count, "count");
            Assert.Equal(19, Categories.All.Distinct().Count(), "distinct");
            foreach (var id in new[] { "music", "music-video", "podcast", "video", "live-stream", "sports", "movie-or-series",
                         "call", "game-fps", "game-tps", "game-racing", "game-sports", "game-strategy", "game-rpg",
                         "game-action", "game-horror", "game-moba", "unknown", "hold" })
                Assert.True(Categories.All.Contains(id), "missing " + id);
            Assert.Equal(9, Categories.All.Count(Categories.IsGame), "games");
            Assert.True(!Categories.IsGame(Categories.Sports), "sports is not a game");
        });

        Program.Add("Categories: display names and preset labels", () =>
        {
            Assert.Equal("Game: FPS", Categories.DisplayName(Categories.GameFps), "display");
            Assert.Equal("Music", Categories.DisplayName(Categories.Music), "display music");
            Assert.Equal("FPS", Categories.PresetLabel(Categories.GameFps), "label fps");
            Assert.Equal("Sports TV", Categories.PresetLabel(Categories.Sports), "label sports");
            Assert.Equal("Sports Game", Categories.PresetLabel(Categories.GameSports), "label game-sports");
            Assert.Equal("Movie", Categories.PresetLabel(Categories.MovieOrSeries), "label movie");
            Assert.Equal("Voice", Categories.PresetLabel(Categories.Call), "label call");
            Assert.True(Categories.All.All(id => Categories.PresetLabel(id).Length > 0 && Categories.DisplayName(id).Length > 0), "all labelled");
        });

        // ---------- Title segments ----------
        Program.Add("TitleSegments: split by every separator", () =>
        {
            var s = TitleSegments.Split("a - b – c — d | e • f · g");
            Assert.Equal("a,b,c,d,e,f,g", string.Join(',', s), "segments");
            Assert.Equal("Clásico-Final", TitleSegments.Split("Clásico-Final")[0], "hyphen without spaces is not a separator");
        });

        Program.Add("TitleSegments: browser suffix stripped (Chrome, Edge with zero-width space, Firefox)", () =>
        {
            Assert.Equal("Song - YouTube Music", TitleSegments.StripBrowserSuffix("Song - YouTube Music - Google Chrome"), "chrome");
            Assert.Equal("Video - YouTube - Personal", TitleSegments.StripBrowserSuffix("Video - YouTube - Personal - Microsoft\u200B Edge"), "edge");
            Assert.Equal("Video - YouTube", TitleSegments.StripBrowserSuffix("Video - YouTube — Mozilla Firefox"), "firefox");
            Assert.True(TitleSegments.HasSegment("Video - YouTube - Personal - Microsoft\u200B Edge", "YouTube"), "edge segment");
        });

        Program.Add("TitleSegments: UIA tab name cleanup and audible markers (Chrome es-ES format)", () =>
        {
            var markers = ClassTestConfig.Markers;
            var playing = "Uso de memoria de Song - YouTube Music: reproducción de audio: 629 MB";
            var muted = "Uso de memoria de Otra cosa - YouTube: 116 MB";
            Assert.True(TitleSegments.IsAudibleTabName(playing, markers), "playing audible");
            Assert.True(!TitleSegments.IsAudibleTabName(muted, markers), "muted not audible");
            Assert.True(TitleSegments.IsAudibleTabName("Song - YouTube - Audio playing", markers), "english marker");
            Assert.Equal("Uso de memoria de Song - YouTube Music", TitleSegments.CleanTabName(playing, markers), "clean playing");
            Assert.Equal("Uso de memoria de Otra cosa - YouTube", TitleSegments.CleanTabName(muted, markers), "clean muted");
            Assert.True(TitleSegments.HasSegment(TitleSegments.CleanTabName(playing, markers), "YouTube Music"), "site segment after clean");
        });

        // ---------- Classifier: title segments ----------
        Program.Add("Classifier: 'SPOTIFY CAMP NOU' clásico title → sports, never music", () =>
        {
            var c = new SourceClassifier(ClassTestConfig.Create()).Classify(ClassTestConfig.Browser(Jijantes));
            Assert.Equal(Categories.Sports, c.Category, "category");
            Assert.Equal("sports-youtube", c.SourceId, "source");
            Assert.True(c.IsBrowser && !c.IsGame, "flags");
        });

        Program.Add("Classifier: 'FC Barcelona at SPOTIFY CAMP NOU - YouTube' → video, not music", () =>
        {
            var c = new SourceClassifier(ClassTestConfig.Create()).Classify(ClassTestConfig.Browser("FC Barcelona at SPOTIFY CAMP NOU - YouTube"));
            Assert.Equal(Categories.Video, c.Category, "category");
            Assert.Equal("youtube", c.SourceId, "source");
            Assert.Equal("YouTube", c.Site, "site");
        });

        Program.Add("Classifier: 'Song - YouTube Music - Google Chrome' → music", () =>
        {
            var c = new SourceClassifier(ClassTestConfig.Create()).Classify(ClassTestConfig.Browser("Song - YouTube Music - Google Chrome"));
            Assert.Equal(Categories.Music, c.Category, "category");
            Assert.Equal("youtube-music", c.SourceId, "source");
            Assert.Equal("site segment", c.How, "how");
        });

        Program.Add("Classifier: plain YouTube video → video; '(3)' notification prefix ignored", () =>
        {
            var k = new SourceClassifier(ClassTestConfig.Create());
            Assert.Equal(Categories.Video, k.Classify(ClassTestConfig.Browser("How to bake bread - YouTube - Google Chrome")).Category, "plain");
            Assert.Equal(Categories.Video, k.Classify(ClassTestConfig.Browser("(3) How to bake bread - YouTube - Google Chrome")).Category, "prefix");
            Assert.Equal(Categories.Video, k.Classify(ClassTestConfig.Browser("Music theory explained - YouTube")).Category, "word music in the title");
        });

        Program.Add("Classifier: 'LaLiga' / ' vs ' titles on YouTube → sports; DAZN by '*'+regex", () =>
        {
            var k = new SourceClassifier(ClassTestConfig.Create());
            Assert.Equal(Categories.Sports, k.Classify(ClassTestConfig.Browser("Resumen LaLiga jornada 10 - YouTube - Google Chrome")).Category, "laliga");
            Assert.Equal(Categories.Sports, k.Classify(ClassTestConfig.Browser("Real Madrid vs Barcelona - YouTube - Google Chrome")).Category, "vs");
            var d = k.Classify(ClassTestConfig.Browser("Partido en directo | DAZN - Google Chrome"));
            Assert.Equal(Categories.Sports, d.Category, "dazn");
            Assert.Equal("title regex", d.How, "dazn how");
            Assert.Equal(Categories.Video, k.Classify(ClassTestConfig.Browser("Versus mode tutorial - YouTube")).Category, "versus is not vs");
        });

        Program.Add("Classifier: site needs a whole segment (substring 'Spotify', 'Netflix' never matches)", () =>
        {
            var k = new SourceClassifier(ClassTestConfig.Create());
            Assert.Equal(Categories.Music, k.Classify(ClassTestConfig.Browser("Daily Mix 1 - Spotify - Google Chrome")).Category, "spotify web");
            var x = k.Classify(ClassTestConfig.Browser("Spotify earnings report and Netflix news - Google Chrome"));
            Assert.Equal(Categories.Video, x.Category, "browser default");
            Assert.Equal("browser default", x.How, "how");
            Assert.Equal("browsers", x.SourceId, "source");
            Assert.Equal(Categories.MovieOrSeries, k.Classify(ClassTestConfig.Browser("Stranger Things | Netflix - Microsoft\u200B Edge", "msedge.exe")).Category, "netflix");
        });

        Program.Add("Classifier: browser with no title → browser default", () =>
        {
            var c = new SourceClassifier(ClassTestConfig.Create()).Classify(ClassTestConfig.Browser(null, "firefox.exe"));
            Assert.Equal(Categories.Video, c.Category, "category");
            Assert.True(c.IsBrowser, "browser");
        });

        Program.Add("Classifier: invalid regex never matches and never throws", () =>
        {
            var c = new SourceClassifier(ClassTestConfig.Create()).Classify(ClassTestConfig.Browser("(unclosed - Google Chrome"));
            Assert.Equal(Categories.Video, c.Category, "category");
        });

        Program.Add("Classifier: one audible UIA tab is classified instead of the active title", () =>
        {
            var tabs = new[] { new BrowserTab("Mail - Inbox", false, true), new BrowserTab("Song - YouTube Music", true, false) };
            var c = new SourceClassifier(ClassTestConfig.Create()).Classify(ClassTestConfig.Browser("Mail - Inbox - Google Chrome", tabs: tabs));
            Assert.Equal(Categories.Music, c.Category, "category");
        });

        // ---------- Classifier: games and apps ----------
        Program.Add("Classifier: game by exe → game-fps", () =>
        {
            var c = new SourceClassifier(ClassTestConfig.Create()).Classify(ClassTestConfig.App("cs2.exe", @"D:\Games\cs2\cs2.exe"));
            Assert.Equal(Categories.GameFps, c.Category, "category");
            Assert.True(c.IsGame && !c.IsBrowser, "flags");
            Assert.Equal("game exe", c.How, "how");
        });

        Program.Add("Classifier: game folder, specific beats fallback, fallback for the rest", () =>
        {
            var k = new SourceClassifier(ClassTestConfig.Create());
            var w = k.Classify(ClassTestConfig.App("witcher3.exe", @"D:\SteamLibrary\steamapps\common\The Witcher 3\bin\x64\witcher3.exe"));
            Assert.Equal(Categories.GameRpg, w.Category, "specific");
            Assert.Equal("witcher3", w.SourceId, "specific id");
            var o = k.Classify(ClassTestConfig.App("other.exe", @"D:\SteamLibrary\steamapps\common\Other\other.exe"));
            Assert.Equal(Categories.GameAction, o.Category, "fallback");
            Assert.Equal("game folder", o.How, "how");
        });

        Program.Add("Classifier: game folder exclusions (UnityCrashHandler*, _CommonRedist) are not games", () =>
        {
            var k = new SourceClassifier(ClassTestConfig.Create());
            Assert.Equal(Categories.Unknown, k.Classify(ClassTestConfig.App("UnityCrashHandler64.exe", @"D:\SteamLibrary\steamapps\common\X\UnityCrashHandler64.exe")).Category, "crash handler");
            Assert.Equal(Categories.Unknown, k.Classify(ClassTestConfig.App("vc_redist.exe", @"D:\SteamLibrary\steamapps\common\_CommonRedist\vc_redist.exe")).Category, "redist");
        });

        Program.Add("Classifier: apps by AUMID before exe; exe wildcard; call; hold; unknown", () =>
        {
            var k = new SourceClassifier(ClassTestConfig.Create());
            Assert.Equal(Categories.Podcast, k.Classify(ClassTestConfig.App("Spotify.exe", aumid: "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")).Category, "aumid wins");
            Assert.Equal(Categories.Music, k.Classify(ClassTestConfig.App("spotify.exe")).Category, "exe, case-insensitive");
            Assert.Equal(Categories.Video, k.Classify(ClassTestConfig.App("vlc.exe")).Category, "wildcard");
            Assert.Equal(Categories.Call, k.Classify(ClassTestConfig.App("Discord.exe")).Category, "call");
            Assert.Equal(Categories.Hold, k.Classify(ClassTestConfig.App("obs64.exe")).Category, "hold");
            var u = k.Classify(ClassTestConfig.App("notepad.exe"));
            Assert.Equal(Categories.Unknown, u.Category, "unknown");
            Assert.Null(u.SourceId, "no source");
            Assert.True(!u.IsBrowser && !u.IsGame, "flags");
        });

        Program.Add("Classifier: app source with exe + title regex needs both (AND semantics)", () =>
        {
            var cfg = ClassTestConfig.Create([ClassTestConfig.Src("mpv-live", SourceKind.App, Categories.LiveStream, exe: ["mpv.exe"], regex: @"\blive\b")]);
            var k = new SourceClassifier(cfg);
            Assert.Equal(Categories.LiveStream, k.Classify(ClassTestConfig.App("mpv.exe", title: "Channel live now")).Category, "both");
            Assert.Equal(Categories.Unknown, k.Classify(ClassTestConfig.App("mpv.exe", title: "Holiday video")).Category, "regex fails");
        });

        Program.Add("Classifier: How and Site never contain the window title (privacy)", () =>
        {
            var k = new SourceClassifier(ClassTestConfig.Create());
            var titles = new[]
            {
                Jijantes, "FC Barcelona at SPOTIFY CAMP NOU - YouTube", "Song - YouTube Music - Google Chrome",
                "Private chat with Ana - WhatsApp - Google Chrome", "Partido en directo | DAZN - Google Chrome", "x",
            };
            foreach (var t in titles)
            {
                var c = k.Classify(ClassTestConfig.Browser(t));
                foreach (var part in TitleSegments.Split(TitleSegments.StripBrowserSuffix(t)).Where(p => p.Length > 8 && !string.Equals(p, c.Site, StringComparison.OrdinalIgnoreCase)))
                {
                    Assert.True(!c.How.Contains(part, StringComparison.OrdinalIgnoreCase), "How leaks: " + c.How);
                    Assert.True(c.Site is null || !c.Site.Contains(part, StringComparison.OrdinalIgnoreCase), "Site leaks");
                }
            }
        });

        // ---------- Browser tab tracker (REQ-103) ----------
        Program.Add("Tracker: UIA unavailable → keeps the tab active when the browser started playing", () =>
        {
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            t.OnBrowserTitle(100, "chrome.exe", "Song - YouTube Music - Google Chrome");
            t.OnPlayingChanged(100, true);
            t.OnTabs(100, null);
            t.OnBrowserTitle(100, "chrome.exe", "Mail - Inbox - Google Chrome");   // user switched tab
            var c = t.Current(100, "chrome.exe");
            Assert.Equal(Categories.Music, c.Category, "latched");
            Assert.True(c.How.StartsWith("latched", StringComparison.Ordinal), "how: " + c.How);
        });

        Program.Add("Tracker: YouTube in background while another app is in front → video stays", () =>
        {
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            t.OnBrowserTitle(100, "chrome.exe", "Cooking show - YouTube - Google Chrome");
            t.OnPlayingChanged(100, true);
            clock.Advance(60000);
            Assert.Equal(Categories.Video, t.Current(100, "chrome.exe").Category, "video");
        });

        Program.Add("Tracker: one audible UIA tab overrides the latch", () =>
        {
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            t.OnBrowserTitle(100, "chrome.exe", "Cooking show - YouTube - Google Chrome");
            t.OnPlayingChanged(100, true);
            t.OnTabs(100, [new BrowserTab("Cooking show - YouTube", false, true), new BrowserTab("Daily Mix - Spotify", true, false)]);
            var c = t.Current(100, "chrome.exe");
            Assert.Equal(Categories.Music, c.Category, "tab");
            Assert.True(c.How.StartsWith("audible tab", StringComparison.Ordinal), "how: " + c.How);
        });

        Program.Add("Tracker: several audible tabs → active title, else latched, else first", () =>
        {
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            t.OnBrowserTitle(100, "chrome.exe", "Daily Mix - Spotify - Google Chrome");
            t.OnPlayingChanged(100, true);
            var tabs = new[] { new BrowserTab("Match vs rival - YouTube", true, false), new BrowserTab("Daily Mix - Spotify", true, false),
                               new BrowserTab("Stranger Things | Netflix", true, true) };
            t.OnTabs(100, tabs);
            t.OnBrowserTitle(100, "chrome.exe", "Stranger Things | Netflix - Google Chrome");
            Assert.Equal(Categories.MovieOrSeries, t.Current(100, "chrome.exe").Category, "active title");
            t.OnBrowserTitle(100, "chrome.exe", "Mail - Google Chrome");
            Assert.Equal(Categories.Music, t.Current(100, "chrome.exe").Category, "latched still audible");
            t.OnTabs(100, [tabs[0], tabs[2] with { Selected = false }]);
            Assert.Equal(Categories.Sports, t.Current(100, "chrome.exe").Category, "first audible");
        });

        Program.Add("Tracker: UIA finds no audible tab → latch; latch released when playback stops", () =>
        {
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            t.OnBrowserTitle(100, "chrome.exe", "Stranger Things | Netflix - Google Chrome");
            t.OnPlayingChanged(100, true);
            t.OnTabs(100, [new BrowserTab("Stranger Things | Netflix", false, true)]);
            Assert.Equal(Categories.MovieOrSeries, t.Current(100, "chrome.exe").Category, "latched");
            t.OnPlayingChanged(100, false);
            t.OnBrowserTitle(100, "chrome.exe", "Song - YouTube Music - Google Chrome");
            t.OnPlayingChanged(100, true);
            Assert.Equal(Categories.Music, t.Current(100, "chrome.exe").Category, "new latch");
        });

        Program.Add("Tracker: no title when playback started → browser default", () =>
        {
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            t.OnPlayingChanged(100, true);
            var c = t.Current(100, "msedge.exe");
            Assert.Equal(Categories.Video, c.Category, "default");
            Assert.Equal("browser default", c.How, "how");
        });

        Program.Add("Tracker: not playing → classification of the active tab", () =>
        {
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), new FakeClock());
            t.OnBrowserTitle(100, "chrome.exe", "Song - YouTube Music - Google Chrome");
            Assert.Equal(Categories.Music, t.Current(100, "chrome.exe").Category, "active");
        });

        Program.Add("Tracker: UIA read cadence (on start, follow-up after 4 s, failed read retried after 15 s, never when silent)", () =>
        {
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            Assert.True(!t.NeedsTabRead(100), "unknown pid");
            t.OnBrowserTitle(100, "chrome.exe", "x - YouTube");
            Assert.True(!t.NeedsTabRead(100), "not playing");
            t.OnPlayingChanged(100, true);
            Assert.True(t.NeedsTabRead(100), "on start");
            t.OnTabs(100, null);
            Assert.True(!t.NeedsTabRead(100), "just read (failed)");
            clock.Advance(4000);
            Assert.True(t.NeedsTabRead(100), "follow-up after 4 s");
            t.OnTabs(100, null);
            clock.Advance(14900);
            Assert.True(!t.NeedsTabRead(100), "before 15 s");
            clock.Advance(100);
            Assert.True(t.NeedsTabRead(100), "after 15 s");
            t.OnPlayingChanged(100, false);
            Assert.True(!t.NeedsTabRead(100), "stopped");
        });

        Program.Add("Tracker: How never contains the tab or window title (privacy)", () =>
        {
            var clock = new FakeClock();
            var t = new BrowserTabTracker(new SourceClassifier(ClassTestConfig.Create()), clock);
            t.OnBrowserTitle(100, "chrome.exe", "Secret project meeting - YouTube - Google Chrome");
            t.OnPlayingChanged(100, true);
            var a = t.Current(100, "chrome.exe");
            t.OnTabs(100, [new BrowserTab("Confidential salary review - YouTube Music", true, false)]);
            var b = t.Current(100, "chrome.exe");
            foreach (var c in new[] { a, b })
            {
                Assert.True(!c.How.Contains("Secret", StringComparison.OrdinalIgnoreCase), "how a: " + c.How);
                Assert.True(!c.How.Contains("Confidential", StringComparison.OrdinalIgnoreCase), "how b: " + c.How);
            }
        });
    }
}
