#if DEBUG
using FxAuto.App;
using FxAuto.Core;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Devices;
using FxAuto.Core.FxSound;
using FxAuto.Core.Localization;
using FxAuto.Core.Presets;
using FxAuto.Core.Updates;

namespace FxAuto.Ui.Settings;

/// <summary>Debug-only <see cref="ISettingsHost"/> with sample data, for building the window without the engine
/// (tools/settings-preview). Same threading as the real host: events are raised on the UI thread; task results are
/// completed off the UI thread with RunContinuationsAsynchronously, so <c>await</c> resumes on the UI context.</summary>
internal sealed class FakeSettingsHost : ISettingsHost
{
    private readonly SynchronizationContext _ui;
    private VersionedConfig _config;
    private StatusSnapshot _status;
    private FxStatus _fx;
    private bool _startWithWindows;
    private int _external;

    /// <summary>Preview: the sample config carries this language and theme, so an Apply (which re-applies them, as
    /// the app does) keeps the forced look instead of falling back to "system".</summary>
    public void UsePreferences(string language, string theme)
        => _config = new VersionedConfig(_config.Config with { General = _config.Config.General with { Language = language, Theme = theme } }, _config.Version);

    public FakeSettingsHost(SynchronizationContext ui, bool gameRunning = false)
    {
        _ui = ui;
        _config = new VersionedConfig(SampleConfig(), 1);
        _fx = SampleFx();
        _status = SampleStatus(gameRunning);
        Endpoints = SampleEndpoints();
    }

    public VersionedConfig Config => _config;
    public StatusSnapshot Status => _status;
    public EndpointSnapshot Endpoints { get; }
    public bool StartWithWindows => _startWithWindows;

    /// <summary>Fake update engine (no network): see <see cref="FakeUpdateController"/>.</summary>
    public IUpdateController? Updates { get; set; }

    public event Action<StatusSnapshot>? StatusChanged;
    public event Action<VersionedConfig>? ConfigChanged;

    /// <summary>False = saves made elsewhere are not announced, so the window's next Apply is stale.</summary>
    public bool AnnounceSaves { get; set; } = true;

    /// <summary>Milliseconds each fake call takes.</summary>
    public int Latency { get; set; } = 120;

    public void SetStartWithWindows(bool on) => _startWithWindows = on;

    public Task<ApplyConfigResult> ApplyConfigAsync(FxAutoConfig next, long baseVersion) => Run(() =>
    {
        if (_status.ConfigSaveBlocked is { } blocked)
            return new ApplyConfigResult(false, [new ConfigIssue(IssueLevel.Error, "config.json", blocked)], _config);
        if (baseVersion != _config.Version)
            return new ApplyConfigResult(false, [new ConfigIssue(IssueLevel.Error, "stale",
                $"The configuration changed (version {_config.Version}, edited from {baseVersion}).")], _config);
        var issues = ConfigValidator.Validate(next, _fx);
        if (issues.Any(i => i.Level == IssueLevel.Error)) return new ApplyConfigResult(false, issues, _config);
        Save(next);
        return new ApplyConfigResult(true, issues, _config);
    });

    public Task<(PresetOpResult Result, VersionedConfig Config)> RunPresetOpAsync(PresetOpRequest req, CancellationToken ct) => Run(() =>
    {
        var user = _fx.UserPresets.ToList();
        var steps = new List<string>();
        var c = _config.Config;
        var done = new List<(string, string)>();
        switch (req.Kind)
        {
            case PresetOpKind.Duplicate:
                steps.AddRange([$"--preset={req.Source}", "copy .fac", $"--status (\"{req.Target}\" listed)"]);
                user.Add(req.Target!);
                break;
            case PresetOpKind.Rename:
                steps.AddRange([$"--preset={req.Source}", $"--rename_preset={req.Target}", "--status"]);
                user[user.IndexOf(req.Source!)] = req.Target!;
                c = ConfigEdits.RenamePresetEverywhere(c, req.Source!, req.Target!);
                done.Add((req.Source!, req.Target!));
                break;
            case PresetOpKind.Delete:
                steps.AddRange([$"--preset={req.Source}", "--delete_preset", "--status"]);
                user.Remove(req.Source!);
                c = ConfigEdits.RemovePreset(c, req.Source!, null);
                break;
        }
        _fx = _fx with { UserPresets = user };
        if (!ReferenceEquals(c, _config.Config)) Save(c);
        var result = new PresetOpResult(true, null, steps, _fx, done) { ReapplyRequested = req.Kind == PresetOpKind.Delete };
        return (result, _config);
    });

