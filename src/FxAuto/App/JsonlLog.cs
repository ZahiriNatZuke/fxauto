using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using FxAuto.Core;

namespace FxAuto.App;

/// <summary>
/// Daily JSONL log in %LOCALAPPDATA%\FxAuto. A single writer (queue): any thread may call
/// <see cref="Log"/>. Disk errors are swallowed: logging never brings the app down.
/// </summary>
internal sealed class JsonlLog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private string? _lastRotationDay;

    public string Directory { get; }

    public JsonlLog(string directory)
    {
        Directory = directory;
        try { System.IO.Directory.CreateDirectory(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _writer = Task.Run(WriteLoopAsync);
    }

    public string TodayFile => Path.Combine(Directory, LogPolicy.FileName(DateTimeOffset.Now));

    /// <summary>Appends a line {"t":..., "ev":..., "data":...}.</summary>
    public void Log(string ev, object? data = null)
    {
        string line;
        try
        {
            line = JsonSerializer.Serialize(new LogLine(DateTimeOffset.Now.ToString("o"), ev, data), JsonOptions);
        }
        catch (NotSupportedException ex)
        {
            line = JsonSerializer.Serialize(new LogLine(DateTimeOffset.Now.ToString("o"), ev, ex.Message), JsonOptions);
        }
        _queue.Writer.TryWrite(line);
    }

    private sealed record LogLine(string T, string Ev, object? Data);

    // The file stays open between batches (CPU: opening and closing it per batch went through the antivirus filters
    // every time); it is reopened when the day changes or after an error. FileShare.ReadWrite | Delete keeps
    // "Open log" and the 7-day cleanup working while it is open.
    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        string? openPath = null;
        StreamWriter? writer = null;
        long checkedAt = 0;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var now = DateTimeOffset.Now;
                string path = Path.Combine(Directory, LogPolicy.FileName(now));
                Rotate(now);
                try
                {
                    // The open file may have been deleted or moved (e.g. by hand): checked every minute, then reopened.
                    long nowMs = Environment.TickCount64;
                    if (writer is not null && nowMs - checkedAt >= ExistsCheckMs)
                    {
                        checkedAt = nowMs;
                        if (!File.Exists(openPath)) Close(ref writer);
                    }
                    if (writer is null || openPath != path)
                    {
                        checkedAt = nowMs;
                        Close(ref writer);
                        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        writer = new StreamWriter(stream);
                        openPath = path;
                    }
                    while (reader.TryRead(out var line))
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Disk full, file locked...: that batch is lost and logging goes on (the file is reopened).
                    Close(ref writer);
                    while (reader.TryRead(out _)) { }
                }
                catch (UnauthorizedAccessException)
                {
                    Close(ref writer);
                    while (reader.TryRead(out _)) { }
                }
            }
        }
        finally
        {
            Close(ref writer);
        }
    }

    private const long ExistsCheckMs = 60_000;

    private static void Close(ref StreamWriter? writer)
    {
        if (writer is null) return;
        try { writer.Dispose(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ObjectDisposedException) { }
        writer = null;
    }

    // Deletes files older than 7 days, once a day.
    private void Rotate(DateTimeOffset now)
    {
        string day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (day == _lastRotationDay) return;
        _lastRotationDay = day;
        try
        {
            var files = System.IO.Directory.GetFiles(Directory, "fxauto-*.jsonl");
            foreach (var f in LogPolicy.Expired(files, now))
            {
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Flushes the queue (2 s cap) and closes.</summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}
