$me = Get-Process -Id $PID
function M($t){ [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect(); $me.Refresh(); "{0}: private MB={1:F1} managed MB={2:F1}" -f $t, ($me.PrivateMemorySize64/1MB), ([GC]::GetTotalMemory($true)/1MB) }
Add-Type -Path (Join-Path $PSScriptRoot 'UiaTabs.cs') -ReferencedAssemblies UIAutomationClient, UIAutomationTypes, WindowsBase
M 'compiled, UIA not used'
$sw=[Diagnostics.Stopwatch]::StartNew(); $r=[UiaTabs]::Read(); "first read ms={0:F1} tabs={1}" -f $sw.Elapsed.TotalMilliseconds, $r.Count
M 'after first read'
$t=@(); for($i=0;$i -lt 200;$i++){ $sw.Restart(); $null=[UiaTabs]::Read(); $t+=$sw.Elapsed.TotalMilliseconds }
"200 reads ms: min={0:F1} median={1:F1} max={2:F1}" -f ($t|Measure -Min).Minimum, ($t|Sort)[100], ($t|Measure -Max).Maximum
M 'after 201 reads'
