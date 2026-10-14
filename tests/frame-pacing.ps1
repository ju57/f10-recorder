param([ValidateRange(10,120)][int]$Seconds = 30, [ValidateSet(24,30,60,120)][int]$FramesPerSecond = 60, [ValidateSet(0,24,30,60,120,240)][int]$SourceFramesPerSecond = 0, [ValidateSet('h264_nvenc','hevc_nvenc','av1_nvenc')][string]$VideoCodec = 'h264_nvenc', [ValidateSet('legacy','paced')][string]$SourceMode = 'paced', [ValidateSet('baseline','legacy-input','small-read','low-probe','direct-audio','packet-audio','no-video-queue','filter-source','filter-headroom','filter-oversample','filter-fps','filter-fast','debug-timestamps')][string]$Variant = 'baseline', [ValidateRange(0,1000)][int]$CaptureCeiling = 0, [switch]$NoAudio, [switch]$NoBuild, [switch]$RequireAcceptance)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROOT = Split-Path -Parent $sdk
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$output = Join-Path $root 'bin\pacing-checks'
if (-not $NoBuild) {
    & $sdk build (Join-Path $root 'projects\F10Recorder.WorkerChecks\F10Recorder.WorkerChecks.csproj') -c Release -o $output -m:1 --disable-build-servers -p:UseSharedCompilation=false --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'Frame pacing build failed' }
}
$audio = if ($NoAudio) { 'off' } else { 'on' }
if ($SourceFramesPerSecond -eq 0) { $SourceFramesPerSecond = $FramesPerSecond }
$data = Join-Path $root ('artifacts\pacing-' + $SourceMode + '-' + $FramesPerSecond + '-' + $audio + '-' + $Variant + '-' + $CaptureCeiling + '-source' + $SourceFramesPerSecond + '-' + $VideoCodec + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$process = Start-Process -FilePath (Join-Path $output 'F10Recorder.WorkerChecks.exe') -ArgumentList @('--pacing',('"'+$data+'"'),$Seconds,$FramesPerSecond,$SourceMode,$audio,$Variant,$CaptureCeiling,$SourceFramesPerSecond,$VideoCodec) -WindowStyle Hidden -PassThru -Wait
$report = Get-Content (Join-Path $data 'pacing.json') -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'measured') { throw ($report | ConvertTo-Json -Depth 8) }
[ordered]@{report=(Join-Path $data 'pacing.json');source=$SourceMode;fps=$FramesPerSecond;audio=$audio;analysis=$report.analysis;finalMetrics=$report.finalMetrics} | ConvertTo-Json -Depth 6
if ($RequireAcceptance -and $report.analysis.acceptance -ne 'passed') { throw "Frame pacing acceptance did not pass: $($report.analysis.acceptance); $data" }
