$me = Get-Process -Id $PID
function M($t){ $me.Refresh(); "{0}: private MB={1:F1} WS MB={2:F1}" -f $t, ($me.PrivateMemorySize64/1MB), ($me.WorkingSet64/1MB) }
function C { ((Get-Process chrome|%{$_.TotalProcessorTime.TotalSeconds})|Measure -Sum).Sum }
M 'start'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
M 'after Add-Type'
$c0 = C; $sw=[Diagnostics.Stopwatch]::StartNew()
$out = & "$PSScriptRoot\uia-tabs.ps1" -Passes 20
$sw.Stop(); $c1 = C
M 'after 20 passes'
"chrome cpu during 20 passes: {0:F2} s over wall {1:F2} s => {2:F2} cpu-s/s (baseline measured separately)" -f ($c1-$c0), $sw.Elapsed.TotalSeconds, (($c1-$c0)/$sw.Elapsed.TotalSeconds)
$out | Where-Object { $_ -like 'PASS*' } | Select-Object -Last 5
$out | Where-Object { $_ -like '*TAB*' } | Select-Object -Last 2 | ForEach-Object { $_; ($_.ToCharArray() | Where-Object { [int]$_ -gt 127 } | ForEach-Object { 'U+{0:X4}' -f [int]$_ }) -join ' ' }
