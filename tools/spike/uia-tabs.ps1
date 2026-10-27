# Spike (b): read-only enumeration of Chrome tabs through UI Automation (managed UIAutomationClient).
# Windows PowerShell 5.1:  powershell -NoProfile -File uia-tabs.ps1 [-Passes 3]
# Never clicks, never changes focus or tab. Skips web content (Document subtrees) so the page
# accessibility tree is not walked.
param([int]$Passes = 3, [string]$ProcessName = 'chrome')
$ErrorActionPreference = 'Stop'

function ChromeStats {
  $ps = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
  [pscustomobject]@{
    Count = @($ps).Count
    CpuS = (($ps | ForEach-Object { $_.TotalProcessorTime.TotalSeconds }) | Measure-Object -Sum).Sum
    PrivMB = (($ps | ForEach-Object { $_.PrivateMemorySize64 }) | Measure-Object -Sum).Sum / 1MB
  }
}

$me = Get-Process -Id $PID
$memBefore = $me.PrivateMemorySize64 / 1MB
$c0 = ChromeStats
$swLoad = [Diagnostics.Stopwatch]::StartNew()
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$swLoad.Stop()
$A = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

function Get-ChromeWindows {
  $pids = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
  $cond = New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'Chrome_WidgetWin_1')
  $wins = $A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
  foreach ($w in $wins) { if ($pids -contains $w.Current.ProcessId -and $w.Current.Name) { $w } }
}

function Get-Tabs($win) {
  # BFS on the control view, not entering Document (web content) subtrees; depth cap 12.
  $res = New-Object System.Collections.Generic.List[object]
  $q = New-Object System.Collections.Generic.Queue[object]
  $q.Enqueue(@($win, 0)); $visited = 0
  while ($q.Count -gt 0) {
    $item = $q.Dequeue(); $el = $item[0]; $d = $item[1]
    $c = $walker.GetFirstChild($el)
    while ($c -ne $null) {
      $visited++
      $ct = $c.Current.ControlType
      if ($ct -eq $CT::TabItem) { $res.Add($c) }
      elseif ($ct -ne $CT::Document -and $d -lt 12) { $q.Enqueue(@($c, $d + 1)) }
      $c = $walker.GetNextSibling($c)
    }
  }
  return ,@($res, $visited)
}

for ($p = 1; $p -le $Passes; $p++) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $wins = @(Get-ChromeWindows)
  $out = @()
  $visitedTotal = 0
  foreach ($w in $wins) {
    $r = Get-Tabs $w; $tabs = $r[0]; $visitedTotal += $r[1]
    $out += "WINDOW pid=$($w.Current.ProcessId) hwnd=0x$('{0:X}' -f $w.Current.NativeWindowHandle) offscreen=$($w.Current.IsOffscreen) name='$($w.Current.Name)'"
    foreach ($t in $tabs) {
      $cur = $t.Current
      $sel = $null
      try { $sel = $t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch {}
      $out += "  TAB selected=$sel name='$($cur.Name)' help='$($cur.HelpText)' itemStatus='$($cur.ItemStatus)' acc='$($cur.AccessKey)' aid='$($cur.AutomationId)' class='$($cur.ClassName)'"
    }
  }
  $sw.Stop()
  "PASS $p : windows=$($wins.Count) visited=$visitedTotal ms=$($sw.Elapsed.TotalMilliseconds.ToString('F1'))"
  if ($p -eq 1 -or $p -eq $Passes) { $out }
}
$me.Refresh()
$c1 = ChromeStats
"UIAutomationClient load ms=$($swLoad.Elapsed.TotalMilliseconds.ToString('F0'))"
"pwsh private MB before=$($memBefore.ToString('F1')) after=$(($me.PrivateMemorySize64/1MB).ToString('F1'))"
"chrome procs before=$($c0.Count) after=$($c1.Count) cpuS delta=$(($c1.CpuS - $c0.CpuS).ToString('F2')) privMB before=$($c0.PrivMB.ToString('F0')) after=$($c1.PrivMB.ToString('F0'))"
