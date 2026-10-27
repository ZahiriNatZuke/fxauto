using FxAuto.Core.Presets;

namespace FxAuto.FxSound;

/// <summary>
/// FxSound preset files (REQ-107 file part, REQ-113). User presets live in <c>%APPDATA%\FxSound\Presets\&lt;name&gt;.fac</c>,
/// built-ins in <c>&lt;FxSound folder&gt;\Factsoft\&lt;n&gt;.fac</c>; the real name is line 3 of the file, so lookups go by
/// line 3, not by file name. Writes only inside Presets\ (exact copy: <c>.tmp</c> + <c>File.Replace</c>, the
/// <c>.fac.tmp</c> leftovers are deleted on failure). Never writes Factsoft\, AutoSave\ or FxSound.settings.
/// System.IO only (no Win32): unit-tested against temporary folders.
/// </summary>
internal sealed class PresetFileStore : IPresetFileStore
{
    private const string TmpSuffix = ".tmp";

    private readonly string _dataFolder;
    private readonly Func<string?> _factsoftFolder;
    private readonly Action<string, object>? _log;

    /// <param name="dataFolder">FxSound's data folder (<c>%APPDATA%\FxSound</c>).</param>
    /// <param name="factsoftFolder">Built-in presets folder; evaluated on each lookup (the FxSound path is configurable).</param>
    public PresetFileStore(string dataFolder, Func<string?> factsoftFolder, Action<string, object>? log = null)
    {
        _dataFolder = dataFolder;
        _factsoftFolder = factsoftFolder;
        _log = log;
    }

    /// <summary>Store for the current user; Factsoft\ is next to the configured FxSound.exe.</summary>
    public static PresetFileStore ForCurrentUser(Func<string> fxSoundExePath, Action<string, object>? log = null)
        => new(DefaultDataFolder, () => FactsoftFolderOf(fxSoundExePath()), log);

    public static string DefaultDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FxSound");

    public static string? FactsoftFolderOf(string? exePath)
        => string.IsNullOrEmpty(exePath) || Path.GetDirectoryName(exePath) is not { Length: > 0 } dir
            ? null : Path.Combine(dir, "Factsoft");

    public string PresetsFolder => Path.Combine(_dataFolder, "Presets");
    public string AutoSaveFolder => Path.Combine(_dataFolder, "AutoSave");
    public string SettingsFile => Path.Combine(_dataFolder, "FxSound.settings");

    public string? FindPresetFile(string presetName)
    {
        if (string.IsNullOrEmpty(presetName)) return null;
        return FindIn(PresetsFolder, presetName) ?? FindIn(_factsoftFolder(), presetName);
    }

    public string? WriteExactCopy(string sourceFile, string newName)
    {
        if (FacFile.CheckName(newName) is string nameErr) return nameErr;
        string? tmp = null;
        try
        {
            var src = ReadAll(sourceFile);
            var data = FacFile.WithName(src, newName);
            Directory.CreateDirectory(PresetsFolder);
            // The file FxSound created for the copy (normally Presets\<newName>.fac), found by line 3.
            var dst = FindIn(PresetsFolder, newName) ?? Path.Combine(PresetsFolder, newName + ".fac");
            if (string.Equals(Path.GetFullPath(dst), Path.GetFullPath(sourceFile), StringComparison.OrdinalIgnoreCase))
                return "The source and the destination are the same file.";
            tmp = dst + TmpSuffix;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(dst)) File.Replace(tmp, dst, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else File.Move(tmp, dst);
            tmp = null;
            var back = ReadAll(dst);
            if (!back.AsSpan().SequenceEqual(data)) return $"\"{dst}\" does not hold the expected bytes after writing.";
            _log?.Invoke("presets.file.copy", new { source = sourceFile, target = dst, bytes = data.Length });
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                                       or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            _log?.Invoke("presets.file.copy.error", new { source = sourceFile, name = newName, error = ex.Message });
            return ex.Message;
        }
        finally
        {
            if (tmp is not null) DeleteTmpLeftovers(tmp);
        }
    }

    public bool AutoSaveExists(string presetName)
    {
        if (string.IsNullOrEmpty(presetName)) return false;
        var folder = AutoSaveFolder;
        try
        {
            if (!Directory.Exists(folder)) return false;
            if (FacFile.CheckName(presetName) is null && File.Exists(Path.Combine(folder, presetName + ".fac"))) return true;
            return FindIn(folder, presetName) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Names (line 3) of the presets with an AutoSave copy: they load with unsaved changes (Status warning).</summary>
    public IReadOnlyList<string> AutoSaveNames()
    {
        var result = new List<string>();
        foreach (var f in SafeFiles(AutoSaveFolder))
            if (TryReadName(f) is string n) result.Add(n);
        return result;
    }

    public int MaxUserPresets()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return FacFile.DefaultMaxUserPresets;
            using var fs = new FileStream(SettingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return FacFile.ParseMaxUserPresets(sr.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FacFile.DefaultMaxUserPresets;
        }
    }

    // Exact file name first (cheap and the usual case), then every *.fac by line 3.
    private static string? FindIn(string? folder, string name)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        if (FacFile.CheckName(name) is null)
        {
            var direct = Path.Combine(folder, name + ".fac");
            if (File.Exists(direct) && TryReadName(direct) == name) return direct;
        }
        foreach (var f in SafeFiles(folder))
            if (TryReadName(f) == name) return f;
        return null;
    }

    private static List<string> SafeFiles(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return [];
            var files = Directory.GetFiles(folder, "*.fac").Where(f => f.EndsWith(".fac", StringComparison.OrdinalIgnoreCase)).ToList();
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? TryReadName(string file)
    {
        try { return FacFile.ReadName(ReadAll(file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static byte[] ReadAll(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[fs.Length];
        fs.ReadExactly(buf);
        return buf;
    }

    // Our own temp file plus any *.fac.tmp left in Presets\ by an earlier failed write (spike outcome for 4b).
    private void DeleteTmpLeftovers(string ownTmp)
    {
        var targets = new List<string> { ownTmp };
        try
        {
            if (Directory.Exists(PresetsFolder)) targets.AddRange(Directory.GetFiles(PresetsFolder, "*.fac" + TmpSuffix));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var t in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(t)) File.Delete(t);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke("presets.file.tmp.error", new { file = t, error = ex.Message });
            }
        }
    }
}