    public Task<FxStatus?> RefreshFxStatusAsync() => Run<FxStatus?>(() => _fx);

    public IReadOnlyList<DetectedSource> DetectPlayingNow() =>
    [
        new(4120, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", null, "DAZN - Live sports",
            new SourceClass(Categories.Video, "chrome", "DAZN", "browser default", false, true)),
        new(9876, "RacingSim.exe", @"D:\SteamLibrary\steamapps\common\Racing Sim\RacingSim.exe", null, "Racing Sim",
            new SourceClass(Categories.GameAction, "steam-games", null, "folder \\steamapps\\common\\", true, false)),
    ];

    public Task SetModeAsync(Mode mode, string? lockedPreset = null, TimeSpan? pauseFor = null) => Run<object?>(() =>
    {
        SetStatus(_status with { Mode = mode, ExpectedPreset = mode == Mode.Locked ? lockedPreset : _status.ExpectedPreset });
        return null;
    });

    public Task<PresetOpResult> BeginEditAsync(string preset) => Run(() =>
    {
        SetStatus(_status with { Mode = Mode.Editing, ExpectedPreset = preset, VerifiedPreset = preset, EditingPreset = preset });
        return new PresetOpResult(true, null, [$"--preset={preset}"], _fx, []);
    });

    public Task<(PresetOpResult Result, VersionedConfig Config)> FinishEditAsync(EditOutcome outcome, string? saveAsName) => Run(() =>
    {
        if (outcome == EditOutcome.SaveAsNew && saveAsName is not null) _fx = _fx with { UserPresets = [.. _fx.UserPresets, saveAsName] };
        SetStatus(_status with { Mode = Mode.Auto, EditingPreset = null });
        return (new PresetOpResult(true, null, [outcome.ToString()], _fx, []), _config);
    });

    // ── preview helpers ──

    /// <summary>Simulates StatusSnapshot.ConfigSaveBlocked (null = saves allowed). While set, a plain apply is refused
    /// like the real host does; ApplyConfigReplacingAsync saves anyway.</summary>
    public void SimulateSaveBlocked(string? reason, bool replaceable = false)
        => SetStatus(_status with { ConfigSaveBlocked = reason, ConfigSaveReplaceable = reason is not null && replaceable });

    public Task<ApplyConfigResult> ApplyConfigReplacingAsync(FxAutoConfig next, long baseVersion) => Run(() =>
    {
        var issues = ConfigValidator.Validate(next, _fx);
        if (issues.Any(i => i.Level == IssueLevel.Error)) return new ApplyConfigResult(false, issues, _config);
        Save(next);
        _status = _status with { ConfigSaveBlocked = null, ConfigSaveReplaceable = false };
        var s = _status;
        _ui.Post(_ => StatusChanged?.Invoke(s), null);
        return new ApplyConfigResult(true, issues, _config);
    });

    /// <summary>Simulates a save from elsewhere (hand edit, tray): adds a source, bumps the version.</summary>
    public void SimulateExternalEdit()
    {
        var c = _config.Config;
        var s = new SourceConfig(_external == 0 ? "added-elsewhere" : $"added-elsewhere-{_external + 1}", "Added elsewhere", SourceKind.App, new SourceMatch([$"addedelsewhere{_external}.exe"], [], [], [], null),
            Categories.MovieOrSeries, false, DefaultConfig.OriginUser);
        _external++;
        Save(c with { Sources = [.. c.Sources, s] });
    }

