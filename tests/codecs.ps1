param([ValidateSet('h264_nvenc','hevc_nvenc','av1_nvenc')][string]$Encoder)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $root 'build.ps1')
$data = Join-Path $root ('artifacts\codecs-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$arguments = @('--codec-test','--data-dir',('"' + $data + '"'))
if ($Encoder) { $arguments += @('--codec', $Encoder) }
$process = Start-Process -FilePath (Join-Path $root 'bin\F10Recorder.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
$report = Get-Content -LiteralPath (Join-Path $data 'verification.json') -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 6) }
foreach ($clip in $report.clips) {
    $probeText = & ffprobe -v error -show_entries 'format=duration:stream=codec_type,codec_name,r_frame_rate' -of json $clip.Clip
    if ($LASTEXITCODE -ne 0) { throw 'Saved codec clip could not be probed.' }
    $probe = ($probeText -join [Environment]::NewLine) | ConvertFrom-Json
    $video = $probe.streams | Where-Object codec_type -eq video
    $expected = switch ($clip.Encoder) { h264_nvenc { 'h264' } hevc_nvenc { 'hevc' } av1_nvenc { 'av1' } }
    if ($video.codec_name -ne $expected) { throw "Wrong video codec: $($video.codec_name)" }
    if (@($probe.streams | Where-Object codec_type -eq audio).Count -ne 1) { throw 'Missing audio track.' }
    $decodeErrors = & ffmpeg -hide_banner -v error -i $clip.Clip -fps_mode passthrough -f null - 2>&1
    if ($LASTEXITCODE -ne 0 -or $decodeErrors) { throw ('Decode failed: ' + ($decodeErrors -join [Environment]::NewLine)) }
    Write-Host "Passed $expected at $($clip.FramesPerSecond) FPS: $($clip.Clip)"
}
