using System.Security.Cryptography;
using System.Text;
using FxAuto.Core.Config;
using FxAuto.Core.Localization;

namespace FxAuto.App;

/// <summary>How the config in use at startup was obtained.</summary>
internal enum ConfigOrigin { File, Default, Backup, DefaultAfterError }

/// <summary>Startup result of <see cref="ConfigStore.Load"/>.</summary>
internal sealed record ConfigLoad(FxAutoConfig Config, IReadOnlyList<ConfigIssue> Issues, ConfigOrigin Origin);

/// <summary>A hand edit of config.json seen by the watcher. Config null = unusable (parse error or deleted): the
/// coordinator keeps the last valid config and shows <see cref="Issues"/> in Status.</summary>
internal sealed record ConfigFileChange(FxAutoConfig? Config, IReadOnlyList<ConfigIssue> Issues);

/// <summary>
/// %APPDATA%\FxAuto\config.json (REQ-110) and runtime-state.json. First run: start from
/// <see cref="DefaultConfig"/>; atomic saves
/// (<c>config.json.tmp</c> → <c>File.Replace</c> keeping <c>config.json.bak</c>); a <see cref="FileSystemWatcher"/>
/// reports hand edits and ignores FxAuto's own writes by content hash. A config written by a newer FxAuto is used
/// read-only and never saved. Platform-neutral (System.IO only): compiled into the tests too.
/// </summary>
internal sealed class ConfigStore : IDisposable
{
    public const string ConfigFileName = "config.json";
    public const string BackupFileName = "config.json.bak";
    public const string InvalidFileName = "config.json.invalid";

    private readonly Action<string, object> _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private string? _ownHash;               // hash of the last content FxAuto wrote or loaded
    private bool _diskValid = true;         // config.json on disk is a valid config (or missing): Save may keep it as .bak
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private Action<ConfigFileChange>? _onChange;

    public ConfigStore(string directory, Action<string, object> log, Func<DateTimeOffset>? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FxAuto");

    public string Directory { get; }
    public string ConfigFile => Path.Combine(Directory, ConfigFileName);
    public string BackupFile => Path.Combine(Directory, BackupFileName);
    public string StateFile => Path.Combine(Directory, LearnedStateJson.FileName);
    public string InvalidFile => Path.Combine(Directory, InvalidFileName);

    /// <summary>Not null when the config must never be saved (written by a newer FxAuto).</summary>
    public string? ReadOnlyReason { get; private set; }

    /// <summary>Not null when saving would overwrite a config.json FxAuto could not use (locked/unreadable, or
    /// invalid with no valid backup, so the built-in defaults are in memory). Saves are refused until the file reads
    /// valid again or the user confirms the replacement (<c>Save(c, confirmReplace: true)</c>).</summary>
    public string? ProtectedReason { get; private set; }

    /// <summary>Why a save is refused now (read-only or protected), or null.</summary>
    public string? SaveBlockedReason => ReadOnlyReason ?? ProtectedReason;

    /// <summary><see cref="ReadOnlyReason"/> as data for the UI (translated there).</summary>
    public Msg? ReadOnlyReasonText { get; private set; }

    /// <summary><see cref="ProtectedReason"/> as data for the UI (translated there).</summary>
    public Msg? ProtectedReasonText { get; private set; }

    /// <summary><see cref="SaveBlockedReason"/> as data for the UI.</summary>
    public Msg? SaveBlockedReasonText => ReadOnlyReason is not null ? ReadOnlyReasonText : ProtectedReason is not null ? ProtectedReasonText : null;

    /// <summary>Loads the config to use at startup (see the class remarks). Never throws for a bad file.</summary>
    public ConfigLoad Load()
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (File.Exists(ConfigFile))
        {
            var text = ReadWithRetry(ConfigFile);
            if (text is not null)
            {
                var (cfg, issues) = Parse(text);
                if (cfg is not null && cfg.SchemaVersion > ConfigJson.CurrentSchemaVersion)
                {
                    // Written by a newer FxAuto: used as it is, read-only (never saved, never moved aside).
                    lock (_gate) _ownHash = Hash(text);
                    UpdateReadOnly(cfg);
                    return new ConfigLoad(cfg, issues, ConfigOrigin.File);
                }
                if (cfg is not null && !issues.Any(i => i.Level == IssueLevel.Error))
                {
                    lock (_gate) _ownHash = Hash(text);
                    UpdateReadOnly(cfg);
                    return new ConfigLoad(cfg, issues, ConfigOrigin.File);
                }
                // Unusable (bad JSON or validation errors): the last valid copy stays active, like a bad hand edit.
                lock (_gate) _ownHash = Hash(text);
                _diskValid = false;
                _log("config.invalid", new { file = ConfigFile, errors = Errors(issues) });
                return Fallback($"{ConfigFileName} has errors ({FirstError(issues)})", new Msg("Store.HasErrors", ConfigFileName, FirstErrorText(issues)), issues, unreadable: false);
            }
            _log("config.unreadable", new { file = ConfigFile });
            return Fallback($"{ConfigFileName} is locked or unreadable", new Msg("Store.Unreadable", ConfigFileName), [], unreadable: true);
        }

        // First run: the built-in defaults.
        var def = DefaultConfig.Create();
        SaveFirstRun(def);
        _log("config.created", new { file = ConfigFile });
        return new ConfigLoad(def, ConfigValidator.Validate(def), ConfigOrigin.Default);
    }