    private void Save(FxAutoConfig next)
    {
        _config = new VersionedConfig(next, _config.Version + 1);
        var v = _config;
        if (AnnounceSaves) _ui.Post(_ => ConfigChanged?.Invoke(v), null);
    }

    private void SetStatus(StatusSnapshot s)
    {
        _status = s;
        _ui.Post(_ => StatusChanged?.Invoke(s), null);
    }

    private Task<T> Run<T>(Func<T> body)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latency = Latency;
        _ = Task.Run(async () =>
        {
            await Task.Delay(latency).ConfigureAwait(false);
            // the real coordinator runs on its own thread; the fake runs the body on the UI thread to keep its
            // state single-threaded, then completes off it
            var done = new ManualResetEventSlim();
            T result = default!;
            Exception? error = null;
            _ui.Post(_ =>
            {
                try { result = body(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            }, null);
            done.Wait(TimeSpan.FromSeconds(10));
            done.Dispose();
            if (error is not null) tcs.TrySetException(error);
            else tcs.TrySetResult(result);
        });
        return tcs.Task;
    }

    // ── sample data (a typical setup, plus a second device) ──

    private static readonly string[] UserPresets =
        ["Headphones - General", "Headphones - Music", "Headphones - Video", "Headphones - Sports TV", "Headphones - Movie", "Headphones - Movie Night", "Headphones - Voice", "Headphones - FPS",
         "Headphones - TPS", "Headphones - RPG", "Headphones - MOBA", "Headphones - Racing", "Headphones - Sports Game", "Headphones - Strategy", "Headphones - Action", "Headphones - Horror"];

    private static readonly string[] BuiltIns =
        ["General", "Music", "Movies", "Gaming", "Voice", "Streaming Video", "Bass Boost", "Dialog Boost", "Classic Processing",
         "Transistor Radio", "Treble Boost", "Vocal Boost", "Signature"];

    private static FxAutoConfig SampleConfig()
    {
        var c = DefaultConfig.Create();
        var devices = new List<DeviceConfig>
        {
            new("headset", "Headset", "Headphones", new DeviceMatchSpec(["Wireless Headset"], FormFactor.Headset, null, [], null),
                "Headphones - General", true, "Headphones (Wireless Headset)"),
            new("realtek", "Desk speakers", "Realtek", new DeviceMatchSpec(["Realtek(R) Audio"], FormFactor.Speakers, null, [], null),
                "General", true, "Speakers (Realtek(R) Audio)"),
        };
        var map = new (string Cat, string Preset)[]
        {
            (Categories.Music, "Headphones - Music"), (Categories.MusicVideo, "Headphones - Music"), (Categories.Video, "Headphones - Video"),
            (Categories.LiveStream, "Headphones - Video"), (Categories.Podcast, "Headphones - Video"), (Categories.Sports, "Headphones - Sports TV"),
            (Categories.MovieOrSeries, "Headphones - Movie"), (Categories.Call, "Headphones - Voice"), (Categories.GameFps, "Headphones - FPS"),
            (Categories.GameTps, "Headphones - TPS"), (Categories.GameRpg, "Headphones - RPG"), (Categories.GameMoba, "Headphones - MOBA"),
            (Categories.GameRacing, "Headphones - Racing"), (Categories.GameSports, "Headphones - Sports Game"),
            (Categories.GameStrategy, "Headphones - Strategy"), (Categories.GameAction, "Headphones - Action"), (Categories.GameHorror, "Headphones - Witcher"),
        };
        var assignments = map.Select(m => new Assignment("headset", m.Cat, null, m.Preset)).ToList();
        assignments.Add(new Assignment("realtek", Categories.Music, null, "Music"));
        var presets = UserPresets.ToDictionary(p => p, p => new PresetMeta(p == "Headphones - Voice" ? 1.5 : 0, null), StringComparer.Ordinal);
        return c with { Devices = devices, Assignments = assignments, Presets = presets };
    }

    private static FxStatus SampleFx() => new("1.2.16.0", "Headphones - Music", true, false, 0, "Headphones (Wireless Headset)",
        UserPresets, BuiltIns, DateTimeOffset.Now)
    {
        OutputDevices = ["Headphones (Wireless Headset)", "Speakers (Realtek(R) Audio)"],
        ModifiedByPreset = UserPresets.Concat(BuiltIns).ToDictionary(p => p, p => p == "Headphones - Movie"),
    };

    private static StatusSnapshot SampleStatus(bool gameRunning)
    {
        var now = DateTimeOffset.Now;
        var playing = new List<PlayingSourceView>
        {
            new(4120, "chrome.exe", Categories.Music, "youtube-music", "YouTube Music", "site segment", true, true, !gameRunning, now.AddMinutes(-4)),
            new(7312, "Spotify.exe", Categories.Music, null, null, "no match", false, false, false, now.AddMinutes(-20)),
            new(2208, "Discord.exe", Categories.Call, "discord", null, "app exe", false, true, false, now.AddSeconds(-40)),
        };
        if (gameRunning)
            playing.Insert(0, new(9876, "witcher3.exe", Categories.GameRpg, "the-witcher-3", null, "game exe+folder", true, true, true, now.AddMinutes(-12)));
        return new StatusSnapshot(Mode.Auto, true, false, true, "headset", "Headset", "Headphones (Wireless Headset)",
            gameRunning ? "Headphones - RPG" : "Headphones - Music", gameRunning ? "Headphones - RPG" : "Headphones - Music",
            gameRunning ? "Game: RPG in the foreground beats Music in the background. Headset → Game: RPG → Headphones - RPG (category)."
                : "YouTube Music is playing in the foreground. Headset → Music → Headphones - Music (category). Discord is a call and calls are ignored.",
            playing,
            [ConfigIssue.Of(IssueLevel.Warning, "assignments[16].preset", "Issue.Validator.PresetMissing", "Headphones - Witcher")],
            null)
        {
            // the same structured reason the coordinator builds (arbiter → resolver), so the preview shows it translated
            WhyText = new Msg("Why.Chain",
                new Msg("Why.Winner", new Msg(gameRunning ? "Why.Level.ForegroundGamePlaying" : "Why.Level.ForegroundPlaying"),
                    new Msg("Why.SrcWithSource", new Msg("Category." + (gameRunning ? Categories.GameRpg : Categories.Music)), gameRunning ? "the-witcher-3" : "youtube-music")),
                new Msg("Res.Category", new Msg("Category." + (gameRunning ? Categories.GameRpg : Categories.Music)))),
        };
    }

    private static EndpointSnapshot SampleEndpoints() => new(DateTimeOffset.Now,
    [
        new("{0.0.0.00000000}.{headset}", "Headphones (Wireless Headset)", "Headphones", "Wireless Headset", FormFactor.Headset, "{c-headset}", "USB", 1, false),
        new("{0.0.0.00000000}.{realtek}", "Speakers (Realtek(R) Audio)", "Speakers", "Realtek(R) Audio", FormFactor.Speakers, "{c-rt}", "HDAUDIO", 1, false),
        new("{0.0.0.00000000}.{monitor}", "Monitor (NVIDIA High Definition Audio)", "Monitor", "NVIDIA High Definition Audio", FormFactor.Hdmi, "{c-monitor}", "HDAUDIO", 1, false),
        new("{0.0.0.00000000}.{redmi}", "Headphones (Redmi Buds 6 Pro)", "Headphones", "Redmi Buds 6 Pro", FormFactor.Headphones, "{c-redmi}", "BTHENUM", 4, false),
        new("{0.0.0.00000000}.{fx}", "Speakers (FxSound Audio Enhancer)", "Speakers", "FxSound Audio Enhancer", FormFactor.Speakers, "{c-fx}", "ROOT", 1, true),
    ], "{0.0.0.00000000}.{fx}", "{0.0.0.00000000}.{fx}");
}

/// <summary>Debug-only <see cref="IUpdateController"/> for the preview: answers checks with <see cref="NextResult"/>
/// after a short delay and simulates a download (progress, then <see cref="DownloadOutcome"/>). Never touches the
/// network or starts anything. Raises <see cref="CheckCompleted"/> on a pool thread, like the real service.</summary>
internal sealed class FakeUpdateController : IUpdateController
{
    public SemVersion Current { get; init; } = new(1, 0, 0);
    public UpdateCheckResult? LastResult { get; set; }
    public DateTimeOffset? LastCheckAt { get; set; }
    public SemVersion? PendingVersion => UpdateNotice.Pending(Current, LastResult, null, includePrereleases: false);
    public UpdateCheckResult? NextResult { get; set; }
    public DownloadResult? DownloadOutcome { get; set; }
    public int Latency { get; set; } = 300;

