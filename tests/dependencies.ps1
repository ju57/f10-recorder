param([switch]$Download, [string]$Package, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
if ($Download -and $Package) { throw 'Choose either -Package or -Download.' }
if (-not $Download -and -not $Package) { throw 'Supply the pinned archive with -Package or explicitly choose -Download.' }
$root = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/home'
$env:NUGET_PACKAGES = Join-Path $root '.tools/packages'
$env:DOTNET_ROOT = Split-Path -Parent $sdk
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$output = Join-Path $root 'bin/dependency-checks'
if (-not $NoBuild) {
    $project = Join-Path $root 'projects/F10Recorder.WorkerChecks/F10Recorder.WorkerChecks.csproj'
    & $sdk restore $project --locked-mode
    if ($LASTEXITCODE) { throw 'Locked dependency restore failed.' }
    & $sdk build $project -c Release --no-restore -o $output -m:1 --disable-build-servers -p:UseSharedCompilation=false
    if ($LASTEXITCODE) { throw 'Dependency verification build failed.' }
}
$data = Join-Path $root ('artifacts/dependencies-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$arguments = @('--dependency-test',('"' + $data + '"'))
if ($Download) { $arguments += '--download' }
if ($Package) { $arguments += @('--package',('"' + [IO.Path]::GetFullPath($Package) + '"')) }
$process = Start-Process -FilePath (Join-Path $output 'F10Recorder.WorkerChecks.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
if (-not (Test-Path -LiteralPath (Join-Path $data 'verification.json'))) { throw "Dependency checks exited with code $($process.ExitCode) before writing a report." }
$report = Get-Content -LiteralPath (Join-Path $data 'verification.json') -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 6) }
Write-Host ($report | ConvertTo-Json -Depth 6)
Write-Host "Report: $data/verification.json"
