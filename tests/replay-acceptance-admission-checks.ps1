param([switch]$Plan)
$ErrorActionPreference='Stop'
if($Plan){'Managed-only controls through actual child-handle adoption, sealed-pair threshold and pre-launch reseal loop. Injected process/storage objects, in-memory compilation; no native call, child process, UI, media, file output or settings change.';return}
function Read-CheckAst([string]$Name){
 $tokens=$null;$errors=$null
 $parsed=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $Name),[ref]$tokens,[ref]$errors)
 if($errors.Count){throw "Parse failure: $Name"};return $parsed
}
$pairAst=Read-CheckAst 'replay-acceptance-pair.ps1'
$adopt=@($pairAst.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Open-ReplayObservedChild'},$true))
if($adopt.Count -ne 1){throw 'Exact actual child-adoption function required.'}
. ([ScriptBlock]::Create($adopt[0].Extent.Text))
Add-Type -TypeDefinition @'
using System;
public sealed class ReplayAdmissionCheckProcess {
 public DateTime ExpectedBirth, ActualBirth;
 public bool Pinned, HandleFailure, BirthFailure;
 public int BirthReadsBeforePin, Disposes, Kills;
 public bool IsClosed=>false;
 public bool IsInvalid=>false;
 public object SafeHandle {get {if(HandleFailure)throw new InvalidOperationException("Injected pin failure");Pinned=true;return this;}}
 public DateTime StartTime {get {if(!Pinned){BirthReadsBeforePin++;return ExpectedBirth;}if(BirthFailure)throw new InvalidOperationException("Injected birth failure");return ActualBirth;}}
 public int Id=>123;
 public void Dispose(){Disposes++;}
 public void Kill(){Kills++;}
}
'@
function Get-ReplayUnboundChild {param($Id)if($script:case -ceq 'disappeared'){throw [ArgumentException]::new('Gone')};return $script:candidate}
$passed=0
foreach($case in @('valid','reused-birth','before-owner','pin-failure','birth-failure','disappeared')){
 $expected=[DateTime]::UtcNow;$ownerStart=$expected.AddSeconds(-1);$entry=$null;$rejected=$false
 $candidate=[ReplayAdmissionCheckProcess]::new();$candidate.ExpectedBirth=$expected;$candidate.ActualBirth=$expected
 if($case -ceq 'reused-birth'){$candidate.ActualBirth=$expected.AddSeconds(1)}
 if($case -ceq 'before-owner'){$ownerStart=$expected.AddSeconds(1)}
 $candidate.HandleFailure=$case -ceq 'pin-failure';$candidate.BirthFailure=$case -ceq 'birth-failure'
 try{$entry=Open-ReplayObservedChild 123 $expected $ownerStart}catch{$rejected=$true}
 # Only a transferred object is eligible for later owned cleanup.
 if($entry){$entry.process.Kill()}
 if($candidate.BirthReadsBeforePin -ne 0){throw "Birth read before retained handle: $case"}
 if($case -ceq 'valid'){
  if($rejected -or $null -eq $entry -or $candidate.Kills -ne 1 -or $candidate.Disposes -ne 0 -or $entry.startedUtc -ne $expected){throw 'Valid retained ownership transfer failed.'}
 }elseif($case -ceq 'disappeared'){
  if($rejected -or $entry -or $candidate.Disposes -ne 0 -or $candidate.Kills -ne 0){throw 'Disappeared PID was adopted or cleaned up.'}
 }elseif(-not $rejected -or $entry -or $candidate.Kills -ne 0 -or $candidate.Disposes -ne 1){throw "Rejected candidate was transferred or leaked: $case"}
 $passed++
}
$budgetAst=Read-CheckAst 'replay-acceptance-budget.ps1'
$threshold=@($budgetAst.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Assert-ReplaySealedPairBytes'},$true))
if($threshold.Count -ne 1){throw 'Exact actual sealed-pair assertion required.'}
. ([ScriptBlock]::Create($threshold[0].Extent.Text))
foreach($bytes in @(1999999999L,2000000000L,2000000001L,-1L)){
 $rejected=$false;try{Assert-ReplaySealedPairBytes $bytes}catch{$rejected=$true}
 if($rejected -ne ($bytes -lt 0 -or $bytes -gt 2000000000L)){throw "Sealed threshold mismatch: $bytes"};$passed++
}
$rejected=$false;try{Assert-ReplaySealedPairBytes 1L 2000000001L}catch{$rejected=$true}
if(-not $rejected){throw 'Changed experiment threshold accepted.'};$passed++
$batchAst=Read-CheckAst 'replay-acceptance-batch.ps1'
$reseal=@($batchAst.FindAll({param($n)$n -is [Management.Automation.Language.ForEachStatementAst] -and $n.Extent.Text.StartsWith('foreach($priorPair in $sealedPairs)')},$true))
if($reseal.Count -ne 1){throw 'Exact actual pre-launch reseal loop required.'}
function Measure-ReplayAcceptanceStorage {param($Roots)$script:scans++;[pscustomobject]@{bytes=$script:mockStorage[$Roots[0]]}}
foreach($grown in @(0,1,2)){
 $sealedPairs=@([pscustomobject]@{number=1;root='first';plan='first/plan.json'},[pscustomobject]@{number=2;root='second';plan='second/plan.json'})
 $mockStorage=@{first=2000000000L;second=1999999999L};if($grown){$mockStorage[$sealedPairs[$grown-1].root]=2000000001L}
 $p=[pscustomobject]@{sealedPairBytes=2000000000L};$activePair=$null;$scans=0;$launches=0;$rejected=$false
 try{. ([ScriptBlock]::Create($reseal[0].Extent.Text));$launches++}catch{$rejected=$true}
 if($grown){if(-not $rejected -or $launches -ne 0 -or $scans -ne $grown -or $activePair.number -ne $grown){throw "Prior sealed growth failed to stop before next launch: grown=$grown rejected=$rejected launches=$launches scans=$scans active=$($activePair.number)"}}
 elseif($rejected -or $launches -ne 1 -or $scans -ne 2 -or $activePair){throw 'Valid retained pairs failed admission.'}
 $passed++
}
"Passed $passed managed acceptance admission controls."
