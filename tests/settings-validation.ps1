param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/home'
$env:NUGET_PACKAGES = Join-Path $root '.tools/packages'
$env:DOTNET_ROOT = Split-Path -Parent $sdk
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$output = Join-Path $root 'bin/settings-validation'
if (-not $NoBuild) {
    $project = Join-Path $root 'projects/F10Recorder.WorkerChecks/F10Recorder.WorkerChecks.csproj'
    & $sdk restore $project --locked-mode
    if ($LASTEXITCODE) { throw 'Locked restore failed.' }
    & $sdk build $project -c Release --no-restore -m:1 --disable-build-servers -p:UseSharedCompilation=false -o $output
    if ($LASTEXITCODE) { throw 'Settings validation build failed.' }
}
$data = Join-Path $root ('artifacts/settings-validation-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
# Hidden shortcut windows; no capture, GPU probes, visible UI, startup writes or installation.
$process = Start-Process -FilePath (Join-Path $output 'F10Recorder.WorkerChecks.exe') -ArgumentList @('--settings-validation',('"' + $data + '"')) -WindowStyle Hidden -PassThru -Wait
$report = Get-Content -LiteralPath (Join-Path $data 'verification.json') -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 6) }
Write-Host ($report | ConvertTo-Json -Depth 6)
Write-Host "Report: $data/verification.json"
