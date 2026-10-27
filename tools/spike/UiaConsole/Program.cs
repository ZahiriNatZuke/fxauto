// Spike (b) from a minimal net10.0-windows console: read Chrome tab names via managed UIA (read-only),
// print masked names (only markers + length), memory and Chrome CPU before/after and minutes later.
// Usage: UiaConsole [reads=20] [afterMinutes=4]
using System.Diagnostics;
using System.Windows.Automation;

int reads = args.Length > 0 ? int.Parse(args[0]) : 20;
int afterMin = args.Length > 1 ? int.Parse(args[1]) : 4;
string[] markers = { "reproducción de audio", "audio silenciado", "silenciad", "Audio playing", "Audio muted", "Uso de memoria" };

static double ChromeCpu() { double s = 0; foreach (var p in Process.GetProcessesByName("chrome")) { try { s += p.TotalProcessorTime.TotalSeconds; } catch { } p.Dispose(); } return s; }
static double ChromePrivMb() { long s = 0; foreach (var p in Process.GetProcessesByName("chrome")) { try { s += p.PrivateMemorySize64; } catch { } p.Dispose(); } return s / 1048576.0; }
static string Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); using var me = Process.GetCurrentProcess(); return $"private MB={me.PrivateMemorySize64 / 1048576.0:F1} WS MB={me.WorkingSet64 / 1048576.0:F1} managed MB={GC.GetTotalMemory(true) / 1048576.0:F1}"; }
void CpuWindow(string label, int seconds) { var a = ChromeCpu(); var sw = Stopwatch.StartNew(); Thread.Sleep(seconds * 1000); var b = ChromeCpu(); Console.WriteLine($"{DateTime.Now:HH:mm:ss} chrome CPU {label}: {(b - a) / sw.Elapsed.TotalSeconds:F3} cpu-s/s over {seconds}s, chrome private MB={ChromePrivMb():F0}"); }

static List<string> Read() {
  var res = new List<string>(); var w = TreeWalker.ControlViewWalker;
  var pids = new HashSet<int>(); foreach (var p in Process.GetProcessesByName("chrome")) { pids.Add(p.Id); p.Dispose(); }
  var wins = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1"));
  foreach (AutomationElement win in wins) {
    if (!pids.Contains(win.Current.ProcessId)) continue;
    var q = new Queue<(AutomationElement, int)>(); q.Enqueue((win, 0));
    while (q.Count > 0) { var (el, d) = q.Dequeue(); var c = w.GetFirstChild(el);
      while (c != null) { var ct = c.Current.ControlType;
        if (ct == ControlType.TabItem) res.Add(c.Current.Name);
        else if (ct != ControlType.Document && d < 12) q.Enqueue((c, d + 1));
        c = w.GetNextSibling(c); } }
  }
  return res;
}

Console.WriteLine("start: " + Mem());
CpuWindow("baseline (before read)", 30);
var sw = Stopwatch.StartNew(); var tabs = Read(); Console.WriteLine($"first read ms={sw.Elapsed.TotalMilliseconds:F1} tabs={tabs.Count}");
foreach (var t in tabs) Console.WriteLine($"  tab len={t.Length} markers=[{string.Join(",", markers.Where(m => t.Contains(m, StringComparison.OrdinalIgnoreCase)))}]");
Console.WriteLine("after first read: " + Mem());
var times = new List<double>();
for (int i = 0; i < reads; i++) { sw.Restart(); Read(); times.Add(sw.Elapsed.TotalMilliseconds); }
times.Sort(); Console.WriteLine($"{reads} reads ms: min={times[0]:F1} median={times[times.Count / 2]:F1} max={times[^1]:F1}");
Console.WriteLine($"after {reads + 1} reads: " + Mem());
for (int m = 0; m <= afterMin; m += 2) { if (m > 0) Thread.Sleep(90_000); CpuWindow($"+{m} min after last read", 30); }
Console.WriteLine("end: " + Mem());
