# Builds the FxAuto installer: installer\Output\FxAuto-Setup-<version>.exe
#
#   pwsh installer\build.ps1                 # publish + compile
#   pwsh installer\build.ps1 -SkipPublish    # recompile the .iss from the existing staging folder
#
# Publishes FxAuto self-contained (win-x64, .NET runtime included, no trimming: WinForms/COM do not support it)
# into installer\staging (never into dist\), then compiles installer\FxAuto.iss with Inno Setup 6 (ISCC.exe).
# Inno Setup is a build tool only: winget install --id JRSoftware.InnoSetup -e --scope user
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [string]$Iscc,
    # Optional x.y.z (a leading 'v', as in a git tag, is dropped). Default: the published exe's file version.
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $PSScriptRoot 'staging'
$project = Join-Path $root 'src\FxAuto\FxAuto.csproj'

if (-not $SkipPublish) {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    # ReadyToRun: precompiles FxAuto's own assemblies (the runtime ones already are), for a faster start.
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:PublishTrimmed=false `
        -p:DebugType=none -p:GenerateDocumentationFile=false `
        -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}

$exe = Join-Path $staging 'FxAuto.exe'
if (-not (Test-Path $exe)) { throw "Not found: $exe (run without -SkipPublish)" }
$v = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
$version = if ($Version) { $Version.TrimStart('v', 'V') } else { "$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart)" }
# The in-app updater looks for exactly FxAuto-Setup-<x.y.z>.exe in the GitHub release.
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z, got '$version'" }

# Optional code signing of the app itself (no certificate yet; SmartScreen will say "Unknown publisher").
# signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f cert.pfx /p $env:FXAUTO_CERT_PASSWORD $exe

if (-not $Iscc) {
    $Iscc = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Iscc) { throw 'ISCC.exe (Inno Setup 6) not found. Install it: winget install --id JRSoftware.InnoSetup -e --scope user' }

& $Iscc /Q "/DAppVersion=$version" "/DStagingDir=$staging" (Join-Path $PSScriptRoot 'FxAuto.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

$out = Join-Path $PSScriptRoot "Output\FxAuto-Setup-$version.exe"
'{0}  ({1:N1} MB)' -f $out, ((Get-Item $out).Length / 1MB)
