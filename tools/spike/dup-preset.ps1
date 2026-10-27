# Spike (c): duplicate a preset X -> Y with research 03 §3.3 A, then rename and delete Y, restore the active preset.
# pwsh -NoProfile -File dup-preset.ps1 -Source G435_General -Copy "Spike - Test" -BackupDir <dir>
# Pause FxAuto first (FxAuto.exe --exit). Never closes FxSound. No --output.
param(
  [string]$Source = 'G435_General',
  [string]$Copy = 'Spike - Test',
  [string]$Renamed = 'Spike - Renamed',
  [Parameter(Mandatory)][string]$BackupDir
)
$ErrorActionPreference = 'Stop'
$exe = 'C:\Program Files\FxSound LLC\FxSound\FxSound.exe'
$root = Join-Path $env:APPDATA 'FxSound'
$presets = Join-Path $root 'Presets'
$autosave = Join-Path $root 'AutoSave'
$statusFile = Join-Path $root 'status.json'
$log = New-Object System.Collections.Generic.List[string]
function Log($m) { $line = "[{0:HH:mm:ss.fff}] {1}" -f (Get-Date), $m; $log.Add($line); Write-Output $line }

function Fx([string]$arguments) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $psi = [Diagnostics.ProcessStartInfo]::new($exe)
  $psi.Arguments = "$arguments --run_minimized"; $psi.UseShellExecute = $false
  $p = [Diagnostics.Process]::Start($psi)
  if (-not $p.WaitForExit(10000)) { throw "FxSound emitter did not exit: $arguments" }
  Log ("FX {0}  exit={1} ms={2}" -f $arguments, $p.ExitCode, $sw.ElapsedMilliseconds)
}
function Status {
  $before = (Get-Item $statusFile).LastWriteTimeUtc
  Fx '--status'
  for ($i = 0; $i -lt 20 -and (Get-Item $statusFile).LastWriteTimeUtc -eq $before; $i++) { Start-Sleep -Milliseconds 50 }
  Get-Content $statusFile -Raw | ConvertFrom-Json
}
function PresetInfo($s, $name) { @($s.presets.built_in + $s.presets.user_defined | Where-Object { $_.name -eq $name })[0] }
function Sound($s) { [pscustomobject]@{ bands = ($s.equalizer.bands | ForEach-Object { $_.gain }) -join ','; freqs = ($s.equalizer.bands | ForEach-Object { $_.frequency }) -join ','; master = $s.equalizer.master_gain; effects = ($s.effects | ConvertTo-Json -Compress) } }
function Hashes { Get-ChildItem $presets -Filter *.fac | Sort-Object Name | ForEach-Object { "{0} {1}" -f $_.Name, (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 16) } }
function AutoSaveList { @(Get-ChildItem $autosave -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) }
function Check($cond, $what) { if ($cond) { Log "OK   $what" } else { Log "FAIL $what"; $script:fails++ } }
$fails = 0

# 0. Backup + baseline
New-Item -ItemType Directory -Force $BackupDir | Out-Null
Copy-Item $presets (Join-Path $BackupDir 'Presets') -Recurse -Force
Copy-Item (Join-Path $root 'FxSound.settings') $BackupDir -Force
Copy-Item $autosave (Join-Path $BackupDir 'AutoSave') -Recurse -Force
Log "backup -> $BackupDir"
$hash0 = Hashes
$s0 = Status
$orig = $s0.selected_preset
$list0 = (@($s0.presets.user_defined | ForEach-Object { "$($_.name):$($_.modified)" })) -join '|'
Log "active preset before = '$orig'; volume_leveling=$($s0.equalizer.volume_leveling); power=$($s0.power); autosave=[$((AutoSaveList) -join ',')]"
Check ($s0.power) 'power on'
Check (-not (PresetInfo $s0 $Source).modified) "$Source modified=false at start"
Check (-not (PresetInfo $s0 $Copy)) "$Copy does not exist yet"

