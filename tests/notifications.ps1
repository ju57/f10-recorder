$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $root 'build.ps1')
$data = Join-Path $root ('artifacts\notifications-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$process = Start-Process -FilePath (Join-Path $root 'bin\F10Recorder.exe') -ArgumentList @('--notification-test','--data-dir',('"' + $data + '"')) -WindowStyle Hidden -PassThru -Wait
$report = Get-Content -LiteralPath (Join-Path $data 'verification.json') -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 6) }
$decodeErrors = & ffmpeg -hide_banner -v error -i $report.clip -fps_mode passthrough -f null - 2>&1
if ($LASTEXITCODE -ne 0 -or $decodeErrors) { throw ('Replay decode reported errors: ' + ($decodeErrors -join [Environment]::NewLine)) }
Write-Host ($report | ConvertTo-Json -Depth 6)
Write-Host "Preview: $data\notification-preview.png"
