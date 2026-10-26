param([string]$Dotnet, [string]$BuildDirectory, [string]$DataDirectory, [string]$SnapshotsPath, [string]$EngineSource, [string]$EngineRoot, [switch]$DesktopOnly, [switch]$EntryOnly, [switch]$StatusTransportOnly, [switch]$RealWorkerCorrection, [switch]$NoBuild, [switch]$Plan)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (@($DesktopOnly,$EntryOnly,$StatusTransportOnly,$RealWorkerCorrection | Where-Object { $_ }).Count -gt 1) { throw 'Choose one profile verification route.' }
if ($RealWorkerCorrection -and (-not $EngineSource -or -not $EngineRoot)) { throw 'Real-worker correction requires explicit retained -EngineSource and isolated shared -EngineRoot.' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $root $(if ($DesktopOnly -or $EntryOnly) { 'bin/profile-migration-ui' } else { 'bin/profile-migration' }) }
if (-not $DataDirectory) { $DataDirectory = Join-Path $root ('artifacts/profile-migration-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)) }
$launch = [pscustomobject]@{FilePath=(Join-Path $BuildDirectory 'F10Recorder.WorkerChecks.exe'); ArgumentList=@('--profile-migration',('"' + $DataDirectory + '"'))}
if ($DesktopOnly) { $launch = [pscustomobject]@{FilePath=(Join-Path $BuildDirectory 'F10Recorder.MigrationTests.exe'); ArgumentList=@(('"' + $DataDirectory + '"'),'--profile-migration-ui',('"' + $SnapshotsPath + '"'))} }
if ($EntryOnly) { $launch = [pscustomobject]@{FilePath=(Join-Path $BuildDirectory 'F10Recorder.MigrationTests.exe'); ArgumentList=@(('"' + $DataDirectory + '"'),'--profile-entry-ui')} }
if ($StatusTransportOnly) { $launch.ArgumentList = @('--profile-status-transport',('"' + $DataDirectory + '"')) }
if ($RealWorkerCorrection) { $launch.ArgumentList = @('--profile-correction-real',('"' + $DataDirectory + '"'),('"' + $EngineSource + '"'),('"' + $EngineRoot + '"')) }
$effects = [pscustomobject]@{
    Launch=$launch; BuildAndRestore=(-not $NoBuild); RestoreMayUseNetwork=(-not $NoBuild)
    VisibleWindows=$false; CaptureExcludedSurfaces=$false; DesktopOrDeviceAudio=$false
    GpuEncoding=$false; EngineLaunch=$false; HiddenShortcuts=$(if ($DesktopOnly -or $EntryOnly -or $StatusTransportOnly) { 'None' } else { 'Isolated Ctrl+Alt+Shift+F4-F6' }); OffscreenControls=([bool]$DesktopOnly -or [bool]$EntryOnly)
    NativeWorker=(-not $DesktopOnly -and -not $EntryOnly -and -not $StatusTransportOnly)
    SourceIdentityCheck=$(if ($EntryOnly -or $StatusTransportOnly) { 'None; controlled snapshots/local files/headless client only' } else { 'Unavailable fixture executable/class only; no source window or image acquisition' })
    StartupWrites=$false; InstalledRecorderMutation=$false; VerificationNetwork=$false
    EngineHashVerification=[bool]$RealWorkerCorrection; TaskOnlyEngineCopy=$(if ($RealWorkerCorrection) { $EngineRoot } else { 'None' })
    RetainedEngineSource=$(if ($RealWorkerCorrection) { $EngineSource } else { 'None' }); FixtureStartupCallback=[bool]$RealWorkerCorrection
    OwnershipStorage=$(if ($RealWorkerCorrection) { Join-Path $DataDirectory 'ownership' } else { 'Existing route declaration' })
    AllowedWorkerCommands=$(if ($RealWorkerCorrection) { 'status, Apply and Quit only; paused:true fixture child on every launch' } else { 'Existing route declaration' })
    Scenarios=$(if ($RealWorkerCorrection) { '7: held-stopped, held-paused, read-lease-failure, future-settings, interrupted, backup-collision, startup-callback-failure' } else { 'Existing route declaration' })
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
    $name = if ($DesktopOnly -or $EntryOnly) { 'F10Recorder.MigrationTests' } else { 'F10Recorder.WorkerChecks' }
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
