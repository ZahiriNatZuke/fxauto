# Spike (c) failure cases on a BUILT-IN source: cut the duplicate after set_effect (before save_preset) and recover.
# Case 1: cut, recover at once with --undo_preset.  Case 2: cut, then another --preset happens (engine race) -> AutoSave appears? -> recover.
param([string]$Source = 'Music', [Parameter(Mandatory)][string]$BackupDir)
$ErrorActionPreference = 'Stop'
$exe = 'C:\Program Files\FxSound LLC\FxSound\FxSound.exe'
$root = Join-Path $env:APPDATA 'FxSound'; $autosave = Join-Path $root 'AutoSave'; $statusFile = Join-Path $root 'status.json'
$log = New-Object System.Collections.Generic.List[string]
function Log($m) { $line = "[{0:HH:mm:ss.fff}] {1}" -f (Get-Date), $m; $log.Add($line); Write-Output $line }
function Fx([string]$a) { $psi = [Diagnostics.ProcessStartInfo]::new($exe); $psi.Arguments = "$a --run_minimized"; $psi.UseShellExecute = $false; $p = [Diagnostics.Process]::Start($psi); $null = $p.WaitForExit(10000); Log "FX $a exit=$($p.ExitCode)" }
function Status { $b = (Get-Item $statusFile).LastWriteTimeUtc; Fx '--status'; for ($i = 0; $i -lt 20 -and (Get-Item $statusFile).LastWriteTimeUtc -eq $b; $i++) { Start-Sleep -Milliseconds 50 }; Get-Content $statusFile -Raw | ConvertFrom-Json }
function Mod($s, $n) { @($s.presets.built_in + $s.presets.user_defined | Where-Object { $_.name -eq $n })[0].modified }
function AS { @(Get-ChildItem $autosave -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join ',' }
$fails = 0; function Check($c, $w) { if ($c) { Log "OK   $w" } else { Log "FAIL $w"; $script:fails++ } }

New-Item -ItemType Directory -Force $BackupDir | Out-Null
Copy-Item (Join-Path $root 'Presets') (Join-Path $BackupDir 'Presets') -Recurse -Force
Copy-Item $autosave (Join-Path $BackupDir 'AutoSave') -Recurse -Force
Copy-Item (Join-Path $root 'FxSound.settings') $BackupDir -Force
$s0 = Status; $orig = $s0.selected_preset; Log "active before='$orig' autosave=[$(AS)]"
try {
  # Case 1
  Fx "--preset=`"$Source`""; $s = Status; $cl = $s.effects.clarity; $bands0 = ($s.equalizer.bands | % { $_.gain }) -join ','
  Fx "--set_effect=clarity:$cl"; $s = Status; Check (Mod $s $Source) "case1: $Source modified=true after set_effect (cut here)"
  Fx '--undo_preset'; $s = Status
  Check (-not (Mod $s $Source)) "case1: $Source modified=false after undo_preset"
  Check ((AS) -eq '') "case1: AutoSave empty [$(AS)]"
  Check ((($s.equalizer.bands | % { $_.gain }) -join ',') -eq $bands0 -and $s.effects.clarity -eq $cl) "case1: sound equal to saved version"
  # Case 2: cut, then a different preset is applied before recovery (engine race)
  Fx "--set_effect=clarity:$cl"; $s = Status; Check (Mod $s $Source) "case2: modified=true"
  Fx "--preset=`"$orig`""; Start-Sleep -Milliseconds 300
  Log "case2: after switching away, AutoSave=[$(AS)]"
  $s = Status; Log "case2: $Source modified flag after switching away = $(Mod $s $Source)"
  Fx "--preset=`"$Source`""; Fx '--undo_preset'; $s = Status
  Check (-not (Mod $s $Source)) "case2: modified=false after --preset + --undo_preset"
  Check ((AS) -eq '') "case2: AutoSave empty after recovery [$(AS)]"
}
catch { Log "ERROR $_"; $fails++ }
finally {
  Fx "--preset=`"$orig`""; $e = Status
  Check ($e.selected_preset -eq $orig) "active restored '$orig'"
  Check (@($e.presets.built_in + $e.presets.user_defined | ? { $_.modified }).Count -eq 0) "no preset modified"
  Check ((AS) -eq '') "AutoSave empty"
  Log "FAILS=$fails"; $log | Set-Content (Join-Path $BackupDir 'fail-recover.log') -Encoding utf8
}
