Add-Type -Path "$PSScriptRoot\Sessions.cs"
[SessionProbe]::Run(240000) | Out-File "$PSScriptRoot\long.txt" -Encoding utf8
