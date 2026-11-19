param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run,[switch]$Release,[int]$Pair)
$ErrorActionPreference='Stop'
if(([int]$Plan.IsPresent+[int]$Run.IsPresent+[int]$Release.IsPresent) -ne 1){throw 'Choose Plan, Run or Release.'}
$path=(Resolve-Path -LiteralPath $PlanFile).Path
if(([IO.FileInfo]::new($path)).Length -gt 65536){throw 'Batch plan64KiB cap.'}
$p=Get-Content -Raw -LiteralPath $path|ConvertFrom-Json;$sha=(Get-FileHash -LiteralPath $path).Hash
if($p.kind -cne 'issue83-acceptance-ten-pairs-v2' -or $p.pairs.Count -ne 10 -or $p.maximumAcquisitions -ne 10 -or $p.deadlineSeconds -ne 9060 -or
  $p.budgetBytes -ne 31000000000L -or $p.stopBytes -ne 30500000000L -or $p.minimumFreeBytes -ne 60000000000L -or $p.sealedPairBytes -ne 2000000000L){throw 'Exact finite ten-pair conditional-retention admission required.'}
if((Get-FileHash -LiteralPath $p.hardwareFile).Hash -cne $p.hardwareSha256){throw 'Frozen reference hardware metadata changed.'}
$artifactPrefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))+[IO.Path]::DirectorySeparatorChar
foreach($root in @($p.root,$p.admission)){if(-not [IO.Path]::IsPathFullyQualified($root) -or -not $root.StartsWith($artifactPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Private artifact roots required.'}}
$phases=@(.10,.60,.30,.80,.50,.00,.70,.20,.90,.40)
function Assert-ProductionPreservation {
 if($p.productionFiles.Count -ne 12){throw 'All12 production bindings required.'}
 foreach($file in $p.productionFiles){
  $exists=Test-Path -LiteralPath $file.path;$observed=$null
  if($exists){
   $reader=[IO.FileStream]::new($file.path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
   try{$observed=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($reader))}finally{$reader.Dispose()}
  }
  if($exists -ne $file.exists -or $observed -cne $file.sha256){throw "Production binding changed: $($file.path)"}
 }
}
for($i=0;$i -lt 10;$i++){
 $item=$p.pairs[$i]
 if($item.number -ne $i+1 -or $item.phase -ne $phases[$i] -or (Get-FileHash -LiteralPath $item.plan).Hash -cne $item.sha256){throw 'Frozen pair order/phase/bytes changed.'}
 if($item.root -ine (Join-Path $p.root ('pair-'+($i+1).ToString('00')))){throw 'Frozen pair root changed.'}
}
if($Plan){$p|ConvertTo-Json -Depth 8;return}
if($sha -cne $ExpectedPlanSha256){throw 'Exact reviewed complete batch required.'}
$actualHost=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
if(-not [string]::Equals($actualHost,$p.hardware.controllerHost,[StringComparison]::OrdinalIgnoreCase)){throw 'Launch with the frozen native PowerShell host.'}
if($Release){
 if($Pair -lt 1 -or $Pair -gt 10){throw 'Select exact pending pair1..10.'}
 $admitted=Get-Content -Raw -LiteralPath (Join-Path $p.root 'batch-admitted.json')|ConvertFrom-Json
 if($admitted.planSha256 -cne $sha -or [DateTime]::UtcNow -ge [DateTime]$admitted.deadlineUtc -or
  [DateTime]$admitted.deadlineUtc -gt ([DateTime]$p.createdUtc).AddHours(4)){throw 'Original admitted fixed batch deadline required for release.'}
 $owner=[Diagnostics.Process]::GetProcessById($admitted.controllerPid)
 try{$null=$owner.SafeHandle;if($owner.HasExited -or $owner.StartTime.ToUniversalTime() -ne [DateTime]$admitted.controllerBirth){throw 'Exact admitted running controller required.'}}finally{$owner.Dispose()}
 $item=$p.pairs[$Pair-1]
 & (Join-Path $PSScriptRoot 'replay-acceptance-pair.ps1') -PlanFile $item.plan -ExpectedPlanSha256 $item.sha256 -Release
 return
}
if([DateTime]::UtcNow.AddSeconds(9060) -ge ([DateTime]$p.createdUtc).AddHours(4)){throw 'Fresh full9060second horizon required only before initial Run.'}
if(Test-Path -LiteralPath $p.root){throw 'Preserve prior whole-batch attempt.'}
if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($p.root)).AvailableFreeSpace -lt 92000000000L){throw '31GB batch +1GB preparation +60GBfloor must fit before first capture.'}
. (Join-Path $PSScriptRoot 'replay-acceptance-budget.ps1')
[IO.Directory]::CreateDirectory($p.root)|Out-Null
$self=[Diagnostics.Process]::GetCurrentProcess();$clock=[Diagnostics.Stopwatch]::StartNew();$values=[Collections.Generic.List[object]]::new();$sealedPairs=[Collections.Generic.List[object]]::new();$failure=$null;$completed=0;$maxBytes=0L;$activePair=$null;$partial=$null
@{planSha256=$sha;controllerPid=$self.Id;controllerBirth=$self.StartTime.ToUniversalTime();utc=[DateTime]::UtcNow;deadlineUtc=[DateTime]::UtcNow.AddSeconds(9060)}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $p.root 'batch-admitted.json')
try{
 foreach($item in $p.pairs){
  foreach($priorPair in $sealedPairs){
   $retained=Measure-ReplayAcceptanceStorage @($priorPair.root,(Split-Path -Parent $priorPair.plan))
   try{Assert-ReplaySealedPairBytes $retained.bytes $p.sealedPairBytes}catch{$activePair=$priorPair;throw}
  }
  $activePair=$item
  Assert-ProductionPreservation
  if($clock.Elapsed.TotalSeconds -gt 8160){throw 'One full900second pair must remain in overall9060second deadline.'}
  $inventory=Assert-ReplayAcceptanceStorage $p;$maxBytes=[Math]::Max($maxBytes,$inventory.bytes)
  & (Join-Path $PSScriptRoot 'replay-acceptance-pair.ps1') -PlanFile $item.plan -ExpectedPlanSha256 $item.sha256 -Run
  $closure=Get-Content -Raw -LiteralPath (Join-Path $item.root 'closure.json')|ConvertFrom-Json
  $result=Get-Content -Raw -LiteralPath (Join-Path $item.root 'result.json')|ConvertFrom-Json
  if($closure.error -or $closure.cleanupErrors.Count -ne 0 -or -not $closure.workerJoined -or -not $closure.captureAbsent -or $result.results.Count -ne 2){throw 'Incomplete pair closure/results; stop, preserve full12GB failed slot.'}
  if(@(Get-ChildItem -LiteralPath (Join-Path $item.root 'cache') -File -Recurse).Count -ne 0){throw 'Source media remained after ordinary retirement; no sealed-pair budget claim.'}
  $pairAdmission=Split-Path -Parent $item.plan
  $sealed=Measure-ReplayAcceptanceStorage @($item.root,$pairAdmission)
  Assert-ReplaySealedPairBytes $sealed.bytes $p.sealedPairBytes
  Assert-ProductionPreservation
  foreach($sample in $result.results){$values.Add([pscustomobject]@{pair=$item.number;phase=$item.phase;label=$sample.label;milliseconds=$sample.requestToSavedMilliseconds;result=(Join-Path $item.root ($sample.label+'.json'))})}
  $completed++
  $sealedPairs.Add($item)
  $activePair=$null
 }
}catch{$failure=$_.ToString()}
finally{
 if($activePair){
  $observed=@(foreach($label in @('first-use','repeat-use')){
   $samplePath=Join-Path $activePair.root ($label+'.json')
   if(Test-Path -LiteralPath $samplePath){
    try{if(([IO.FileInfo]::new($samplePath)).Length -gt 8388608){throw 'Partial sample8MiB cap.'};$sample=Get-Content -Raw -LiteralPath $samplePath|ConvertFrom-Json;
     [pscustomobject]@{label=$label;milliseconds=$sample.requestToSavedMilliseconds;path=$samplePath;classification='Observed Saved from incomplete/invalid pair; retained, excluded from20-success quantile'}
    }catch{[pscustomobject]@{label=$label;path=$samplePath;parseError=$_.ToString()}}
   }
  })
  $partial=[pscustomobject]@{pair=$activePair.number;phase=$activePair.phase;root=$activePair.root;observedSaved=$observed;
   attemptEvidence=Join-Path $activePair.root 'last-stage.json';closureEvidence=Join-Path $activePair.root 'closure.json';controllerEvidence=Join-Path (Split-Path -Parent $activePair.plan) 'controller.json';fullFailedPromiseBytes=12000000000L}
 }
 $productionPreserved=$false;try{Assert-ProductionPreservation;$productionPreserved=$true}catch{$failure="$failure Production preservation:$_"}
 try{$inventory=Assert-ReplayAcceptanceStorage $p;$maxBytes=[Math]::Max($maxBytes,$inventory.bytes)}catch{$failure="$failure Final storage:$_"}
 $sorted=@($values|Sort-Object milliseconds);$median=$null;$p95=$null
 if($completed -eq 10 -and $sorted.Count -eq 20){$median=($sorted[9].milliseconds+$sorted[10].milliseconds)/2;$p95=$sorted[18].milliseconds}
 $strata=@(foreach($label in @('first-use','repeat-use')){
  $group=@($values|Where-Object label -eq $label|Sort-Object milliseconds)
  [pscustomobject]@{label=$label;count=$group.Count;medianMilliseconds=if($group.Count -eq 10){($group[4].milliseconds+$group[5].milliseconds)/2}else{$null};p95Milliseconds=if($group.Count -eq 10){$group[9].milliseconds}else{$null}}
 })
 $differences=@(foreach($number in 1..10){$first=@($values|Where-Object {$_.pair -eq $number -and $_.label -ceq 'first-use'});$repeat=@($values|Where-Object {$_.pair -eq $number -and $_.label -ceq 'repeat-use'});if($first.Count -eq 1 -and $repeat.Count -eq 1){[pscustomobject]@{pair=$number;repeatMinusFirstMilliseconds=$repeat[0].milliseconds-$first[0].milliseconds}}})
 $summary=[pscustomobject]@{planSha256=$sha;completedPairs=$completed;samples=$values.ToArray();medianMilliseconds=$median;p95Milliseconds=$p95;
  strata=$strata;pairedDifferences=$differences;
  partialFailedPair=$partial;
  speedPass=($null -ne $median -and $median -le 3000 -and $p95 -le 5000 -and -not $failure);seconds=$clock.Elapsed.TotalSeconds;maxBytes=$maxBytes;productionPreserved=$productionPreserved;error=$failure;
  scope='Request through client-observed Saved; ten fresh worker first/repeat pairs, actual hidden UI and continued capture. Correlated pairs, no OS-cache-cold/physical-feedback/gaming/1or30minute/resource/release claim. Resource report requires independent coverage/accounting review.'}
 $summary|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $p.root 'batch-result.json');$self.Dispose()
}
if($failure){throw $failure}
