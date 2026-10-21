param([ValidateRange(10,86400)][int]$SoakSeconds = 120, [switch]$NoBuild, [switch]$LiveDesktop, [ValidateSet(24,30,60,120)][int]$FramesPerSecond = 60, [switch]$GpuFixture, [switch]$Plan)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$prefix = if ($LiveDesktop) { 'worker-live-' } else { 'worker-' }
$data = Join-Path $root ('artifacts/' + $prefix + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$desktop = Join-Path $root 'bin/avalonia/F10Recorder.Desktop.exe'
. (Join-Path $PSScriptRoot 'verification-launch.ps1')
$mode = if ($LiveDesktop) { 'LiveDesktop' } else { 'Synthetic' }
$launch = Get-VerificationLaunchPlan -Suite Worker -Mode $mode -Root $root -DataDirectory $data -Desktop $desktop -SoakSeconds $SoakSeconds -FramesPerSecond $FramesPerSecond -GpuFixture:$GpuFixture
Assert-VerificationLaunchPlan $launch
if ($Plan) { return $launch }
$sdk = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROOT = Split-Path -Parent $sdk
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$output = Join-Path $root 'bin\worker-checks'
if (-not $NoBuild) {
    & (Join-Path $root 'build-avalonia.ps1')
    & $sdk build (Join-Path $root 'projects\F10Recorder.WorkerChecks\F10Recorder.WorkerChecks.csproj') -c Release -o $output -m:1 --disable-build-servers -p:UseSharedCompilation=false --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'Worker verification build failed.' }
}
$process = Start-VerificationLaunchPlan $launch
$gpuSamples = [System.Collections.Generic.List[object]]::new()
$gpuTool = if ($LiveDesktop) { Get-Command nvidia-smi -ErrorAction SilentlyContinue } else { $null }
$samplingClock = [System.Diagnostics.Stopwatch]::StartNew()
$nextGpuSample = 0
while (-not $process.WaitForExit(1000)) {
    if ($gpuTool -and $samplingClock.Elapsed.TotalSeconds -ge $nextGpuSample) {
        $nextGpuSample += 5
        $rows = & $gpuTool.Source --query-gpu=timestamp,index,name,driver_version,utilization.gpu,utilization.encoder,utilization.decoder,memory.used --format=csv,noheader,nounits 2>$null
        if ($LASTEXITCODE -eq 0) {
            foreach ($row in ($rows | ConvertFrom-Csv -Header timestamp,index,name,driver_version,gpu_percent,encoder_percent,decoder_percent,memory_mib)) { $gpuSamples.Add($row) }
        }
    }
}
if ($gpuSamples.Count) { $gpuSamples | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $data 'gpu-samples.json') -Encoding utf8 }
$report = Get-Content -LiteralPath (Join-Path $data 'verification.json') -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 8) }
$probe = Join-Path (Split-Path -Parent $report.mediaEngine) 'ffprobe.exe'
if (-not (Test-Path -LiteralPath $probe)) { $probe = (Get-Command ffprobe -ErrorAction Stop).Source }
$media = @()
foreach ($clip in $report.clips) {
    $details = ((& $probe -v error -show_entries 'format=duration:stream=codec_type,codec_name,width,height,start_time,duration,r_frame_rate,nb_frames' -of json $clip) -join [Environment]::NewLine) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect saved replay: $clip" }
    $video = @($details.streams | Where-Object codec_type -eq video)
    $audio = @($details.streams | Where-Object codec_type -eq audio)
    if ($video.Count -ne 1 -or $audio.Count -ne 1) { throw "Missing video/audio: $clip" }
    $startSkew = [Math]::Abs([double]$video[0].start_time - [double]$audio[0].start_time)
    $endSkew = [Math]::Abs(([double]$video[0].start_time + [double]$video[0].duration) - ([double]$audio[0].start_time + [double]$audio[0].duration))
    if ($startSkew -gt 0.15 -or $endSkew -gt 0.15) { throw "Audio/video timestamp boundary skew exceeds 150ms: $clip" }
    $decode = & $report.mediaEngine -hide_banner -v error -i $clip -fps_mode passthrough -f null - 2>&1
    if ($LASTEXITCODE -ne 0 -or $decode) { throw "MP4 decode failed: $clip $decode" }
    $media += [pscustomobject]@{clip=$clip;duration_seconds=[double]$details.format.duration;start_boundary_skew_seconds=$startSkew;end_boundary_skew_seconds=$endSkew;video=$video[0];audio=$audio[0];decode='passed'}
}
$latencies = @($report.saves | Where-Object success | Select-Object -ExpandProperty encoder_save_ms | Sort-Object)
$summary = [ordered]@{
    result='passed'; report=(Join-Path $data 'verification.json'); soak_seconds=$report.soakSeconds
    save_latency_p50_ms=if ($latencies.Count) {$latencies[[int][Math]::Floor(($latencies.Count-1)*0.5)]} else {$null}
    save_latency_p95_ms=if ($latencies.Count) {$latencies[[int][Math]::Ceiling(($latencies.Count-1)*0.95)]} else {$null}
    max_capture_cpu_percent=($report.samples.CaptureCpuPercent | Measure-Object -Maximum).Maximum
    max_capture_working_set_bytes=($report.samples.CaptureWorkingSetBytes | Measure-Object -Maximum).Maximum
    max_worker_working_set_bytes=($report.samples.WorkerWorkingSetBytes | Measure-Object -Maximum).Maximum
    max_worker_cpu_percent=($report.samples.WorkerCpuPercent | Measure-Object -Maximum).Maximum
    max_ui_working_set_bytes=($report.samples.UiWorkingSetBytes | Measure-Object -Maximum).Maximum
    max_ui_cpu_percent=($report.samples.UiCpuPercent | Measure-Object -Maximum).Maximum
    max_cache_bytes=($report.samples.CacheBytes | Measure-Object -Maximum).Maximum
    max_encoder_reported_drops=($report.samples.EncoderReportedDrops | Measure-Object -Maximum).Maximum
    max_encoder_reported_duplicates=($report.samples.EncoderReportedDuplicates | Measure-Object -Maximum).Maximum
    media=$media
    capture_source=$report.captureSource
    gpu_sample_count=$gpuSamples.Count
    gpu_samples=if ($gpuSamples.Count) { Join-Path $data 'gpu-samples.json' } else { $null }
    limitations='Controlled test imagery, not gaming performance; FFmpeg-reported drops, not total capture loss; timestamp boundary skew, not perceptual lip-sync. Optional NVIDIA counters cover the whole GPU, not F10 alone. Physical device changes, sleep/resume and volume exhaustion are not exercised.'
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $data 'measurements.json') -Encoding utf8
Write-Host ($summary | ConvertTo-Json -Depth 8)
