namespace FxAuto.Core;

/// <summary>Foreground window already identified by the Windows layer.</summary>
public sealed record ForegroundInfo(nint Hwnd, string? Exe, string? FullPath, string? Title, string? Aumid,
                                    int Pid, DateTimeOffset? ProcessStart, bool Unreadable);

/// <summary>Working mode of the watcher. Editing = REQ-107 "Edit in FxSound" (engine paused on a preset).</summary>
public enum Mode { Auto, Locked, Paused, Editing }

/// <summary>Injectable clock (tests use a fake one).</summary>
public interface IClock { DateTimeOffset Now { get; } }

/// <summary>Real clock.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset Now => DateTimeOffset.Now;
}
