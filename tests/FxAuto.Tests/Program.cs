using System.Diagnostics;

namespace FxAuto.Tests;

/// <summary>Skipped test (does not count as a failure).</summary>
internal sealed class SkipException(string reason) : Exception(reason);

internal sealed class AssertException(string message) : Exception(message);

internal static class Assert
{
    public static void True(bool cond, string what)
    {
        if (!cond) throw new AssertException(what);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"{what}: expected \"{expected}\" but got \"{actual}\"");
    }

    public static void Null(object? o, string what)
    {
        if (o is not null) throw new AssertException($"{what}: expected null but got \"{o}\"");
    }

    public static T NotNull<T>(T? o, string what) where T : class
        => o ?? throw new AssertException($"{what}: got null");
}

/// <summary>Mini-runner: each test is a name and a function; exits with a non-zero code if any fails.</summary>
internal static class Program
{
    private static readonly List<(string Name, Func<Task> Body)> Tests = [];

    public static void Add(string name, Action body) => Tests.Add((name, () => { body(); return Task.CompletedTask; }));
    public static void Add(string name, Func<Task> body) => Tests.Add((name, body));

    private static async Task<int> Main()
    {
        CommandAndParserTests.Register();
        ControllerTests.Register();
        ClassifierTests.Register();
        SoundingTests.Register();
        ArbiterTests.Register();
        AudioLayerTests.Register();
        ConfigTests.Register();
        DeviceTests.Register();
        PresetTests.Register();
        PresetOpsTests.Register();
        BrowserTabReaderTests.Register();
        DeviceAndPresetFileTests.Register();
        CoordinatorLogicTests.Register();
        CpuFixTests.Register();
        UpdateTests.Register();
        LocalizationTests.Register();

        int ok = 0, fail = 0, skip = 0;
        var total = Stopwatch.StartNew();
        foreach (var (name, body) in Tests)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var task = body();
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) != task)
                    throw new AssertException("30 s timeout reached");
                await task.ConfigureAwait(false);
                ok++;
                Console.WriteLine($"  OK       {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (SkipException ex)
            {
                skip++;
                Console.WriteLine($"  SKIPPED  {name}: {ex.Message}");
            }
#pragma warning disable CA1031 // the runner reports any exception as a failure
            catch (Exception ex)
#pragma warning restore CA1031
            {
                fail++;
                Console.WriteLine($"  FAILED   {name}: {ex.GetType().Name}: {ex.Message}");
                if (ex is not AssertException) Console.WriteLine(ex.StackTrace);
            }
        }
        Console.WriteLine();
        Console.WriteLine($"Result: {ok} passed, {fail} failed, {skip} skipped, {Tests.Count} total ({total.ElapsedMilliseconds} ms).");
        return fail == 0 ? 0 : 1;
    }
}
