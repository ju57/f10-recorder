param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$PlanFile,
    [Parameter(Mandatory)][string]$CapturedSource,
    [Parameter(Mandatory)][string]$Media,
    [Parameter(Mandatory)][string]$Model,
    [Parameter(Mandatory)][string]$EngineRoot,
    [string]$ExpectedPlanSha256,
    [switch]$Plan, [switch]$Prepare, [switch]$Run, [switch]$Reencode
)
$ErrorActionPreference='Stop'
if (@(@($Plan,$Prepare,$Run)|Where-Object{$_}).Count-ne1){throw 'Choose one mode.'}
$repo=Split-Path -Parent $PSScriptRoot;$sdk=Join-Path $repo '.tools/dotnet/dotnet.exe'
$assembly=[IO.Path]::GetFullPath($AssemblyPath);$root=[IO.Path]::GetFullPath($OutputRoot);$planPath=[IO.Path]::GetFullPath($PlanFile)
$source=[IO.Path]::GetFullPath($CapturedSource);$mediaPath=[IO.Path]::GetFullPath($Media);$modelPath=[IO.Path]::GetFullPath($Model);$engineDirectory=[IO.Path]::GetFullPath($EngineRoot)
$prefix=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))+[IO.Path]::DirectorySeparatorChar
foreach($path in @($root,$planPath,$source,$mediaPath,$modelPath)){if(-not$path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Ignored artifacts only.'}}
$effects='Three processes/210seconds/32GB retained-family cap30GBstop/60GBfloor. Independent owned MP4 copies per mode;500ms active observation throughout decode without harness phase delay. Existing captured TS/MP4 and frozen model only; A/B retention adapter versus ordinary native snapshot reader, actual calibrated applicability and private durable ledger. TwoCPUdecoders; no acquisition/encoder/UI/playback/settings/startup/catalog activation. Own MP4 hardlink evidence aliases are identity-deduplicated and all metadata handles close before exit. Publication suppressed; failures stay failed.'
if($Reencode){$effects='Three processes/210seconds/38GBcap36GBstop60GBfloor: onehelper, oneNVENCencoder, oneCPUvalidator. Existing completed TS only, actual calibrated frozenF1/applicability/private ledger/native snapshot reader/corrected evidence hooks; continuous500ms active coverage through encode/full18kframevalidation. Callback/guard/hardlink timing recorded, output identity queries only after terminal closure. No acquisition/UI/playback/settings/startup/catalog activation/publication. Retain all failed evidence.'}
if($Plan){return @{root=$root;effects=$effects;maximumChildren=3;deadlineSeconds=210}}
if($Prepare){
    if((Test-Path $root)-or(Test-Path $planPath)){throw 'Fresh root/plan required.'}
    $build=Split-Path $assembly;$files=[ordered]@{}
    $inputs=@($sdk,$assembly,(Join-Path $build 'F10Recorder.Windows.dll'),(Join-Path $build 'F10Recorder.Core.dll'),(Join-Path $build 'F10Recorder.WorkerChecks.deps.json'),(Join-Path $build 'F10Recorder.WorkerChecks.runtimeconfig.json'),(Join-Path $engineDirectory 'ffmpeg-9.0.2/bin/ffmpeg.exe'),$PSCommandPath,$mediaPath,$modelPath,(Join-Path $source 'segments.csv'))+@(Get-ChildItem -LiteralPath $source -Filter 'chunk-*.ts' -File|ForEach-Object FullName)
    foreach($label in @('T01','T02','T03')){foreach($name in @('sample.json','qualification-source.json','capture-exit.json','fixture-exit.json')){$inputs+=Join-Path (Join-Path (Split-Path $modelPath) $label) $name}}
    foreach($inputFile in $inputs){$files[[IO.Path]::GetFullPath($inputFile)]=(Get-FileHash -LiteralPath $inputFile).Hash}
    New-Item -ItemType Directory -Path (Split-Path $planPath) -Force|Out-Null
    $kind=if($Reencode){'issue14-calibrated-transition-v1'}else{'issue14-calibrated-validation-v1'}
    @{kind=$kind;root=$root;capturedSource=$source;media=$mediaPath;model=$modelPath;engineRoot=$engineDirectory;files=$files;maximumChildren=3;deadlineSeconds=210;effects=$effects}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $planPath
    return @{plan=$planPath;sha256=(Get-FileHash $planPath).Hash;effects=$effects}
}
if(-not$ExpectedPlanSha256-or(Get-FileHash $planPath).Hash-ne$ExpectedPlanSha256){throw 'Reviewed diagnostic identity changed.'}
$record=Get-Content $planPath -Raw|ConvertFrom-Json
if($record.root-ne$root-or$record.capturedSource-ne$source-or$record.media-ne$mediaPath-or$record.model-ne$modelPath-or$record.engineRoot-ne$engineDirectory-or (($record.kind-eq'issue14-calibrated-transition-v1')-ne[bool]$Reencode)){throw 'Launch differs from frozen plan.'}
foreach($file in $record.files.PSObject.Properties){if((Get-FileHash -LiteralPath $file.Name).Hash-ne$file.Value){throw 'Bound input changed.'}}
$receipt="$planPath.controller.json";$stdout="$planPath.stdout.txt";$stderr="$planPath.stderr.txt";if(Test-Path $receipt){throw 'Attempt already charged.'}
$arguments=@($assembly,'--replay-calibration-calibrated-validation',$planPath,$ExpectedPlanSha256);$quoted=$arguments|ForEach-Object{if($_.Contains('"')){throw 'Quoted paths unsupported.'};'"'+$_+'"'}
$owned=$null;$timer=[Diagnostics.Stopwatch]::StartNew()
try{
    $owned=Start-Process -FilePath $sdk -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while(-not$owned.WaitForExit(100)){if($timer.Elapsed.TotalSeconds-gt205){throw 'Diagnostic deadline;5seconds cleanup remains.'};if((Get-Item $stdout).Length+(Get-Item $stderr).Length-gt131072){throw 'Diagnostic log cap.'}}
    $code=$owned.ExitCode;@{pid=$owned.Id;exit=$code;confirmedExited=$true;elapsedSeconds=$timer.Elapsed.TotalSeconds}|ConvertTo-Json|Set-Content $receipt
    Get-Content $stdout;Get-Content $stderr;if($code-ne0){throw 'Calibrated diagnostic failed; retain all evidence.'}
}finally{if($owned-and-not$owned.HasExited){$owned.Kill($true);if(-not$owned.WaitForExit(5000)){throw 'Diagnostic exit unconfirmed.'}};if($owned){$owned.Dispose()}}
