$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $root 'build.ps1')
$data = Join-Path $root ('artifacts\settings-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$process = Start-Process -FilePath (Join-Path $root 'bin\F10Recorder.exe') -ArgumentList @('--settings-test','--data-dir',('"' + $data + '"')) -WindowStyle Hidden -PassThru -Wait
$report = Get-Content -LiteralPath (Join-Path $data 'verification.json') -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 6) }
Write-Host ($report | ConvertTo-Json -Depth 6)
Write-Host "Preview: $data\settings-preview.png"
