param([Parameter(Mandatory)][string]$PlanFile,[Parameter(Mandatory)][string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run,[switch]$Template)
$ErrorActionPreference='Stop'
$clock=[Diagnostics.Stopwatch]::StartNew();$entryUtc=[DateTime]::UtcNow
if($Plan-eq$Run){throw 'Choose Plan or Run'}
if($Template-and-not$Plan){throw 'Template is a read-only prospective Plan preview only'}
try {
$shared=Join-Path $PSScriptRoot confirmation-controller
foreach($leaf in @('Operations','CreatorProof','UnheldControls','Common','Arguments')){. (Join-Path $shared "ProcessController.$leaf.ps1")}
$planPath=(Resolve-Path -LiteralPath $PlanFile).Path
$raw=Read-Bounded $planPath 131072
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))
if($hash-cne$ExpectedPlanSha256){throw 'Exact reviewed raw admission required'}
$a=[Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json
$retained=$a.operation-ceq'retained-H03-1m'
$p=if($retained){$a.Resources}else{$a.plan}
$same=$a.controllerKind-ceq'issue83-two-confirmation-owner-v2'
if(-not$same-and$raw.Length-gt65536){throw 'Historical plan64KiB bound remains unchanged'}
if($a.operation-cnotin@('C00','retained-H03-1m')-or$a.controllerKind-cnotin@('issue83-two-confirmation-owner-v1','issue83-two-confirmation-owner-v2')){throw 'Only the two fixed confirmation operations are supported'}
$whole=if($retained){if($same){900}else{600}}else{1800};$roles=if($retained){if($same){9}else{7}}else{if($same){16}else{13}};$captures=if($retained){0}else{1}
$admission=if($retained){if($same){'issue83-retained-same-C00-final-minute-v10'}else{'issue83-retained-H03-final-minute-pair-v9'}}else{if($same){'issue83-C00-same-capture-reference-v10'}else{'issue83-C00-current-context-frozen-union-v9'}}
if($p.FreshCandidateAdmission-cne$admission-or$p.ReplayMinutes-ne$(if($retained){1}else{5})-or$p.DeadlineSeconds-ne$whole-or
 $p.OfflineDeadlineSeconds-ne$(if($same-and-not$retained){900}else{600})-or$p.MaximumChildren-ne$roles-or$p.RetainedChildren-ne0-or$p.RetainedElapsedSeconds-ne0-or$p.RetainedOfflineSeconds-ne0-or
 ($p.Order-join',')-cne'H03'-or$p.FloorBytes-ne60000000000-or$p.ReplayExporterRecipe-cne'replay-avc-aac-owned-completed-history-direct-avcc-with-reserved-staging-exact-fallback-v5'-or
 $a.parentHandleBudget-ne49152-or$a.maximumSupportChildren-ne1-or$a.maximumOuterRoles-ne1-or$a.creatorProofPolicy-cne'qualification-child-creator-v1'-or$a.creatorProofMilliseconds-ne130000){throw 'Exact finite confirmation shape required'}
if(-not$retained-and(-not$p.CompositeQualification-or-not$p.CompositeExplicitForegroundRelease-or-not$p.CompositeManualForegroundHandoff)){throw 'C00 requires explicit owned foreground release'}
if($retained-and-not$Template-and($p.CompositeQualification-or$p.SelectedRetainedFiles.Count-lt1-or$p.SelectedRetainedFiles.Count-gt65-or$a.Inputs.Count-ne$p.SelectedRetainedFiles.Count-or
 (-not$same-and$a.Inputs.Count-ne61)-or
 ($a.Inputs|Where-Object {$_.ContentSha256-cnotmatch'^[0-9A-F]{64}$'}))){throw 'Retained pair requires61 original-content expectations and selected leases'}
$directory=Split-Path -Parent $planPath
if($Template){
 if(-not[IO.Path]::IsPathFullyQualified([string]$a.prospectivePlanPath)){throw 'Exact prospective final admission path required'}
 $directory=Split-Path -Parent $a.prospectivePlanPath
}elseif($same-and$a.prospectivePlanPath-cne$planPath){throw 'Actual operation differs from its approved prospective path'}
$phase=$directory;$phaseByteLimit=1000000000L
$artifactPrefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))+'\'
if(-not([IO.Path]::GetFullPath($p.Root)).StartsWith($artifactPrefix,[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $p.Root)){throw 'Fresh isolated confirmation output required'}
if($a.entryPath-cne$PSCommandPath-or$a.stopFile-cne(Join-Path $directory STOP.REQUEST)-or$a.watchStatusFile-cne(Join-Path $directory NEXT-HANDOFF.json)){throw 'Exact owner paths required'}
$runtime=@($p.Files.PSObject.Properties.Name|Where-Object {[IO.Path]::GetFileName($_)-ceq'dotnet.exe'})
$helper=@($p.Files.PSObject.Properties.Name|Where-Object {[IO.Path]::GetFileName($_)-ceq'F10Recorder.WorkerChecks.dll'})
$engine=@($p.Files.PSObject.Properties.Name|Where-Object {[IO.Path]::GetFileName($_)-ceq'ffmpeg.exe'})
$consoleImage=Join-Path ([Environment]::SystemDirectory) conhost.exe
if($runtime.Count-ne1-or$helper.Count-ne1-or$engine.Count-ne1){throw 'Exact host/helper/engine roles required'}
if([IO.Path]::GetFullPath((Join-Path $p.EngineRoot 'ffmpeg-9.0.2/bin/ffmpeg.exe'))-cne$engine[0]){throw 'Declared managed engine root does not resolve the exact bound executable'}
foreach($file in @($PSCommandPath,$consoleImage,$a.runtimeManifest,$a.controllerRuntimeManifest,$a.controllerRuntime)+@(Get-ChildItem -LiteralPath $shared -File|ForEach-Object FullName)){
 if($p.Files.PSObject.Properties.Name-cnotcontains$file){throw "Unbound owner dependency:$file"}
}
if($Template-and(-not$same-or-not$a.prospectiveSameCaptureRules-or($retained-and($a.Inputs.Count-ne0-or$p.SelectedRetainedFiles.Count-ne0)))){throw 'Exact prospective v10 rules/empty future selection required'}
foreach($binding in $p.Files.PSObject.Properties){
 if($Template-and$binding.Value-cin@('conditional-approved-manifest','conditional-derived')){continue}
 if((Get-FileHash -LiteralPath $binding.Name).Hash-cne$binding.Value){throw "Changed binding:$($binding.Name)"}
}
foreach($manifest in @($a.runtimeManifest,$a.controllerRuntimeManifest)){
 $members=[Text.Encoding]::UTF8.GetString((Read-Bounded $manifest 524288))|ConvertFrom-Json
 if(@($members.PSObject.Properties).Count-lt1-or@($members.PSObject.Properties).Count-gt1024){throw 'Bounded runtime closure required'}
 foreach($binding in $members.PSObject.Properties){if((Get-FileHash -LiteralPath $binding.Name).Hash-cne$binding.Value){throw "Runtime member changed:$($binding.Name)"}}
}
if($a.PSObject.Properties.Name -contains 'fault'-or$a.PSObject.Properties.Name -contains 'lifecycleMode'){throw 'No injected operations in production owner'}
$rawChildRowLimit=4*([int][Math]::Ceiling($whole/.5)+2);$rawChildRowBytes=4096;$rawChildByteLimit=[long]$rawChildRowLimit*$rawChildRowBytes
if($a.rawChildByteLimit-ne$rawChildByteLimit){throw 'Exact finite raw observation reserve required'}
if($Plan){[ordered]@{planSha256=$hash;operation=$a.operation;prospectiveTemplate=[bool]$Template;helperArguments=@($helper[0],$(if($retained){'--replay-coverage-retained-pair'}else{'--replay-calibration-run'}),$(if($Template){$a.prospectivePlanPath}else{$planPath}),$(if($Template){'conditional-final-raw-plan-sha256'}else{$hash}));
 captures=$captures;outputs=2;managedRoles=$roles;outer=1;consoleSupport=1;wholeSeconds=$whole;terminalReserveSeconds=25;parentRam=805306368;parentHandles=49152;childRam=2147483648;rawChildByteLimit=$rawChildByteLimit;
 effects='Plan reads bounded NONMEDIA plans/bindings/runtime members only. Run requires explicit conditional release, invokes media owner, and never accepts precise A/V without its independent oracle.'}|ConvertTo-Json -Depth 6;return}
# The release is separate from the plan; it cannot be made true by a Plan preview.
. (Join-Path $shared 'ProcessController.Release.ps1')
Assert-ConfirmationRelease
if((Get-Process -Id $PID).Path-ine$a.controllerRuntime){throw 'Actual pinned controller host required'}
$deadline=([DateTimeOffset]$a.expiresUtc).UtcDateTime
$purposeDeadline=if($retained){$deadline}else{([DateTimeOffset]$a.originalPurposeDeadlineUtc).UtcDateTime}
$wholeLimitSeconds=[Math]::Min($whole,($purposeDeadline-$entryUtc).TotalSeconds)
if($wholeLimitSeconds-lt$(if($retained){if($same){900}else{600}}else{1200})-or$entryUtc.AddSeconds($wholeLimitSeconds)-ge$deadline){throw 'Fixed release window no longer fits; no refresh'}
$minimumFree=if($same){if($retained){73000000000L}else{90000000000L}}elseif($retained){72000000000L}else{72721989120L}
if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($p.Root)).AvailableFreeSpace-lt$minimumFree){throw 'Initial physical commitments/free reserve unavailable'}
if(Test-Path -LiteralPath $a.stopFile){throw 'Stop requested before start'}
$stdout=Join-Path $directory stdout.txt;$stderr=Join-Path $directory stderr.txt;$receipt=Join-Path $directory controller.json
foreach($file in @($stdout,$stderr,$receipt)){if(Test-Path -LiteralPath $file){throw 'Prior attempt preserved; no retry'}}
$private=Join-Path $directory cohort-private;if(Test-Path -LiteralPath $private){throw 'Prior private runtime preserved'}
$envNames=@('DOTNET_CLI_HOME','TEMP','TMP','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO')
$old=@{};foreach($name in $envNames){$old[$name]=[Environment]::GetEnvironmentVariable($name)}
$controllerArguments=@($helper[0],$(if($retained){'--replay-coverage-retained-pair'}else{'--replay-calibration-run'}),$planPath,$hash)
# Historical counters are retained in their old roots; this pair never renumbers them.
$a|Add-Member -NotePropertyMembers @{priorFailedManagedRoles=0;retainedManagedRoles=0;cumulativeManagedRoleCeiling=$roles} -Force
. (Join-Path $shared 'ProcessController.State.ps1')
. (Join-Path $shared 'ProcessController.Result.ps1')
. (Join-Path $shared 'ProcessController.Flow.ps1')
} catch {
 $primary=$_
 if($Run){
  $failureDirectory=Split-Path -Parent ([IO.Path]::GetFullPath($PlanFile))
  $allowed=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/issue83-same-capture-plan-20261114/admissions'))+'\'
  if($failureDirectory.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)-and(Test-Path -LiteralPath $failureDirectory)){
   $failureFile=Join-Path $failureDirectory ADMISSION-FAILURE.json
   if(-not(Test-Path -LiteralPath $failureFile)){
    $message=$primary.ToString();if($message.Length-gt8192){$message=$message.Substring(0,8192)}
    $bytes=[Text.Encoding]::UTF8.GetBytes((@{kind='confirmation-owner-failure-v1';planPath=$PlanFile;planSha256=$hash;error=$message;seconds=$clock.Elapsed.TotalSeconds;noRetry=$true}|ConvertTo-Json))
    $stream=[IO.FileStream]::new($failureFile,'CreateNew','Write','Read');try{$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
   }
  }
 }
 throw $primary
}
