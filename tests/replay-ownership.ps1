param([switch]$NoBuild, [switch]$Plan)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'bin/replay-ownership'
$data = Join-Path $root ('artifacts/replay-ownership-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$executable = Join-Path $output 'F10Recorder.WorkerChecks.exe'
if ($Plan) {
    [pscustomobject]@{
        Executable=$executable; Arguments=@('--replay-ownership',$data)
        Effects=[ordered]@{UiWindows=$false; Focus=$false; DesktopPixels=$false; SystemAudio=$false; GpuEncoding=$false; EngineLaunch=$false; StartupWrites=$false; NvidiaReplayMayStop=$false}
    }
    return
}
$sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/home'
$env:NUGET_PACKAGES = Join-Path $root '.tools/packages'
$env:DOTNET_ROOT = Split-Path -Parent $sdk
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
if (-not $NoBuild) {
    $project = Join-Path $root 'projects/F10Recorder.WorkerChecks/F10Recorder.WorkerChecks.csproj'
    & $sdk restore $project --locked-mode
    if ($LASTEXITCODE) { throw 'Locked restore failed.' }
    & $sdk build $project -c Release --no-restore -m:1 --disable-build-servers -p:UseSharedCompilation=false -o $output
    if ($LASTEXITCODE) { throw 'Replay ownership build failed.' }
}
# File fixtures and controlled export results only. No native capture/encoder setup.
$process = Start-Process -FilePath $executable -ArgumentList @('--replay-ownership',('"' + $data + '"')) -WindowStyle Hidden -PassThru -Wait
$report = Get-Content -LiteralPath (Join-Path $data 'verification.json') -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $report.result -ne 'passed') { throw ($report | ConvertTo-Json -Depth 8) }
Write-Host ($report | ConvertTo-Json -Depth 8)
Write-Host "Report: $data/verification.json"
