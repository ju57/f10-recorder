param([Parameter(Mandatory)][string]$Package, [switch]$NoBuild, [string]$Dotnet)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dotnet) { $Dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'; if (-not (Test-Path -LiteralPath $Dotnet)) { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source } }
$env:DOTNET_ROOT = Split-Path -Parent $Dotnet
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/home'
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $root '.tools/packages' }
$project = Join-Path $root 'projects/F10Recorder.WorkerChecks/F10Recorder.WorkerChecks.csproj'
$output = Join-Path $root 'bin/security-checks'
if (-not $NoBuild) {
    & $Dotnet restore $project --locked-mode
    if ($LASTEXITCODE) { throw 'Locked restore failed.' }
    & $Dotnet build $project -c Release --no-restore -m:1 --disable-build-servers -p:UseSharedCompilation=false -o $output
    if ($LASTEXITCODE) { throw 'Security-check build failed.' }
}
# No visible UI, desktop/system-audio capture, input injection, startup change or installation.
# Native account-only pipes require an ordinary user token; a restricted sandbox
# token can reject even the expected child, which is an environment failure.
$data = Join-Path $root ('artifacts/security-' + (Get-Date -Format yyyyMMdd-HHmmss))
$process = Start-Process -FilePath (Join-Path $output 'F10Recorder.WorkerChecks.exe') -ArgumentList @('--security-checks',('"' + $data + '"'),('"' + [IO.Path]::GetFullPath($Package) + '"')) -WindowStyle Hidden -PassThru -Wait
$report = Get-Content -LiteralPath (Join-Path $data verification.json) -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 6) }
Write-Host ($report | ConvertTo-Json -Depth 6)
Write-Host "Report: $data/verification.json"
