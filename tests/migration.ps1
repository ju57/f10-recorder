param([switch]$NoBuild, [switch]$FocusOnly)
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
$output = Join-Path $root 'bin\migration-tests'
if (-not $NoBuild) {
    & $sdk build (Join-Path $root 'projects\F10Recorder.MigrationTests\F10Recorder.MigrationTests.csproj') -c Release -o $output -m:1 --disable-build-servers -p:UseSharedCompilation=false --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'Migration verification build failed.' }
}
$data = Join-Path $root ('artifacts\migration-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$testArguments = @(('"' + $data + '"'))
if ($FocusOnly) { $testArguments += '--focus-only' }
$process = Start-Process -FilePath (Join-Path $output 'F10Recorder.MigrationTests.exe') -ArgumentList $testArguments -WindowStyle Hidden -PassThru -Wait
$report = Get-Content -LiteralPath (Join-Path $data 'verification.json') -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 8) }
if ($FocusOnly) {
    Write-Host ($report | ConvertTo-Json -Depth 8)
    Write-Host "Paused focus checks passed without recording or screenshots: $data"
    return
}
$engine = $report.mediaEngine
$probe = Join-Path (Split-Path -Parent $engine) 'ffprobe.exe'
if (-not (Test-Path -LiteralPath $probe)) { $probe = (Get-Command ffprobe -ErrorAction Stop).Source }
foreach ($clip in @($report.media) + @($report.hotkeyReplayClip) + @($report.overlayReplayClip)) {
    $streams = ((& $probe -v error -show_entries 'stream=codec_type,codec_name,r_frame_rate' -of json $clip) -join [Environment]::NewLine) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or @($streams.streams | Where-Object codec_type -eq video).Count -ne 1 -or @($streams.streams | Where-Object codec_type -eq audio).Count -ne 1) { throw "Missing video/audio: $clip" }
    $decode = & $engine -hide_banner -v error -i $clip -fps_mode passthrough -f null - 2>&1
    if ($LASTEXITCODE -ne 0 -or $decode) { throw "MP4 decode failed: $clip $decode" }
}
Write-Host ($report | ConvertTo-Json -Depth 8)
Write-Host "All saved MP4s decoded successfully. Previews and report: $data"
