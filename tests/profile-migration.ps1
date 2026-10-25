param([string]$Dotnet, [string]$BuildDirectory, [string]$DataDirectory, [string]$SnapshotsPath, [switch]$DesktopOnly, [switch]$StatusTransportOnly, [switch]$NoBuild, [switch]$Plan)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($DesktopOnly -and $StatusTransportOnly) { throw 'Choose one profile verification route.' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $root $(if ($DesktopOnly) { 'bin/profile-migration-ui' } else { 'bin/profile-migration' }) }
if (-not $DataDirectory) { $DataDirectory = Join-Path $root ('artifacts/profile-migration-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)) }
$launch = [pscustomobject]@{FilePath=(Join-Path $BuildDirectory 'F10Recorder.WorkerChecks.exe'); ArgumentList=@('--profile-migration',('"' + $DataDirectory + '"'))}
if ($DesktopOnly) { $launch = [pscustomobject]@{FilePath=(Join-Path $BuildDirectory 'F10Recorder.MigrationTests.exe'); ArgumentList=@(('"' + $DataDirectory + '"'),'--profile-migration-ui',('"' + $SnapshotsPath + '"'))} }
if ($StatusTransportOnly) { $launch.ArgumentList = @('--profile-status-transport',('"' + $DataDirectory + '"')) }
$effects = [pscustomobject]@{
    Launch=$launch; BuildAndRestore=(-not $NoBuild); RestoreMayUseNetwork=(-not $NoBuild)
    VisibleWindows=$false; CaptureExcludedSurfaces=$false; DesktopOrDeviceAudio=$false
    GpuEncoding=$false; EngineLaunch=$false; HiddenShortcuts=$(if ($DesktopOnly -or $StatusTransportOnly) { 'None' } else { 'Isolated Ctrl+Alt+Shift+F4-F6' }); OffscreenControls=[bool]$DesktopOnly
    NativeWorker=(-not $DesktopOnly -and -not $StatusTransportOnly)
    SourceIdentityCheck=$(if ($StatusTransportOnly) { 'None; local file/protocol/headless client only' } else { 'Unavailable fixture executable/class only; no source window or image acquisition' })
    StartupWrites=$false; InstalledRecorderMutation=$false; VerificationNetwork=$false
    Storage=$DataDirectory
}
if ($Plan) { return $effects }
if ($DesktopOnly -and -not $SnapshotsPath) { throw 'Supply real worker snapshots with -SnapshotsPath.' }
if (-not $Dotnet) { $Dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'; if (-not (Test-Path -LiteralPath $Dotnet)) { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source } }
$env:DOTNET_ROOT = Split-Path -Parent $Dotnet
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_CLI_HOME = Join-Path $root 'artifacts/profile-migration-sdk-home'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
if (-not $NoBuild) {
    $name = if ($DesktopOnly) { 'F10Recorder.MigrationTests' } else { 'F10Recorder.WorkerChecks' }
    $project = Join-Path $root ('projects/' + $name + '/' + $name + '.csproj')
    & $Dotnet restore $project --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all
    if ($LASTEXITCODE) { throw 'Locked restore failed.' }
    & $Dotnet build $project -c Release --no-restore -m:1 --disable-build-servers -p:UseSharedCompilation=false -o $BuildDirectory
    if ($LASTEXITCODE) { throw 'Profile migration build failed.' }
}
$process = Start-Process -FilePath $launch.FilePath -ArgumentList $launch.ArgumentList -WindowStyle Hidden -PassThru -Wait
$receipt = if ($DesktopOnly) { 'desktop-verification.json' } else { 'verification.json' }
$report = Get-Content -LiteralPath (Join-Path $DataDirectory $receipt) -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 8) }
$report | ConvertTo-Json -Depth 8
