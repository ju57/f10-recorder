param([ValidateSet('Synthetic','Desktop','Resilience')][string]$Mode = 'Synthetic')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $root 'build.ps1')
$data = Join-Path $root ('artifacts\verify-' + $Mode.ToLowerInvariant() + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$flag = switch ($Mode) { Synthetic { '--self-test' } Desktop { '--smoke-test' } Resilience { '--resilience-test' } }
$process = Start-Process -FilePath (Join-Path $root 'bin\F10Recorder.exe') -ArgumentList @($flag,'--data-dir',('"' + $data + '"')) -WindowStyle Hidden -PassThru -Wait
$reportPath = Join-Path $data 'verification.json'
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 6) }
$probeText = & ffprobe -v error -show_entries 'format=duration:stream=codec_type,codec_name,width,height,start_time,duration' -of json $report.clip
if ($LASTEXITCODE -ne 0) { throw 'Saved MP4 could not be probed.' }
$probe = ($probeText -join [Environment]::NewLine) | ConvertFrom-Json
if (@($probe.streams | Where-Object codec_type -eq video).Count -ne 1 -or @($probe.streams | Where-Object codec_type -eq audio).Count -ne 1) { throw 'Expected video and audio tracks.' }
if ([double]$probe.format.duration -le 3 -or [double]$probe.format.duration -gt 61) { throw 'Unexpected replay duration.' }
$decodeErrors = & ffmpeg -hide_banner -v error -i $report.clip -fps_mode passthrough -f null - 2>&1
if ($LASTEXITCODE -ne 0 -or $decodeErrors) { throw ('Replay decode reported errors: ' + ($decodeErrors -join [Environment]::NewLine)) }
$probeText | Set-Content -LiteralPath (Join-Path $data 'media-probe.json') -Encoding UTF8
Write-Host "Passed $Mode verification: $reportPath"
Write-Host "Saved clip: $($report.clip)"
