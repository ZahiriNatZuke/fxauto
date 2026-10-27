using System.Text;

namespace FxAuto.Core.Config;

/// <summary>Atomic text-file writes for config.json and runtime-state.json: write <c>&lt;path&gt;.tmp</c>, then
/// <see cref="File.Replace(string, string, string?)"/> over the existing file (or a move when there is none), so a
/// crash never leaves a half-written file.</summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> atomically. With
    /// <paramref name="backupPath"/>, the previous file is kept there (e.g. <c>config.json.bak</c>).</summary>
    public static void WriteAllText(string path, string content, string? backupPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Utf8NoBom.GetBytes(content);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, backupPath, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Reads a text file; null when it does not exist.</summary>
    public static string? ReadAllTextOrNull(string path)
        => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
