param([switch]$Plan,[Parameter(Mandatory)][string]$Root)
$ErrorActionPreference='Stop'
if($Plan){'Pure generated NONEMPIRICAL authorization/template JSON fixtures; no process/native inventory/media/audio/UI/device.30s/1MiB fresh ignored output.';return}
$Root=[IO.Path]::GetFullPath($Root)
$prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts')))+[IO.Path]::DirectorySeparatorChar
if(-not$Root.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $Root)){throw 'Fresh readiness JSON-fixture root required'}
[void][IO.Directory]::CreateDirectory($Root)
. (Join-Path $PSScriptRoot 'confirmation-controller/ProcessController.Common.ps1')
. (Join-Path $PSScriptRoot 'confirmation-controller/ProcessController.Release.ps1')
$watch=[Diagnostics.Stopwatch]::StartNew();$results=[Collections.Generic.List[object]]::new()
function Write-Json([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30))}
foreach($scenario in @('exact-conditional-release','changed-static-budget','shifted-expiry','wrong-derived-digest','wrong-approval-identity','manifest-member-changed',
 'retained-exact-order','retained-wrong-C00-plan','retained-wrong-controller-operation')){
 $directory=Join-Path $Root $scenario;[void][IO.Directory]::CreateDirectory($directory)
 $retained=$scenario.StartsWith('retained-');$captures=if($retained){0}else{1};$entryUtc=[DateTime]::UtcNow;$t0=$entryUtc.AddSeconds(-1)
 $manifestPath=Join-Path $directory invariants.json;$templatePath=Join-Path $directory template.json;$planPath=Join-Path $directory plan.json
 $dependency=Join-Path $directory source-manifest.json;Write-Json $dependency @{controlledFixture=$true;source='generated nonmedia JSON only'}
 $helper=@('D:\controlled-fixture\Helper.dll')
 $files=[ordered]@{};$files[$manifestPath]='conditional-approved-manifest';$files[$dependency]='conditional-derived';$files[$helper[0]]=('A'*64)
 $operation=if($retained){'retained-H03-1m'}else{'C00'}
 $template=[ordered]@{operation=$operation;createdUtc='conditional-T0';expiresUtc='conditional-expiry';originalPurposeDeadlineUtc='conditional-purpose';plan=@{Files=$files;SourceComponentBytes=1000000000}}
 $lateBindings=@(@{role='source-manifest';path=$dependency})
 if($retained){
  $C00Path=Join-Path $directory C00-controlled-plan.json;$C00Controller=Join-Path $directory C00-controlled-controller.json
  Write-Json $C00Path @{controlledFixture=$true;operation='C00'};$C00Hash=(Get-FileHash -LiteralPath $C00Path).Hash
  $closure=@{controlledFixture=$true;operation='C00';planHash=$C00Hash;absent=$true;exitCode=0;error=$null;postDisposal=$true;processHandlesDisposed=$true}
  if($scenario-eq'retained-wrong-controller-operation'){$closure.operation='different-operation'}
  Write-Json $C00Controller $closure
  $files[$C00Controller]='conditional-derived';$lateBindings+=@(@{role='C00-controller';path=$C00Controller})
  $template.Resources=$template.plan;$template.Remove('plan');$template.Remove('originalPurposeDeadlineUtc')
 }
 Write-Json $templatePath $template
 $members=[ordered]@{};$members[$templatePath]=(Get-FileHash -LiteralPath $templatePath).Hash
 $slots=@{};$slots[$operation]=@{path=$planPath;template=$templatePath;lateBindings=$lateBindings}
 if($retained){$slots.C00=@{path=$C00Path}}
 $manifest=@{kind='issue83-two-confirmation-invariants-v1';controlledFixture=$true;files=$members;operations=$slots}
 Write-Json $manifestPath $manifest;$manifestHash=(Get-FileHash -LiteralPath $manifestPath).Hash
 $a=(Get-Content -LiteralPath $templatePath -Raw|ConvertFrom-Json)
 $a.createdUtc=$t0.ToString('o');$a.expiresUtc=$t0.AddSeconds(3600).ToString('o')
 if(-not$retained){$a.originalPurposeDeadlineUtc=$t0.AddSeconds(1800).ToString('o')}
 $p=if($retained){$a.Resources}else{$a.plan}
 $p.Files.$manifestPath=$manifestHash;$p.Files.$dependency=(Get-FileHash -LiteralPath $dependency).Hash
 if($retained){$p.Files.$C00Controller=(Get-FileHash -LiteralPath $C00Controller).Hash}
 switch($scenario){
  'changed-static-budget'{$a.plan.SourceComponentBytes=2000000000}
  'shifted-expiry'{$a.expiresUtc=$t0.AddSeconds(3601).ToString('o')}
  'wrong-derived-digest'{$a.plan.Files.$dependency=('B'*64)}
 }
 Write-Json $planPath $a;$raw=[IO.File]::ReadAllBytes($planPath);$hash=(Get-FileHash -LiteralPath $planPath).Hash
 $authorization=@{controlledFixture=$true;approvedPlanSha256=$hash;approvedCaptures=$captures;questionItemId='controlled-nonempirical-fixture';approvedUtc=$t0.ToString('o');invariantManifestSha256=$manifestHash}
 Write-Json (Join-Path $directory current-authorization.json) $authorization
 $release=@{controlledFixture=$true;kind='issue83-two-confirmation-release-v1';questionItemId='controlled-nonempirical-fixture';approvedUtc=$t0.ToString('o');planSha256=$hash;invariantManifest=$manifestPath;invariantManifestSha256=$manifestHash}
 if($retained){$release.C00ControllerPath=$C00Controller;$release.C00PlanSha256=if($scenario-eq'retained-wrong-C00-plan'){'B'*64}else{$C00Hash}}
 if($scenario-eq'wrong-approval-identity'){$release.questionItemId='changed'}
 Write-Json (Join-Path $directory approval-release.json) $release
 Write-Json (Join-Path $directory PREFLIGHT.json) @{controlledFixture=$true;kind='final-calibration-admission-v1';result='passed';planSha256=$hash;helperSha256=('A'*64)}
 if($scenario-eq'manifest-member-changed'){[IO.File]::AppendAllText($templatePath,' ')}
 $failure=$null;try{Assert-ConfirmationRelease}catch{$failure=$_.Exception.Message}
 if(($scenario-in@('exact-conditional-release','retained-exact-order'))-eq[bool]$failure){throw "Release fixture failed:$scenario $failure"}
 $results.Add(@{scenario=$scenario;passed=$true;rejection=$failure})
}
if($watch.Elapsed.TotalSeconds-ge30){throw 'JSON fixture30s bound'}
$bytes=(Get-ChildItem -LiteralPath $Root -Recurse -File|Measure-Object Length -Sum).Sum;if($bytes-gt1000000){throw 'JSON fixture1MiB bound'}
Write-Json (Join-Path $Root checks.json) @{result='passed';count=$results.Count;results=$results.ToArray();seconds=$watch.Elapsed.TotalSeconds;controlledFixture=$true;empiricalAcceptance=$false;processesLaunched=0;mediaRead=$false}
"Passed $($results.Count) pure conditional-release checks"
