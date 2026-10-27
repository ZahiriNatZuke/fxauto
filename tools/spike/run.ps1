# Spike (a): session peaks per process on every active render endpoint.
# Usage: pwsh -File run.ps1 [-Seconds 60] [-IntervalMs 250]
param([int]$Seconds = 60, [int]$IntervalMs = 250)
Add-Type -Path "$PSScriptRoot\Sessions.cs"
$me = Get-Process -Id $PID
$cpu0 = $me.TotalProcessorTime; $sw = [Diagnostics.Stopwatch]::StartNew()
$out = [SessionProbe]::Run($Seconds * 1000, $IntervalMs)
$sw.Stop(); $me.Refresh()
$out
"WALL s={0:F1}  CPU of this pwsh during run ms={1:F0}  WorkingSet MB={2:F1}  Private MB={3:F1}" -f $sw.Elapsed.TotalSeconds, ($me.TotalProcessorTime - $cpu0).TotalMilliseconds, ($me.WorkingSet64/1MB), ($me.PrivateMemorySize64/1MB)
# Map PIDs of chrome.exe sessions to their role (browser / utility audio)
Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -notmatch '--type=' -or $_.CommandLine -match 'audio' } |
  ForEach-Object { "chrome pid={0} parent={1} role={2}" -f $_.ProcessId, $_.ParentProcessId, $(if ($_.CommandLine -match '--utility-sub-type=([^\s"]+)') { $Matches[1] } elseif ($_.CommandLine -notmatch '--type=') { 'browser' } else { 'other' }) }
