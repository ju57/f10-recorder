param([switch]$Download, [string]$Package)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $root 'build.ps1')
$data = Join-Path $root ('artifacts\dependencies-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$arguments = @('--dependency-test','--data-dir',('"' + $data + '"'))
if ($Download) { $arguments += '--download' }
if ($Package) { $arguments += @('--package',('"' + [IO.Path]::GetFullPath($Package) + '"')) }
$process = Start-Process -FilePath (Join-Path $root 'bin\F10Recorder.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
$report = Get-Content -LiteralPath (Join-Path $data 'verification.json') -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 6) }
Write-Host ($report | ConvertTo-Json -Depth 6)
Write-Host "Report: $data\verification.json"
