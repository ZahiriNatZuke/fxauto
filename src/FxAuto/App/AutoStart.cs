using Microsoft.Win32;

namespace FxAuto.App;

/// <summary>Start with Windows via HKCU\...\Run (no administrator) and detection of FxSound's own autostart.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FxAuto";

    public static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) k.SetValue(ValueName, Command, RegistryValueKind.String);
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Does FxSound start by itself with Windows? (HKCU/HKLM Run, also WOW6432Node, or a Startup folder shortcut).
    /// Only then does the --autostart grace period make sense.
    /// </summary>
    public static bool FxSoundHasAutostart()
    {
        try
        {
            foreach (var (hive, path) in new[]
                     {
                         (Registry.CurrentUser, RunKey),
                         (Registry.LocalMachine, RunKey),
                         (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
                     })
            {
                using var k = hive.OpenSubKey(path);
                if (k is null) continue;
                foreach (var name in k.GetValueNames())
                {
                    if (k.GetValue(name) is string v && v.Contains("FxSound.exe", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            foreach (var folder in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                     })
            {
                if (folder.Length == 0 || !Directory.Exists(folder)) continue;
                if (Directory.EnumerateFiles(folder).Any(f => Path.GetFileName(f).Contains("FxSound", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return false;
    }
}
