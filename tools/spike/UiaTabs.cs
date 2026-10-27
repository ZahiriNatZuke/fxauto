using System; using System.Collections.Generic; using System.Diagnostics; using System.Windows.Automation;
public static class UiaTabs {
  public static List<string> Read() {
    var res = new List<string>(); var w = TreeWalker.ControlViewWalker;
    var pids = new HashSet<int>(); foreach (var p in Process.GetProcessesByName("chrome")) { pids.Add(p.Id); p.Dispose(); }
    var wins = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1"));
    foreach (AutomationElement win in wins) {
      if (!pids.Contains(win.Current.ProcessId)) continue;
      var q = new Queue<KeyValuePair<AutomationElement,int>>(); q.Enqueue(new KeyValuePair<AutomationElement,int>(win, 0));
      while (q.Count > 0) { var it = q.Dequeue(); var c = w.GetFirstChild(it.Key);
        while (c != null) { var ct = c.Current.ControlType;
          if (ct == ControlType.TabItem) res.Add(c.Current.Name);
          else if (ct != ControlType.Document && it.Value < 12) q.Enqueue(new KeyValuePair<AutomationElement,int>(c, it.Value + 1));
          c = w.GetNextSibling(c); } }
    }
    return res;
  }
}
