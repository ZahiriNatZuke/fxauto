using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using FxAuto.Core.Config;
using FxAuto.Core.Updates;

namespace FxAuto.App;

/// <summary>
/// Update engine (no UI). Checks GitHub Releases of <see cref="Owner"/>/<see cref="Repo"/> once a day when
/// automatic checks are on, downloads and verifies the installer, and launches it.
/// <para>Network: only <c>api.github.com/repos/{owner}/{repo}/releases/latest</c> (or <c>/releases</c> with
/// pre-releases on) and the release asset downloads. No token, no telemetry.</para>
/// <para>For the UI: <see cref="CheckCompleted"/> (raised on a pool thread) and <see cref="LastResult"/>; then
/// <see cref="DownloadAndVerifyAsync"/> and <see cref="LaunchInstallerAndExit"/>. The settings come from config.json
/// (<c>general.updates</c>) through <see cref="ApplySettings"/>; the tray balloon is in TrayApp.</para>
/// </summary>
internal sealed class UpdateService : IUpdateController, IDisposable
{
    public const string Owner = "ZahiriNatZuke";
    public const string Repo = "fxauto";

    /// <summary>Command line passed to the Inno Setup installer for a silent update. /RELAUNCH reopens FxAuto when
    /// it ends; the installer closes FxAuto itself with <c>FxAuto.exe --exit</c>, but FxAuto also exits right after
    /// launching it.</summary>
    public const string InstallerArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    /// <summary>Idle limit while reading the installer body (the download as a whole has no time limit).</summary>
    public static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(60);
    private const long MaxChecksumBytes = 64 * 1024;
    private const long MaxInstallerBytes = 512L * 1024 * 1024;

    private static readonly string ApiBase = $"https://api.github.com/repos/{Owner}/{Repo}";

    private readonly HttpClient _http;
    private readonly string _stateFile;
    private readonly string _downloadDir;
    private readonly Action<string, object> _log;
    private readonly Action<string> _requestExit;
    private readonly Func<DateTimeOffset> _now;
    private readonly DateTimeOffset _startedAt;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly Lock _lock = new();
    private UpdateState _state;
    private UpdateCheckResult? _last;
    private System.Threading.Timer? _timer;
    private bool _disposed;

    public SemVersion Current { get; }

    /// <summary>Raised after every check (automatic or manual), on a pool thread.</summary>
    public event Action<UpdateCheckResult>? CheckCompleted;

    public UpdateService(SemVersion current, string stateFile, string downloadDir, Action<string, object> log,
                         Action<string> requestExit, HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
    {
        Current = current;
        _stateFile = stateFile;
        _downloadDir = downloadDir;
        _log = log;
        _requestExit = requestExit;
        _now = now ?? (() => DateTimeOffset.Now);
        _startedAt = _now();
        _state = UpdateStateJson.Load(stateFile);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;   // per-request limits below
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FxAuto", current.ToString()));
    }

    /// <summary>Running version from the assembly's informational version (build metadata ignored).</summary>
    public static SemVersion CurrentVersion()
    {
        var asm = typeof(UpdateService).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemVersion.TryParse(info, out var v)) return v;
        var n = asm.GetName().Version;
        return n is null ? new SemVersion(0, 0, 0) : new SemVersion(Math.Max(n.Major, 0), Math.Max(n.Minor, 0), Math.Max(n.Build, 0));
    }

    /// <summary>%LOCALAPPDATA%\FxAuto\updates.</summary>
    public static string DefaultDownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FxAuto", "updates");

    public UpdateState State { get { lock (_lock) return _state; } }
    public UpdateCheckResult? LastResult { get { lock (_lock) return _last; } }

    public DateTimeOffset? LastCheckAt { get { lock (_lock) return _state.LastCheckAt; } }

    public SemVersion? PendingVersion
    {
        get { lock (_lock) return UpdateNotice.Pending(Current, _last, _state.LatestKnownVersion, _state.IncludePrereleases); }
    }

