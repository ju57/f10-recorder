param([Parameter(Mandatory)][string]$Root,[switch]$Plan)
$ErrorActionPreference='Stop'
if($Plan){'Pure generated NONEMPIRICAL v10 conditional-release/argv JSON fixtures; no native/process/media/device calls.30s/4MiB fresh output.';return}
$rootPath=[IO.Path]::GetFullPath($Root)
$prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts')))+[IO.Path]::DirectorySeparatorChar
if(-not$rootPath.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $rootPath)){throw 'Fresh ignored v10 JSON-fixture root required'}
[void][IO.Directory]::CreateDirectory($rootPath)
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repo 'tests/confirmation-controller/ProcessController.Common.ps1')
. (Join-Path $repo 'tests/confirmation-controller/ProcessController.Release.ps1')
$clock=[Diagnostics.Stopwatch]::StartNew();$results=[Collections.Generic.List[object]]::new()
function Write-Fake([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 40 -Compress))}
foreach($scenario in @('same-C00-exact','changed-static-deadline','changed-selected-path','shifted-expiry','missing-C00-closure','failed-C00-closure','undisposed-C00-closure','unbound-manifest-digest')){
 $directory=Join-Path $rootPath $scenario;[void][IO.Directory]::CreateDirectory($directory)
 $retained=$true;$same=$true;$captures=0;$entryUtc=[DateTime]::UtcNow;$t0=$entryUtc.AddSeconds(-1);$operation='retained-H03-1m'
 $manifestPath=Join-Path $directory 'invariants.json';$planPath=Join-Path $directory 'plan.json';$templatePath=Join-Path $directory 'template.json'
 $C00Plan=Join-Path $directory 'controlled-C00-plan.json';$C00Controller=Join-Path $directory 'controlled-C00-controller.json';$C00Manifest=Join-Path $directory 'controlled-C00-manifest.json'
 $selection=Join-Path $directory 'controlled-selection.jsonl';$freeze=Join-Path $directory 'controlled-freeze.json'
 Write-Fake $C00Plan @{controlledFixture=$true;kind='NONEMPIRICAL-C00-placeholder'}
 $closure=@{controlledFixture=$true;operation='C00';planHash=(Get-FileHash -LiteralPath $C00Plan).Hash;absent=$true;exitCode=0;error=$null;postDisposal=$true;processHandlesDisposed=$true}
 switch($scenario){'missing-C00-closure'{$closure.absent=$false};'failed-C00-closure'{$closure.error='controlled injected failure'};'undisposed-C00-closure'{$closure.processHandlesDisposed=$false}}
 Write-Fake $C00Controller $closure;Write-Fake $freeze @{controlledFixture=$true;notAnOperationalFreeze=$true}
 $original=@();$mapped=@()
 for($n=0;$n-lt300;$n++){
  $extent=[ordered]@{Volume='controlled-volume';Identity="controlled-id-$n";Length=17;AllocatedBytes=4096;AllocationUnit=4096}
  $original+=@([pscustomobject][ordered]@{Path=(Join-Path $directory "unread-original-$n.ts");Start=$n;End=$n+1;Extent=$extent})
  $mapped+=@([pscustomobject][ordered]@{Path=(Join-Path $directory "unread-current-$n.ts");Start=$n;End=$n+1;Extent=$extent;Sha256=('A'*64)})
 }
 [IO.File]::WriteAllLines($selection,@($original|ForEach-Object {$_|ConvertTo-Json -Depth 10 -Compress}))
 Write-Fake $C00Manifest @{controlledFixture=$true;notAnOperationalManifest=$true;Revision='reference-same-C00-coverage-v10';Start=0;End=300;Inputs=$mapped}
 $files=[ordered]@{};$files[$manifestPath]='conditional-approved-manifest'
 $late=@(@{role='coverage-freeze';path=$freeze},@{role='C00-selection';path=$selection},@{role='C00-manifest';path=$C00Manifest},@{role='C00-controller';path=$C00Controller})
 foreach($binding in $late){$files[$binding.path]='conditional-derived'}
 $template=[ordered]@{controlledFixture=$true;operation=$operation;createdUtc='conditional-T0';expiresUtc='conditional-expiry';Resources=@{Files=$files;DeadlineSeconds=900;SelectedRetainedFiles=@()};
  FreezePath=$freeze;FreezeSha256='conditional-derived';OriginalSelectionPath=$selection;OriginalSelectionSha256='conditional-derived';Inputs=@();SameCapture=@{ManifestPath=$C00Manifest}}
 Write-Fake $templatePath $template
 $manifest=@{controlledFixture=$true;kind='issue83-two-confirmation-invariants-v1';files=@{};operations=@{C00=@{path=$C00Plan}}}
 $manifest.files[$templatePath]=(Get-FileHash -LiteralPath $templatePath).Hash
 $manifest.operations[$operation]=@{path=$planPath;template=$templatePath;lateBindings=$late}
 Write-Fake $manifestPath $manifest;$manifestHash=(Get-FileHash -LiteralPath $manifestPath).Hash
 $a=Get-Content -LiteralPath $templatePath -Raw|ConvertFrom-Json;$p=$a.Resources
 $a.createdUtc=$t0.ToString('o');$a.expiresUtc=$t0.AddSeconds(3600).ToString('o');$p.Files.$manifestPath=$manifestHash
 foreach($binding in $late){$p.Files.($binding.path)=(Get-FileHash -LiteralPath $binding.path).Hash}
 $a.FreezeSha256=$p.Files.$freeze;$a.OriginalSelectionSha256=$p.Files.$selection
 $a.Inputs=@(for($n=239;$n-lt300;$n++){[pscustomobject][ordered]@{OriginalPath=$original[$n].Path;CurrentPath=$mapped[$n].Path;ContentSha256=$mapped[$n].Sha256}})
 $p.SelectedRetainedFiles=@($a.Inputs.CurrentPath)
 switch($scenario){'changed-static-deadline'{$p.DeadlineSeconds=901};'changed-selected-path'{$a.Inputs[0].CurrentPath='D:\controlled-not-selected.ts';$p.SelectedRetainedFiles=@($a.Inputs.CurrentPath)};'shifted-expiry'{$a.expiresUtc=$t0.AddSeconds(3601).ToString('o')};'unbound-manifest-digest'{$p.Files.$C00Manifest=('B'*64)}}
 Write-Fake $planPath $a;$raw=[IO.File]::ReadAllBytes($planPath);$hash=(Get-FileHash -LiteralPath $planPath).Hash
 Write-Fake (Join-Path $directory 'current-authorization.json') @{controlledFixture=$true;approvedPlanSha256=$hash;approvedCaptures=0;questionItemId='controlled-no-user-approval';approvedUtc=$t0.ToString('o');invariantManifestSha256=$manifestHash}
 Write-Fake (Join-Path $directory 'approval-release.json') @{controlledFixture=$true;kind='issue83-two-confirmation-release-v1';questionItemId='controlled-no-user-approval';approvedUtc=$t0.ToString('o');planSha256=$hash;invariantManifest=$manifestPath;invariantManifestSha256=$manifestHash;C00ControllerPath=$C00Controller;C00PlanSha256=(Get-FileHash -LiteralPath $C00Plan).Hash}
 $failure=$null;try{Assert-ConfirmationRelease}catch{$failure=$_.Exception.Message}
 if(($scenario-ceq'same-C00-exact')-eq[bool]$failure){throw "Actual v10 release fixture failed:$scenario $failure"}
 $results.Add(@{scenario=$scenario;passed=$true;rejection=$failure})
 if($clock.Elapsed.TotalSeconds-ge30){throw 'Pure30s bound'}
}
$bytes=(Get-ChildItem -LiteralPath $rootPath -Recurse -File|Measure-Object Length -Sum).Sum
if($bytes-gt4000000){throw 'Pure4MiB bound'}
Write-Fake (Join-Path $rootPath 'checks.json') @{result='passed-generated-v10-release';count=$results.Count;results=$results;seconds=$clock.Elapsed.TotalSeconds;bytes=$bytes;controlledFixture=$true;operationalManifestCreated=$false;mediaRead=$false;processesLaunched=0}
"Passed $($results.Count) actual v10 conditional-release JSON controls"
