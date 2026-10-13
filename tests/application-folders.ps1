$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $root 'build.ps1')
$data = Join-Path $root ('artifacts\application-folders-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$process = Start-Process -FilePath (Join-Path $root 'bin\F10Recorder.exe') -ArgumentList @('--application-folders-test','--data-dir',('"' + $data + '"')) -WindowStyle Hidden -Wait -PassThru
$report = Get-Content -LiteralPath (Join-Path $data 'verification.json') -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 6) }
foreach ($clip in $report.clips) {
    $errors = & ffmpeg -hide_banner -v error -i $clip -fps_mode passthrough -f null - 2>&1
    if ($LASTEXITCODE -ne 0 -or $errors) { throw ('Grouped replay decode failed: ' + ($errors -join [Environment]::NewLine)) }
}
Write-Host ($report | ConvertTo-Json -Depth 6)
Write-Host "Report: $data\verification.json"
