param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$PlanFile,
    [Parameter(Mandatory)][string]$CapturedSource,
    [Parameter(Mandatory)][string]$EngineRoot,
    [string]$ExpectedPlanSha256,
    [switch]$Plan, [switch]$Prepare, [switch]$Run
)
$ErrorActionPreference = 'Stop'
if (@(@($Plan,$Prepare,$Run) | Where-Object { $_ }).Count -ne 1) { throw 'Choose one mode.' }
$repo=Split-Path -Parent $PSScriptRoot; $sdk=Join-Path $repo '.tools/dotnet/dotnet.exe'
$assembly=[IO.Path]::GetFullPath($AssemblyPath); $root=[IO.Path]::GetFullPath($OutputRoot); $planPath=[IO.Path]::GetFullPath($PlanFile)
$source=[IO.Path]::GetFullPath($CapturedSource); $engineDirectory=[IO.Path]::GetFullPath($EngineRoot)
$artifactPrefix=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))+[IO.Path]::DirectorySeparatorChar
foreach($path in @($root,$planPath,$source)){if(-not $path.StartsWith($artifactPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Diagnostic uses ignored artifacts only.'}}
if($planPath.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Plan must be outside fresh output.'}
$effects='Bounded trace stall/overflow/error controls plus650ms injected trace-write stall during real sampling. Existing closed captured TS inputs only; onehelper/oneNVENCencoder/oneCPUvalidator,210s total,26GB retained-family cap60GB floor. Actual exact-export and product storage callbacks, readonly validation only after successful encoder closure; publication suppressed. No acquisition, audio/device capture, window, startup/preferences or catalog activation.'
if($Plan){return [pscustomobject]@{root=$root;maximumChildren=3;deadlineSeconds=210;budgetBytes=26000000000;effects=$effects}}
if($Prepare){
    if((Test-Path -LiteralPath $root)-or(Test-Path -LiteralPath $planPath)){throw 'Fresh output/plan required.'}
    $files=[ordered]@{}
    $inputs=@($sdk,$assembly,(Join-Path (Split-Path $assembly) 'F10Recorder.Windows.dll'),(Join-Path (Split-Path $assembly) 'F10Recorder.Core.dll'),(Join-Path (Split-Path $assembly) 'F10Recorder.WorkerChecks.deps.json'),(Join-Path (Split-Path $assembly) 'F10Recorder.WorkerChecks.runtimeconfig.json'),(Join-Path $engineDirectory 'ffmpeg-9.0.2/bin/ffmpeg.exe'),(Join-Path $source 'segments.csv'),$PSCommandPath)+@(Get-ChildItem -LiteralPath $source -Filter 'chunk-*.ts' -File | ForEach-Object FullName)
    foreach($file in $inputs){$files[[IO.Path]::GetFullPath($file)]=(Get-FileHash -LiteralPath $file).Hash}
    New-Item -ItemType Directory -Path (Split-Path $planPath) -Force | Out-Null
    @{kind='issue14-captured-transition-v1';root=$root;capturedSource=$source;engineRoot=$engineDirectory;files=$files;maximumChildren=3;deadlineSeconds=210;budgetBytes=26000000000;effects=$effects}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $planPath
    return [pscustomobject]@{plan=$planPath;sha256=(Get-FileHash -LiteralPath $planPath).Hash;effects=$effects}
}
if(-not $ExpectedPlanSha256-or(Get-FileHash -LiteralPath $planPath).Hash-ne$ExpectedPlanSha256){throw 'Reviewed transition plan changed.'}
$record=Get-Content -LiteralPath $planPath -Raw|ConvertFrom-Json
if($record.root-ne$root-or$record.capturedSource-ne$source-or$record.engineRoot-ne$engineDirectory){throw 'Launch differs from frozen transition.'}
foreach($file in $record.files.PSObject.Properties){if((Get-FileHash -LiteralPath $file.Name).Hash-ne$file.Value){throw 'Transition binding changed.'}}
$receipt="$planPath.controller.json"; $stdout="$planPath.stdout.txt"; $stderr="$planPath.stderr.txt"
if(Test-Path -LiteralPath $receipt){throw 'Attempt already started; retain it.'}
$arguments=@($assembly,'--replay-calibration-captured-transition',$planPath,$ExpectedPlanSha256)
$quoted=$arguments|ForEach-Object{if($_.Contains('"')){throw 'Quoted paths unsupported.'};'"'+$_+'"'}
$owned=$null;$clock=[Diagnostics.Stopwatch]::StartNew()
try{
    $owned=Start-Process -FilePath $sdk -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while(-not$owned.WaitForExit(100)){if($clock.Elapsed.TotalSeconds-gt205){throw 'Transition deadline;5sec cleanup remains.'};if((Get-Item -LiteralPath $stdout).Length+(Get-Item -LiteralPath $stderr).Length-gt131072){throw 'Transition diagnostic cap.'}}
    $code=$owned.ExitCode;@{pid=$owned.Id;exit=$code;confirmedExited=$true;elapsedSeconds=$clock.Elapsed.TotalSeconds}|ConvertTo-Json|Set-Content -LiteralPath $receipt
    Get-Content -LiteralPath $stdout;Get-Content -LiteralPath $stderr
    if($code-ne0){throw 'Captured transition failed;retain all evidence.'}
}finally{if($owned-and-not$owned.HasExited){$owned.Kill($true);if(-not$owned.WaitForExit(5000)){throw 'Owned transition exit unconfirmed.'}};if($owned){$owned.Dispose()}}
