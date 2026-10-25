param([string]$Package, [string]$Dotnet, [switch]$NoBuild, [switch]$Plan)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts/issue-11-build'
$data = Join-Path $root ('artifacts/engine-trust-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$worker = Join-Path $output 'worker/F10Recorder.WorkerChecks.exe'
$packageArgument = if ($Package) { [IO.Path]::GetFullPath($Package) } else { '<approved.zip>' }
$launch = [pscustomobject]@{
    FilePath = Join-Path $output 'ui/F10Recorder.MigrationTests.exe'
    ArgumentList = @(('"' + $data + '"'), '--engine-trust-ui', ('"' + $worker + '"'), ('"' + $packageArgument + '"'))
}
$effects = [pscustomobject]@{
        Route = 'MigrationTests --engine-trust-ui with WorkerChecks --engine-trust-worker'
        Launch = $launch; BuildAndRestore = -not $NoBuild; RestoreMayUseNetwork = -not $NoBuild
        VisibleWindows = $false; CaptureExcludedSurfaces = $false; DesktopOrDeviceAudio = $false
        GeneratedInputs = '256x256 codec initialization and one 640x360/30 synthetic replay with generated tone'
        GpuEncoding = $true; HiddenShortcutWindows = 'Ctrl+Alt+Shift+F1-F3 in an isolated worker'
        StartupWrites = $false; VerificationNetwork = $false; InstalledRecorderMutation = $false
        Storage = 'New artifacts/engine-trust-* directory; local approved archive read-only'
}
if ($Plan) { $effects; return }
if (-not $Package) { throw 'Supply the approved archive with -Package; this route never downloads.' }
if (-not $Dotnet) { $Dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'; if (-not (Test-Path -LiteralPath $Dotnet)) { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source } }
$env:DOTNET_ROOT = Split-Path -Parent $Dotnet
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_CLI_HOME = Join-Path $root 'artifacts/sdk-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $root '.tools/packages' }
if (-not $NoBuild) {
    foreach ($entry in @(@('F10Recorder.MigrationTests', 'ui'), @('F10Recorder.WorkerChecks', 'worker'))) {
        $project = Join-Path $root ('projects/' + $entry[0] + '/' + $entry[0] + '.csproj')
        & $Dotnet restore $project --locked-mode
        if ($LASTEXITCODE) { throw 'Locked restore failed.' }
        & $Dotnet build $project -c Release --no-restore -o (Join-Path $output $entry[1]) -m:1 --disable-build-servers -p:UseSharedCompilation=false
        if ($LASTEXITCODE) { throw 'Engine trust build failed.' }
    }
}
$process = Start-Process -FilePath $launch.FilePath -ArgumentList $launch.ArgumentList -WindowStyle Hidden -PassThru -Wait
$receipt = Join-Path $data 'verification.json'
if (-not (Test-Path -LiteralPath $receipt)) { throw "Trust checks exited $($process.ExitCode) before writing $receipt." }
$report = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
Write-Host "Report: $receipt"
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 8) }
Write-Host ($report | ConvertTo-Json -Depth 8)
