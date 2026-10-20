param([ValidateSet('Synthetic','Window')][string]$Mode='Synthetic', [switch]$NoBuild)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$sdk=Join-Path $root '.tools/dotnet/dotnet.exe'
if(-not(Test-Path -LiteralPath $sdk)){$sdk=(Get-Command dotnet -ErrorAction Stop).Source}
$env:DOTNET_CLI_HOME=Join-Path $root '.tools/home'
$env:NUGET_PACKAGES=Join-Path $root '.tools/packages'
$env:DOTNET_ROOT=Split-Path -Parent $sdk
$env:DOTNET_ROOT_X64=$env:DOTNET_ROOT
$output=Join-Path $root 'bin/window-checks'
if(-not$NoBuild){
    foreach($project in @('Desktop','WorkerChecks')){
        $file=Join-Path $root ('projects/F10Recorder.'+$project+'/F10Recorder.'+$project+'.csproj')
        & $sdk restore $file --locked-mode
        if($LASTEXITCODE){throw 'Locked restore failed.'}
        $folder=if($project-eq'Desktop'){'app'}else{'checks'}
        & $sdk build $file -c Release --no-restore -o (Join-Path $output $folder) -m:1 --disable-build-servers -p:UseSharedCompilation=false
        if($LASTEXITCODE){throw 'Window integration verification build failed.'}
    }
}
$data=Join-Path $root ('artifacts/window-'+$Mode.ToLowerInvariant()+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $data -ErrorAction Stop|Out-Null
$command=if($Mode-eq'Window'){'--window-checks'}else{'--headless-status'}
# Window mode records the controlled visible fixture and default system audio,
# displays ordinary F10 surfaces and briefly requests focus. Obtain authorization
# and exclusive desktop ownership before invoking it. Synthetic creates no UI.
$arguments=@($command,('"'+$data+'"'),('"'+(Join-Path $output 'app/F10Recorder.Desktop.exe')+'"'))
$process=Start-Process -FilePath (Join-Path $output 'checks/F10Recorder.WorkerChecks.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
$report=Get-Content -LiteralPath (Join-Path $data 'verification.json') -Raw|ConvertFrom-Json
if($process.ExitCode-ne0-or$report.result-ne'passed'){throw ($report|ConvertTo-Json -Depth 4)}
Write-Host "Worker/source lifecycle checks passed: $data"
Write-Host 'Decode and timestamp checks, coded A/V sync, gameplay/focus, long-session drift and performance are separate acceptance gates.'
