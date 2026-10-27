using FxAuto.Core;
using FxAuto.Core.Config;
using FxAuto.Core.Devices;
using FxAuto.Core.FxSound;
using FxAuto.Core.Presets;

namespace FxAuto.App;

/// <summary>
/// <see cref="ISettingsHost"/> over the coordinator. Properties read the coordinator's published (immutable)
/// snapshots; requests are posted as events and answered through <see cref="TaskCompletionSource{TResult}"/>
/// (continuations run asynchronously, so an <c>await</c> on the UI thread resumes on the UI thread). The events
/// <see cref="StatusChanged"/> and <see cref="ConfigChanged"/> are raised on the UI thread through
/// <paramref name="uiPost"/>.
/// </summary>
internal sealed class SettingsHost : ISettingsHost, IDisposable
{
    private readonly Coordinator _coordinator;
    private readonly JsonlLog _log;
    private readonly Action<Action> _uiPost;

    public SettingsHost(Coordinator coordinator, JsonlLog log, Action<Action> uiPost)
    {
        _coordinator = coordinator;
        _log = log;
        _uiPost = uiPost;
        _coordinator.StatusChanged += OnStatus;
        _coordinator.ConfigChanged += OnConfig;
    }

    public VersionedConfig Config => _coordinator.Config;
    public StatusSnapshot Status => _coordinator.Current.Status;
    public EndpointSnapshot Endpoints => _coordinator.Endpoints;

    /// <summary>Update engine for About; set by TrayApp.AttachUpdates (null until then).</summary>
    public IUpdateController? Updates { get; set; }

    public event Action<StatusSnapshot>? StatusChanged;
    public event Action<VersionedConfig>? ConfigChanged;

    private void OnStatus(TrayState s) => _uiPost(() => StatusChanged?.Invoke(s.Status));

    private void OnConfig(VersionedConfig c) => _uiPost(() => ConfigChanged?.Invoke(c));

    public Task<ApplyConfigResult> ApplyConfigAsync(FxAutoConfig next, long baseVersion)
    {
        ArgumentNullException.ThrowIfNull(next);
        var tcs = NewTcs<ApplyConfigResult>();
        _coordinator.Post(new ConfigAppliedEvent(DateTimeOffset.Now, next, baseVersion, tcs));
        return tcs.Task;
    }

    public Task<(PresetOpResult Result, VersionedConfig Config)> RunPresetOpAsync(PresetOpRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var tcs = NewTcs<(PresetOpResult, VersionedConfig)>();
        _coordinator.Post(new PresetOpEvent(DateTimeOffset.Now, req, ct, tcs));
        return tcs.Task;
    }

    public async Task<FxStatus?> RefreshFxStatusAsync()
        => await Request(SettingsRequestKind.RefreshFxStatus).ConfigureAwait(false) as FxStatus;

    public IReadOnlyList<DetectedSource> DetectPlayingNow() => _coordinator.DetectedNow;

    public Task<ApplyConfigResult> ApplyConfigReplacingAsync(FxAutoConfig next, long baseVersion)
    {
        ArgumentNullException.ThrowIfNull(next);
        var tcs = NewTcs<ApplyConfigResult>();
        _coordinator.Post(new ConfigAppliedEvent(DateTimeOffset.Now, next, baseVersion, tcs) { ConfirmReplace = true });
        return tcs.Task;
    }

    public Task SetModeAsync(Mode mode, string? lockedPreset = null, TimeSpan? pauseFor = null)
        => Request(SettingsRequestKind.SetMode, mode, lockedPreset, pauseFor);

    public async Task<PresetOpResult> BeginEditAsync(string preset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preset);
        return (PresetOpResult)(await Request(SettingsRequestKind.BeginEdit, preset: preset).ConfigureAwait(false))!;
    }

    public async Task<(PresetOpResult Result, VersionedConfig Config)> FinishEditAsync(EditOutcome outcome, string? saveAsName)
        => ((PresetOpResult, VersionedConfig))(await Request(SettingsRequestKind.FinishEdit, preset: saveAsName, outcome: outcome)
            .ConfigureAwait(false))!;

    public bool StartWithWindows
    {
        get
        {
            try { return AutoStart.IsEnabled(); }
            catch (System.Security.SecurityException) { return false; }
        }
    }

    public void SetStartWithWindows(bool on)
    {
        AutoStart.Set(on);
        _log.Log("autostart", new { enabled = on, command = AutoStart.Command });
    }

    private Task<object?> Request(SettingsRequestKind kind, Mode? mode = null, string? preset = null, TimeSpan? pauseFor = null,
                                  EditOutcome? outcome = null)
    {
        var tcs = NewTcs<object?>();
        _coordinator.Post(new SettingsRequestEvent(DateTimeOffset.Now, kind, mode, preset, pauseFor, outcome, tcs));
        return tcs.Task;
    }

    private static TaskCompletionSource<T> NewTcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        _coordinator.StatusChanged -= OnStatus;
        _coordinator.ConfigChanged -= OnConfig;
    }
}
