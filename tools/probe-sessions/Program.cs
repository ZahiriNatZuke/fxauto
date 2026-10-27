using System.Diagnostics;
using System.Globalization;
using FxAuto.Audio;
using FxAuto.Core.Audio;

namespace FxAuto.Audio
{
    /// <summary>Copy of the contract interface (the real one lives in src/FxAuto/App/Contracts.cs, which drags in
    /// the whole App layer).</summary>
    internal interface IAudioSessionMonitor : IDisposable
    {
        event Action<AudioSnapshot>? Sampled;
        void Start();
        void Refresh();
    }
}

namespace FxAuto.ProbeSessions
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            int seconds = args.Length > 0 && int.TryParse(args[0], CultureInfo.InvariantCulture, out int s) ? s : 20;
            bool quiet = args.Contains("--quiet");
            var inv = CultureInfo.InvariantCulture;
            var me = Process.GetCurrentProcess();
            var tree = new ProcessTree();
            var lockObj = new object();
            int count = 0;
            float maxChrome = 0;
            using var monitor = new AudioSessionMonitor(tree, 250, log: (e, d) => Console.WriteLine($"LOG {e} {d}"));
            monitor.Sampled += snap =>
            {
                lock (lockObj)
                {
                    count++;
                    foreach (var x in snap.Sessions)
                        if (string.Equals(x.RootExe, "chrome.exe", StringComparison.OrdinalIgnoreCase)) maxChrome = Math.Max(maxChrome, x.Peak);
                    if (quiet && count % 20 != 1) return;
                    Console.WriteLine(string.Create(inv, $"[{snap.At:HH:mm:ss.fff}] #{count} fx={snap.FxSoundEndpointId} render={snap.FxSoundRenderEndpointId} sessions={snap.Sessions.Count}"));
                    foreach (var x in snap.Sessions)
                        Console.WriteLine(string.Create(inv,
                            $"    {(x.EndpointId == snap.FxSoundEndpointId ? "FX " : "   ")}{x.EndpointId} pid={x.Pid} root={x.RootPid} {x.RootExe} active={x.Active} peak={x.Peak:F3} aumid={x.RootAumid}"));
                }
            };
            me.Refresh();
            var cpu0 = me.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            monitor.Start();
            Thread.Sleep(TimeSpan.FromSeconds(1));
            me.Refresh();
            var cpu1 = me.TotalProcessorTime;      // after start-up (enumeration, JIT)
            var t1 = sw.Elapsed;
            // CPU per 10 s window, to separate JIT tier-up from steady state.
            var prevCpu = cpu1; var prevT = t1;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(10, Math.Max(0.1, seconds - sw.Elapsed.TotalSeconds))));
                me.Refresh();
                var c = me.TotalProcessorTime; var t = sw.Elapsed;
                Console.WriteLine(string.Create(inv, $"WINDOW {prevT.TotalSeconds:F0}-{t.TotalSeconds:F0} s: {(c - prevCpu).TotalMilliseconds:F0} ms = {(c - prevCpu).TotalMilliseconds / (t - prevT).TotalMilliseconds * 100:F3} % of one core"));
                prevCpu = c; prevT = t;
            }
            me.Refresh();
            var cpu2 = me.TotalProcessorTime;
            var t2 = sw.Elapsed;
            long priv = me.PrivateMemorySize64, ws = me.WorkingSet64;
            monitor.Dispose();
            double steady = (cpu2 - cpu1).TotalMilliseconds / (t2 - t1).TotalMilliseconds * 100;
            Console.WriteLine(string.Create(inv,
                $"SUMMARY seconds={t2.TotalSeconds:F1} snapshots={count} wakes={monitor.WakeCount} treeSnapshots={tree.SnapshotCount} maxChromePeak={maxChrome:F3} lastError={monitor.LastError ?? "none"}"));
            Console.WriteLine(string.Create(inv,
                $"CPU startup={(cpu1 - cpu0).TotalMilliseconds:F0} ms; steady={(cpu2 - cpu1).TotalMilliseconds:F0} ms over {(t2 - t1).TotalSeconds:F1} s = {steady:F3} % of one core (whole probe process)"));
            Console.WriteLine(string.Create(inv, $"MEMORY private={priv / 1048576.0:F1} MB workingSet={ws / 1048576.0:F1} MB"));
            return 0;
        }
    }
}