    /// <summary>config.json is unusable: a valid config.json.bak if there is one (saves allowed: the broken file is
    /// moved aside on the first save), else the built-in defaults in protected mode (no save until the file reads
    /// valid again or the user confirms the replacement). A file that cannot be read at all is always protected:
    /// its content may be fine.</summary>
    private ConfigLoad Fallback(string what, Msg whatText, IReadOnlyList<ConfigIssue> issues, bool unreadable)
    {
        var fromBackup = TryBackup();
        if (unreadable || fromBackup is null)
        {
            ProtectedReason = $"{what}; FxAuto does not save the configuration so as not to overwrite it. " +
                              $"Fix or unlock {ConfigFileName}, or confirm replacing it in Settings.";
            ProtectedReasonText = new Msg("Store.Protected", whatText, ConfigFileName);
        }
        var why = new ConfigIssue(IssueLevel.Error, ConfigFileName,
            $"{what}; " + (fromBackup is not null ? $"{BackupFileName} is used until it is fixed." : "the built-in defaults are used until it is fixed."))
        {
            Text = fromBackup is not null ? new Msg("Store.UsingBackup", whatText, BackupFileName) : new Msg("Store.UsingDefaults", whatText),
        };
        return fromBackup is not null
            ? new ConfigLoad(fromBackup, [why, .. issues], ConfigOrigin.Backup)
            : new ConfigLoad(DefaultConfig.Create(), [why, .. issues], ConfigOrigin.DefaultAfterError);
    }

