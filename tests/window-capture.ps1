param([ValidateSet('Synthetic','Window')][string]$Mode='Synthetic', [switch]$NoBuild, [switch]$Plan)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root 'bin/window-checks'
$data=Join-Path $root ('artifacts/window-'+$Mode.ToLowerInvariant()+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
. (Join-Path $PSScriptRoot 'verification-launch.ps1')
$launch=Get-VerificationLaunchPlan -Suite WindowCapture -Mode $Mode -Root $root -DataDirectory $data -Desktop (Join-Path $output 'app/F10Recorder.Desktop.exe')
Assert-VerificationLaunchPlan $launch
if ($Plan) { return $launch }
$sdk=Join-Path $root '.tools/dotnet/dotnet.exe'
if(-not(Test-Path -LiteralPath $sdk)){$sdk=(Get-Command dotnet -ErrorAction Stop).Source}
$env:DOTNET_CLI_HOME=Join-Path $root '.tools/home'
$env:NUGET_PACKAGES=Join-Path $root '.tools/packages'
$env:DOTNET_ROOT=Split-Path -Parent $sdk
$env:DOTNET_ROOT_X64=$env:DOTNET_ROOT
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
New-Item -ItemType Directory -Path $data -ErrorAction Stop|Out-Null
$process=Start-VerificationLaunchPlan $launch
$process.WaitForExit()
$report=Get-Content -LiteralPath (Join-Path $data 'verification.json') -Raw|ConvertFrom-Json
if($process.ExitCode-ne0-or$report.result-ne'passed'){throw ($report|ConvertTo-Json -Depth 4)}
Write-Host "Worker/source lifecycle checks passed: $data"
Write-Host 'Decode and timestamp checks, coded A/V sync, gameplay/focus, long-session drift and performance are separate acceptance gates.'
