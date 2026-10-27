using System.Globalization;
using FxAuto.Core.Devices;
using FxAuto.Core.FxSound;
using FxAuto.Devices;
using FxAuto.FxSound;

namespace FxAuto.Devices
{
    /// <summary>Copy of the contract interface (the real one lives in src/FxAuto/App/Contracts.cs, which drags in
    /// the whole App layer).</summary>
    internal interface IDeviceMonitor : IDisposable
    {
        event Action<EndpointSnapshot>? Changed;
        EndpointSnapshot Snapshot();
        void Start();
    }
}

namespace FxAuto.ProbeDevices
{
    /// <summary>Usage: probe-devices [watchSeconds]. Prints the first snapshot, then every debounced snapshot raised
    /// while watching (0 = do not watch).</summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            int seconds = args.Length > 0 && int.TryParse(args[0], CultureInfo.InvariantCulture, out int s) ? s : 0;
            using var monitor = new DeviceMonitor((ev, data) => Console.WriteLine($"  [log] {ev}"));
            monitor.Start();
            var snap = monitor.Snapshot();
            Print("initial", snap);

            var (json, written) = ReadStatus();
            var st = json is null ? null : FxStatusParser.TryParse(json, written ?? DateTimeOffset.Now);
            bool fxRunning = System.Diagnostics.Process.GetProcessesByName("FxSound").Length > 0;
            var eff = OutputResolver.Resolve(st, fxRunning, snap);
            Console.WriteLine($"FxSound running={fxRunning} power={st?.Power} selected_output=\"{st?.SelectedOutput}\" " +
                              $"output_devices=[{string.Join(" | ", st?.OutputDevices ?? [])}]");
            Console.WriteLine($"Effective: throughFxSound={eff.ThroughFxSound} endpoint=\"{eff.Endpoint?.FriendlyName}\" " +
                              $"requery={eff.RequeryStatus} why=\"{eff.Why}\"");

            var store = PresetFileStore.ForCurrentUser(() => @"C:\Program Files\FxSound LLC\FxSound\FxSound.exe");
            Console.WriteLine($"Presets: max_user_presets={store.MaxUserPresets()} autosave=[{string.Join(", ", store.AutoSaveNames())}]");
            foreach (var p in (st?.UserPresets ?? []).Concat(st?.BuiltInPresets ?? []))
            {
                var f = store.FindPresetFile(p);
                Console.WriteLine($"  {p,-22} -> {(f is null ? "NOT FOUND" : f)}{(store.AutoSaveExists(p) ? "  [AutoSave]" : "")}");
            }

            if (seconds > 0)
            {
                int raised = 0;
                monitor.Changed += e => { raised++; Print($"changed #{raised}", e); };
                Console.WriteLine($"Watching {seconds} s (read-only)...");
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                Console.WriteLine($"Callbacks={monitor.CallbackCount} snapshots raised={raised}");
            }
            return 0;
        }

        private static (string? Json, DateTimeOffset? Written) ReadStatus()
        {
            var path = Path.Combine(PresetFileStore.DefaultDataFolder, "status.json");
            if (!File.Exists(path)) return (null, null);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return (sr.ReadToEnd(), File.GetLastWriteTime(path));
        }

        private static void Print(string what, EndpointSnapshot s)
        {
            Console.WriteLine($"== {what} at {s.At:HH:mm:ss.fff}: {s.Endpoints.Count} endpoints");
            Console.WriteLine($"   default multimedia = {Name(s, s.DefaultMultimediaId)}");
            Console.WriteLine($"   default console    = {Name(s, s.DefaultConsoleId)}");
            foreach (var e in s.Endpoints)
            {
                Console.WriteLine($"   - \"{e.FriendlyName}\" state={e.State}{(e.IsFxSoundVirtual ? " [FxSound virtual]" : "")}");
                Console.WriteLine($"       id={e.EndpointId}");
                Console.WriteLine($"       desc=\"{e.EndpointDesc}\" adapter=\"{e.Adapter}\" formFactor={e.FormFactor} bus={e.Bus} container={e.ContainerId ?? "-"}");
            }
        }

        private static string Name(EndpointSnapshot s, string? id)
            => id is null ? "(none)" : s.Endpoints.FirstOrDefault(e => e.EndpointId == id)?.FriendlyName ?? id;
    }
}