    /// <summary>Atomic save. Keeps the previous config.json as config.json.bak only when it was valid; an invalid
    /// one is moved to config.json.invalid instead, so the last good backup survives. Throws on I/O errors, when the
    /// config is read-only, and when it is protected unless <paramref name="confirmReplace"/>.</summary>
    public void Save(FxAutoConfig c, bool confirmReplace = false)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (ReadOnlyReason is not null) throw new InvalidOperationException(ReadOnlyReason);
        if (ProtectedReason is not null && !confirmReplace) throw new InvalidOperationException(ProtectedReason);
        var json = ConfigJson.Serialize(c);
        lock (_gate)
        {
            _ownHash = Hash(json);   // before the write: the watcher may fire while File.Replace runs
            bool exists = File.Exists(ConfigFile);
            if (exists && !_diskValid)
            {
                File.Move(ConfigFile, InvalidFile, overwrite: true);
                _log("config.invalid.moved", new { file = InvalidFile });
                exists = false;
            }
            AtomicFile.WriteAllText(ConfigFile, json, exists ? BackupFile : null);
            _diskValid = true;
        }
        if (ProtectedReason is not null) _log("config.replaced", new { confirmed = true });
        ProtectedReason = null;
        _log("config.saved", new { file = ConfigFile });
    }

    // ---- runtime state ----

    public (LearnedState State, IReadOnlyList<ConfigIssue> Issues) LoadState() => LearnedStateJson.Load(StateFile);

    /// <summary>Atomic save of runtime-state.json. Returns the error message, or null.</summary>
    public string? SaveState(LearnedState s)
    {
        try
        {
            LearnedStateJson.Save(StateFile, s);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    // ---- watcher ----

    /// <summary>Reports hand edits of config.json (500 ms debounce, own writes ignored by hash). The callback runs
    /// on a timer thread: it must only enqueue.</summary>
    public void Watch(Action<ConfigFileChange> onChange)
    {
        _onChange = onChange ?? throw new ArgumentNullException(nameof(onChange));
        _debounce = new System.Threading.Timer(_ => CheckFile(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Directory, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Renamed += OnFsEvent;
        _watcher.Deleted += OnFsEvent;
        _watcher.Error += (_, e) => _log("config.watch.error", new { error = e.GetException().Message });
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        bool relevant = IsConfig(e.FullPath) || (e is RenamedEventArgs r && IsConfig(r.OldFullPath));
        if (relevant) _debounce?.Change(500, Timeout.Infinite);
    }

    private static bool IsConfig(string path) => Path.GetFileName(path).Equals(ConfigFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads config.json and reports it if it differs from what FxAuto last wrote/loaded. Public for tests.</summary>
    public ConfigFileChange? CheckFile()
    {
        ConfigFileChange? change;
        if (!File.Exists(ConfigFile))
        {
            _diskValid = true;          // nothing on disk to protect any more
            ProtectedReason = null;
            change = new ConfigFileChange(null, [ConfigIssue.Of(IssueLevel.Warning, ConfigFileName,
                "Issue.Store.02", ConfigFileName)]);
        }
        else
        {
            var text = ReadWithRetry(ConfigFile);
            if (text is null) return null;
            var hash = Hash(text);
            lock (_gate)
            {
                if (hash == _ownHash) return null;          // our own write (or no real change)
            }
            var (cfg, issues) = Parse(text);
            lock (_gate) _ownHash = hash;
            bool newer = cfg is not null && cfg.SchemaVersion > ConfigJson.CurrentSchemaVersion;
            if (cfg is not null && (newer || !issues.Any(i => i.Level == IssueLevel.Error)))
            {
                UpdateReadOnly(cfg);
                _diskValid = true;
                ProtectedReason = null;     // the file reads valid again
            }
            else
            {
                cfg = null;                 // invalid hand edit: keep the last valid one
                _diskValid = false;
            }
            change = new ConfigFileChange(cfg, issues);
        }
        _log("config.file.changed", new { usable = change.Config is not null, errors = Errors(change.Issues) });
        _onChange?.Invoke(change);
        return change;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }

    // ---- internals ----

    /// <summary>Parse + validate (ConfigJson.Parse validates). Config null only when the JSON cannot be turned into a config.</summary>
    private static (FxAutoConfig? Config, IReadOnlyList<ConfigIssue> Issues) Parse(string text) => ConfigJson.Parse(text);

    private FxAutoConfig? TryBackup()
    {
        if (!File.Exists(BackupFile)) return null;
        var text = ReadWithRetry(BackupFile);
        if (text is null) return null;
        var (cfg, issues) = Parse(text);
        return cfg is not null && !issues.Any(i => i.Level == IssueLevel.Error) ? cfg : null;
    }

    private void SaveFirstRun(FxAutoConfig c)
    {
        try { Save(c); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("config.save.error", new { error = ex.Message });
        }
    }

    private void UpdateReadOnly(FxAutoConfig cfg)
    {
        ReadOnlyReasonText = ReadOnlyText(cfg);
        ReadOnlyReason = ReadOnlyEnglish(cfg);
    }

    private static string? ReadOnlyEnglish(FxAutoConfig cfg)
        => cfg.SchemaVersion > ConfigJson.CurrentSchemaVersion
            ? $"{ConfigFileName} was written by a newer FxAuto (schemaVersion {cfg.SchemaVersion}); it is used read-only and never saved."
            : null;

    private static Msg? ReadOnlyText(FxAutoConfig cfg)
        => cfg.SchemaVersion > ConfigJson.CurrentSchemaVersion ? new Msg("Store.ReadOnly", ConfigFileName, cfg.SchemaVersion) : null;

    private static object FirstErrorText(IReadOnlyList<ConfigIssue> issues)
        => issues.FirstOrDefault(i => i.Level == IssueLevel.Error) is { } e ? new Msg("Store.ErrorAt", e.Path, (object?)e.Text ?? e.Message) : new Msg("Store.InvalidJson");

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static IReadOnlyList<string> Errors(IReadOnlyList<ConfigIssue> issues)
        => issues.Where(i => i.Level == IssueLevel.Error).Take(5).Select(i => $"{i.Path}: {i.Message}").ToList();

    private static string FirstError(IReadOnlyList<ConfigIssue> issues)
        => issues.FirstOrDefault(i => i.Level == IssueLevel.Error) is { } e ? $"{e.Path}: {e.Message}" : "invalid JSON";

    private string? ReadWithRetry(string path)
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                return sr.ReadToEnd();
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
        _log("config.read.error", new { file = path });
        return null;
    }
}