    public event Action<UpdateCheckResult>? CheckCompleted;

    /// <summary>A sample "version available" result (1.2.0 with notes in GitHub Markdown).</summary>
    public static UpdateCheckResult Available(SemVersion current, DateTimeOffset at) => new(UpdateCheckKind.UpdateAvailable, current, at)
    {
        Latest = new SemVersion(1, 2, 0),
        Title = "FxAuto 1.2.0",
        Notes = "## What's new\n- **Updates**: FxAuto checks GitHub Releases once a day and installs new versions from About.\n"
              + "- Spanish user interface, with language and theme selectors in General.\n- Faster preset switching when a game starts.\n\n"
              + "## Fixes\n- The tray tooltip no longer shows an old preset after FxSound restarts.\n"
              + "- Browser tabs playing audio are detected again after Windows resumes from sleep.\n"
              + "- Settings keeps the window size between sessions.\n\nFull list: [compare](https://github.com/ZahiriNatZuke/fxauto/compare/v1.0.0...v1.2.0)",
        ReleaseUrl = "https://github.com/ZahiriNatZuke/fxauto/releases/tag/v1.2.0",
        Installer = new ReleaseAsset("FxAuto-Setup-1.2.0.exe", "https://github.com/ZahiriNatZuke/fxauto/releases/download/v1.2.0/FxAuto-Setup-1.2.0.exe", 41_943_040),
        Checksum = new ReleaseAsset("FxAuto-Setup-1.2.0.exe.sha256", "https://github.com/ZahiriNatZuke/fxauto/releases/download/v1.2.0/FxAuto-Setup-1.2.0.exe.sha256", 90),
    };

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        var r = NextResult ?? new UpdateCheckResult(UpdateCheckKind.UpToDate, Current, DateTimeOffset.Now) { Latest = Current };
        r = r with { At = DateTimeOffset.Now };
        LastResult = r;
        LastCheckAt = r.At;
        CheckCompleted?.Invoke(r);
        return r;
    }

    public async Task<DownloadResult> DownloadAndVerifyAsync(UpdateCheckResult update, IProgress<DownloadProgress>? progress = null,
                                                             CancellationToken ct = default)
    {
        long total = update.Installer?.Size ?? 0;
        for (var i = 0; i <= 10; i++)
        {
            if (ct.IsCancellationRequested)
                return new DownloadResult(false, update.Latest, null, null, "Download cancelled.", Cancelled: true);
            progress?.Report(new DownloadProgress(total * i / 10, total));
            try { await Task.Delay(Latency, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new DownloadResult(false, update.Latest, null, null, "Download cancelled.", Cancelled: true); }
        }
        return DownloadOutcome ?? new DownloadResult(false, update.Latest, null, null, "The downloaded installer does not match its SHA-256; it was deleted.")
        {
            Failure = DownloadFailure.Mismatch,
        };
    }

    public bool LaunchInstallerAndExit(DownloadResult download) => false;
}
#endif