try {
  # 2. select X
  Fx "--preset=`"$Source`""
  $s2 = Status; $sx = Sound $s2
  Check ($s2.selected_preset -eq $Source) "selected == $Source"
  $clar = $s2.effects.clarity
  Log "clarity of $Source = $clar ; effects=$($sx.effects)"
  # 3. mark modified
  Fx "--set_effect=clarity:$clar"
  $s3 = Status
  Check ((PresetInfo $s3 $Source).modified) "$Source modified=true after set_effect"
  # 4. save as Y
  Fx "--save_preset=`"$Copy`""
  $s4 = Status
  Check ([bool](PresetInfo $s4 $Copy)) "$Copy in user_defined"
  Check ($s4.selected_preset -eq $Copy) "selected == $Copy"
  Check (-not (PresetInfo $s4 $Source).modified) "$Source modified=false after save_preset"
  Check (-not ((AutoSaveList) -contains "$Source.fac")) "no AutoSave\$Source.fac"
  # 5. exact copy: compare CLI-saved file vs source, then rewrite from source with line 3 replaced
  $srcFile = @(Get-ChildItem $presets -Filter *.fac) + @(Get-ChildItem "C:/Program Files/FxSound LLC/FxSound/Factsoft" -Filter *.fac) | Where-Object { (Get-Content $_.FullName -TotalCount 3)[2] -eq $Source } | Select-Object -First 1
  $dstPath = Join-Path $presets "$Copy.fac"
  Check (Test-Path $dstPath) "file '$Copy.fac' exists"
  $srcLines = [IO.File]::ReadAllText($srcFile.FullName) -split "`r`n"
  $dstLines = [IO.File]::ReadAllText($dstPath) -split "`r`n"
  $diffs = for ($i = 0; $i -lt [Math]::Max($srcLines.Count, $dstLines.Count); $i++) { if ($srcLines[$i] -ne $dstLines[$i]) { "    line $($i+1): src='$($srcLines[$i])' cli='$($dstLines[$i])'" } }
  Log "CLI-saved file vs source: lines src=$($srcLines.Count) cli=$($dstLines.Count) differing=$(@($diffs).Count)"
  $diffs | Select-Object -First 15 | ForEach-Object { Log $_ }
  $bytes = [IO.File]::ReadAllBytes($srcFile.FullName)
  Log ("source encoding: BOM={0} nonASCII={1} CRLF={2}" -f ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF), (@($bytes | Where-Object { $_ -gt 127 }).Count), ([IO.File]::ReadAllText($srcFile.FullName).Contains("`r`n")))
  $srcLines[2] = $Copy
  $tmp = "$dstPath.tmp"
  [IO.File]::WriteAllText($tmp, ($srcLines -join "`r`n"), [Text.UTF8Encoding]::new($false))
  [IO.File]::Replace($tmp, $dstPath, [NullString]::Value)
  $a = [IO.File]::ReadAllBytes($srcFile.FullName); $b = [IO.File]::ReadAllBytes($dstPath)
  $l1 = [IO.File]::ReadAllText($srcFile.FullName) -split "`r`n"; $l2 = [IO.File]::ReadAllText($dstPath) -split "`r`n"
  $nd = @(for ($i = 0; $i -lt $l1.Count; $i++) { if ($l1[$i] -ne $l2[$i]) { $i + 1 } })
  Check ($nd.Count -eq 1 -and $nd[0] -eq 3 -and $l1.Count -eq $l2.Count) "rewritten file identical to source except line 3 (differing lines: $($nd -join ','); bytes src=$($a.Length) dst=$($b.Length))"
  # 6. reload Y from disk and compare
  Fx "--preset=`"$Copy`""
  $s6 = Status; $sy = Sound $s6
  Check ($s6.selected_preset -eq $Copy) "selected == $Copy"
  Check (-not (PresetInfo $s6 $Copy).modified) "$Copy modified=false"
  Check ($sx.bands -eq $sy.bands -and $sx.freqs -eq $sy.freqs -and $sx.master -eq $sy.master) "bands/master equal (X=$($sx.bands) Y=$($sy.bands))"
  Check ($sx.effects -eq $sy.effects) "effects equal (Y=$($sy.effects))"
  # extra: rename (name with spaces and ' - ')
  Fx "--rename_preset=`"$Renamed`""
  $s7 = Status
  Check ([bool](PresetInfo $s7 $Renamed) -and -not (PresetInfo $s7 $Copy)) "renamed to '$Renamed'"
  Check ($s7.selected_preset -eq $Renamed) "selected == $Renamed"
  # C. delete (selected)
  Fx "--delete_preset"
  $s8 = Status
  Check (-not (PresetInfo $s8 $Renamed) -and -not (PresetInfo $s8 $Copy)) "deleted"
  Log "after delete FxSound selected '$($s8.selected_preset)'"
}
catch { Log "ERROR $_"; $fails++ }
finally {
  # restore
  $sNow = Status
  foreach ($n in @($Copy, $Renamed)) { if (PresetInfo $sNow $n) { Log "residue '$n' -> deleting"; Fx "--preset=`"$n`""; Fx '--delete_preset' } }
  $sNow = Status
  if ((PresetInfo $sNow $Source).modified) { Log "residue: $Source modified -> undo"; Fx "--preset=`"$Source`""; Fx '--undo_preset' }
  Fx "--preset=`"$orig`""
  $sEnd = Status
  $listEnd = (@($sEnd.presets.user_defined | ForEach-Object { "$($_.name):$($_.modified)" })) -join '|'
  Check ($sEnd.selected_preset -eq $orig) "active preset restored to '$orig'"
  Check ($listEnd -eq $list0) "user preset list (with modified flags) identical to start"
  Check (@($sEnd.presets.built_in | Where-Object { $_.modified }).Count -eq 0) "no built-in modified"
  $hashEnd = Hashes
  Check ((($hash0 -join "`n") -eq ($hashEnd -join "`n"))) "all original .fac files byte-identical (SHA256)"
  Check (@(AutoSaveList).Count -eq 0) "AutoSave folder empty [$((AutoSaveList) -join ',')]"
  Check (@(Get-ChildItem $presets -Filter "Spike*").Count -eq 0) "no Spike* file left in Presets (incl. .tmp)"
  Log "volume_leveling end=$($sEnd.equalizer.volume_leveling) (start $($s0.equalizer.volume_leveling))"
  Log "FAILS=$fails"
  $log | Set-Content (Join-Path $BackupDir 'dup-preset.log') -Encoding utf8
}