    /// <summary>When <paramref name="result"/> should be announced with a tray balloon (UpdateNotice.ShouldAnnounce),
    /// records its version as announced (persisted) and returns true; false = no balloon.</summary>
    public bool TryMarkAnnounced(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_lock)
        {
            if (!UpdateNotice.ShouldAnnounce(result, _state.NotifiedVersion)) return false;
            _state = _state with { NotifiedVersion = result.Latest!.ToString() };
        }
        SaveState();
        return true;
    }

    /// <summary>Next automatic check (null = automatic checks off).</summary>
    public DateTimeOffset? NextCheckAt { get { lock (_lock) return UpdateSchedule.NextCheckAt(_state, _startedAt); } }

    /// <summary>Removes old downloads and arms the daily automatic check.</summary>
    public void Start()
    {
        CleanDownloads(keep: null);
        lock (_lock)
        {
            if (_disposed) return;
            _timer ??= new System.Threading.Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
        }
        Rearm();
    }

    /// <summary>Turns automatic checks on or off (persisted). The UI calls it from its settings page.</summary>
    public void SetCheckAutomatically(bool on) => ChangeState(s => s with { CheckAutomatically = on });

    /// <summary>Whether pre-releases are offered (persisted; off by default).</summary>
    public void SetIncludePrereleases(bool on) => ChangeState(s => s with { IncludePrereleases = on });

    /// <summary>Takes the settings from config.json (<c>general.updates</c>): from then on update-state.json keeps only
    /// the run state. Saves and re-arms only when something changed.</summary>
    public void ApplySettings(UpdatePreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        bool changed;
        lock (_lock)
        {
            var next = _state with { CheckAutomatically = prefs.CheckAutomatically, IncludePrereleases = prefs.IncludePrereleases,
                                     SettingsInFile = false };
            changed = next != _state;
            _state = next;
        }
        if (!changed) return;
        SaveState();
        Rearm();
        _log("update.settings", new { checkAutomatically = prefs.CheckAutomatically, includePrereleases = prefs.IncludePrereleases });
    }

    private void ChangeState(Func<UpdateState, UpdateState> change)
    {
        lock (_lock) _state = change(_state);
        SaveState();
        Rearm();
    }

    /// <summary>Asks GitHub for the latest release. Never throws (except cancellation of a manual check): network
    /// and HTTP problems give <see cref="UpdateCheckKind.Error"/>; a 404 (no releases yet) is "up to date"; a rate
    /// limit (403/429) is an error with <see cref="UpdateCheckResult.RetryAfter"/>, and no request is sent before it.</summary>
    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default) => CheckAsync(automatic: false, ct);

    private async Task<UpdateCheckResult> CheckAsync(bool automatic, CancellationToken ct)
    {
        await _checkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await FetchAsync(ct).ConfigureAwait(false) with { Automatic = automatic };
            lock (_lock)
            {
                _state = UpdateSchedule.After(_state, result);
                _last = result;
            }
            SaveState();
            _log("update.check", new
            {
                result = result.Kind.ToString(), automatic, current = result.Current.ToString(), latest = result.Latest?.ToString(),
                error = result.Error, retryAfter = result.RetryAfter,
            });
            Rearm();
            try { CheckCompleted?.Invoke(result); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log("update.check.handler.error", new { error = ex.Message }); }
            return result;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task<UpdateCheckResult> FetchAsync(CancellationToken ct)
    {
        var now = _now();
        UpdateState st;
        lock (_lock) st = _state;
        if (st.RetryNotBefore is { } rl && rl > now)
            return new(UpdateCheckKind.Error, Current, now) { Error = "GitHub limited update checks; retrying later.", RetryAfter = rl, ErrorKind = UpdateCheckError.RateLimited };

        bool pre = st.IncludePrereleases;
        var url = pre ? ApiBase + "/releases?per_page=20" : ApiBase + "/releases/latest";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return UpdateDecision.Evaluate(Current, null, pre, Owner, Repo, now);   // private/empty repo: no releases
            if (UpdateDecision.RateLimitedUntil(code, Header(resp, "Retry-After"), Header(resp, "x-ratelimit-remaining"),
                                                Header(resp, "x-ratelimit-reset"), now) is { } until)
                return new(UpdateCheckKind.Error, Current, now) { Error = $"GitHub limited update checks (HTTP {code}).", RetryAfter = until, ErrorKind = UpdateCheckError.RateLimited, HttpStatus = code };
            if (!resp.IsSuccessStatusCode)
                return new(UpdateCheckKind.Error, Current, now) { Error = $"GitHub answered HTTP {code}.", ErrorKind = UpdateCheckError.Http, HttpStatus = code };

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            ReleaseInfo? release;
            string? error;
            if (pre)
            {
                var list = ReleaseJson.ParseList(json, out error);
                release = list is null ? null : UpdateDecision.PickLatest(list, includePrerelease: true)?.Release;
            }
            else release = ReleaseJson.Parse(json, out error);
            if (error is not null) return new(UpdateCheckKind.Error, Current, now) { Error = "Unexpected answer from GitHub: " + error, ErrorKind = UpdateCheckError.BadAnswer };
            return UpdateDecision.Evaluate(Current, release, pre, Owner, Repo, now);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(UpdateCheckKind.Error, Current, now) { Error = "GitHub did not answer in time.", ErrorKind = UpdateCheckError.Timeout };
        }
        catch (HttpRequestException ex)
        {
            return new(UpdateCheckKind.Error, Current, now) { Error = "Could not reach GitHub: " + ex.Message, ErrorKind = UpdateCheckError.Network };
        }
    }

    /// <summary>Downloads the installer of <paramref name="update"/> (an <see cref="UpdateCheckKind.UpdateAvailable"/>
    /// result) to %LOCALAPPDATA%\FxAuto\updates and checks its SHA-256 against the release's .sha256 file. A mismatch,
    /// an error or a cancellation deletes the file. Older downloads are removed first.</summary>
    public async Task<DownloadResult> DownloadAndVerifyAsync(UpdateCheckResult update, IProgress<DownloadProgress>? progress = null,
                                                             CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Kind != UpdateCheckKind.UpdateAvailable || update.Installer is not { } inst || update.Checksum is not { } sum
            || update.Latest is not { } version)
            return new(false, update.Latest, null, null, "There is no update to download.") { Failure = DownloadFailure.NoUpdate };
        if (!UpdateAssets.IsPlainFileName(inst.Name) || !UpdateAssets.IsTrustedDownloadUrl(inst.DownloadUrl, Owner, Repo)
            || !UpdateAssets.IsTrustedDownloadUrl(sum.DownloadUrl, Owner, Repo))
            return new(false, version, null, null, "The release assets are not FxAuto release downloads.") { Failure = DownloadFailure.Untrusted };

        await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
        var target = Path.Combine(_downloadDir, inst.Name);
        var part = target + ".part";
        try
        {
            Directory.CreateDirectory(_downloadDir);
            CleanDownloads(keep: null);

            var sumText = await GetSmallTextAsync(sum.DownloadUrl, ct).ConfigureAwait(false);
            var expected = UpdateAssets.ParseChecksum(sumText, inst.Name);
            if (expected is null) return Fail(version, $"{sum.Name} has no SHA-256 for {inst.Name}.", part, target) with { Failure = DownloadFailure.NoChecksum };

            await DownloadFileAsync(inst, part, progress, ct).ConfigureAwait(false);
            var actual = UpdateAssets.ComputeSha256(part);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return Fail(version, "The downloaded installer does not match its SHA-256; it was deleted.", part, target,
                            new { expected, actual }) with { Failure = DownloadFailure.Mismatch };

            File.Move(part, target, overwrite: true);
            _log("update.download", new { result = "ok", version = version.ToString(), file = inst.Name, sha256 = actual });
            return new(true, version, target, actual, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(part);
            TryDelete(target);
            _log("update.download", new { result = "cancelled", version = version.ToString() });
            return new(false, version, null, null, "Download cancelled.", Cancelled: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or OperationCanceledException or InvalidDataException)
        {
            var msg = ex is OperationCanceledException ? "The download stalled." : ex.Message;
            return Fail(version, "Could not download the update: " + msg, part, target);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private DownloadResult Fail(SemVersion version, string error, string part, string target, object? extra = null)
    {
        TryDelete(part);
        TryDelete(target);
        _log("update.download", new { result = "error", version = version.ToString(), error, detail = extra });
        return new(false, version, null, null, error);
    }

    private async Task<string> GetSmallTextAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxChecksumBytes) throw new InvalidDataException("The checksum file is too large.");
        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        var buf = new byte[MaxChecksumBytes + 1];
        int total = 0, n;
        while (total < buf.Length && (n = await s.ReadAsync(buf.AsMemory(total), cts.Token).ConfigureAwait(false)) > 0) total += n;
        if (total > MaxChecksumBytes) throw new InvalidDataException("The checksum file is too large.");
        return System.Text.Encoding.UTF8.GetString(buf, 0, total);
    }

    private async Task DownloadFileAsync(ReleaseAsset asset, string part, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(RequestTimeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : 0);
        if (total > MaxInstallerBytes) throw new InvalidDataException("The installer is too large.");
        await using var src = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
        await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buf = new byte[1 << 16];
            long got = 0;
            long lastReport = -1;
            int n;
            progress?.Report(new DownloadProgress(0, total));
            while (true)
            {
                stall.CancelAfter(DownloadStallTimeout);
                n = await src.ReadAsync(buf, stall.Token).ConfigureAwait(false);
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), stall.Token).ConfigureAwait(false);
                got += n;
                if (got > MaxInstallerBytes) throw new InvalidDataException("The installer is too large.");
                if (got - lastReport >= 256 * 1024)
                {
                    lastReport = got;
                    progress?.Report(new DownloadProgress(got, total));
                }
            }
            await dst.FlushAsync(stall.Token).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(got, total));
            if (asset.Size > 0 && got != asset.Size)
                throw new InvalidDataException($"The installer size is {got} bytes, expected {asset.Size}.");
        }
    }

    /// <summary>Re-checks the installer's hash, starts it without elevation (the installer asks for what it needs)
    /// with <see cref="InstallerArguments"/>, and exits FxAuto through the normal exit path. Returns false (and FxAuto
    /// keeps running) when the file changed or could not be started (for example, the user declined UAC).</summary>
    public bool LaunchInstallerAndExit(DownloadResult download)
    {
        ArgumentNullException.ThrowIfNull(download);
        if (!download.Ok || download.InstallerPath is not { } path || download.Sha256 is not { } sha) return false;
        try
        {
            if (!File.Exists(path) || !UpdateAssets.VerifySha256(path, sha))
            {
                _log("update.install", new { result = "error", error = "the installer changed after the download" });
                TryDelete(path);
                return false;
            }
            var psi = new ProcessStartInfo(path, InstallerArguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? _downloadDir,
            };
            using var p = Process.Start(psi);
            _log("update.install", new { result = "started", version = download.Version?.ToString(), pid = p?.Id });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log("update.install", new { result = "error", error = ex.Message });
            return false;
        }
        _requestExit("update");
        return true;
    }

    /// <summary>Deletes everything in the downloads folder except <paramref name="keep"/>; files in use (an
    /// installer still running) are left for next time.</summary>
    public void CleanDownloads(string? keep)
    {
        try
        {
            if (!Directory.Exists(_downloadDir)) return;
            foreach (var f in Directory.EnumerateFiles(_downloadDir))
                if (keep is null || !string.Equals(Path.GetFullPath(f), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                    TryDelete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void OnTimer()
    {
        bool due;
        lock (_lock)
        {
            if (_disposed) return;
            due = UpdateSchedule.IsDue(_state, _startedAt, _now());
        }
        if (due) _ = RunAutomaticCheckAsync();
        else Rearm();
    }

    private async Task RunAutomaticCheckAsync()
    {
        try { await CheckAsync(automatic: true, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log("update.check", new { result = "Error", error = ex.Message });
            Rearm();
        }
    }

    /// <summary>Arms the timer for the next due check, at most one hour ahead (so sleep and clock changes are
    /// re-evaluated).</summary>
    private void Rearm()
    {
        lock (_lock)
        {
            if (_disposed || _timer is null) return;
            var next = UpdateSchedule.NextCheckAt(_state, _startedAt);
            if (next is null)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
            var wait = next.Value - _now();
            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            if (wait > TimeSpan.FromHours(1)) wait = TimeSpan.FromHours(1);
            _timer.Change(wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void SaveState()
    {
        UpdateState s;
        lock (_lock) s = _state;
        try { UpdateStateJson.Save(_stateFile, s); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("update.state.save.error", new { error = ex.Message });
        }
    }

    private static string? Header(HttpResponseMessage r, string name)
        => r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        _http.Dispose();
    }
}
