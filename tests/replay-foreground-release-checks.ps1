param([Parameter(Mandatory)][string]$Root)
$ErrorActionPreference='Stop'
$Root=[IO.Path]::GetFullPath($Root)
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))+[IO.Path]::DirectorySeparatorChar
if(-not $Root.StartsWith($artifacts,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path $Root)){throw 'Fresh isolated checks root required'}
New-Item -ItemType Directory -Path $Root|Out-Null
$controller=Join-Path $PSScriptRoot replay-foreground-release.ps1
$results=[Collections.Generic.List[object]]::new()
function Case([string]$Name,[scriptblock]$Change,[bool]$Reject=$true){
 $cell=Join-Path $Root $Name;$case=Join-Path $cell T01
 New-Item -ItemType Directory -Path $case -Force|Out-Null
 $challenge=[ordered]@{Label='T01';Pid=123;Hwnd=456;Nonce=('a'*32)}
 $ready=[ordered]@{label='T01';pid=123;hwnd=456;captureStarted=$false;release=$challenge}
 $files=[ordered]@{};$files[$controller]=(Get-FileHash $controller).Hash
 $admission=[pscustomobject]@{expiresUtc=[DateTime]::UtcNow.AddMinutes(5);plan=[pscustomobject]@{Root=$cell;Order=@('T01');CompositeQualification=$true;CompositeManualForegroundHandoff=$true;CompositeExplicitForegroundRelease=$true;Files=$files}}
 $context=[pscustomobject]@{plan=$admission;ready=$ready;case=$case;label='T01';wrongHash=$false}
 if($Change){& $Change $context}
 $ready|ConvertTo-Json -Depth 5|Set-Content (Join-Path $case foreground-handoff-ready.json)
 $planPath=Join-Path $cell plan.json;$admission|ConvertTo-Json -Depth 6|Set-Content $planPath
 $hash=(Get-FileHash $planPath).Hash;if($context.wrongHash){$hash='0'*64}
 $failed=$false;try{$null=& $controller -PlanFile $planPath -ExpectedPlanSha256 $hash -Label $context.label}catch{$failed=$true}
 if($failed -ne $Reject){throw "Unexpected release check result:$Name"}
 $final=Join-Path $case foreground-handoff-release.json
 if($Name -eq 'existing-final'){
  if([IO.File]::ReadAllText($final) -cne 'existing'){throw 'Existing final response changed'}
 }elseif($Reject -and (Test-Path $final)){throw 'Rejected controller published release'}
 if(@(Get-ChildItem $case -Filter '.foreground-release-*.tmp').Count){throw 'Owned temporary leaked'}
 if(-not $Reject){$actual=Get-Content $final -Raw|ConvertFrom-Json;if($actual.Nonce -cne $challenge.Nonce -or $actual.Pid -ne 123){throw 'Valid response differs from challenge'}}
 $results.Add([pscustomobject]@{name=$Name;passed=$true})
}
Case 'valid-atomic-response' $null $false
Case 'wrong-plan-hash' {param($c)$c.wrongHash=$true}
Case 'ordinary-mode' {param($c)$c.plan.plan.CompositeExplicitForegroundRelease=$false}
Case 'expired-plan' {param($c)$c.plan.expiresUtc=[DateTime]::UtcNow.AddSeconds(-1)}
Case 'wrong-script-binding' {param($c)$c.plan.plan.Files[$controller]='0'*64}
Case 'wrong-case' {param($c)$c.label='T02'}
Case 'wrong-window' {param($c)$c.ready.release.Hwnd=789}
Case 'missing-nonce' {param($c)$c.ready.release.Nonce=''}
Case 'oversized-challenge' {param($c)$c.ready.release.Nonce='a'*5000}
Case 'oversized-plan' {param($c)$c.plan|Add-Member -NotePropertyName padding -NotePropertyValue ('a'*70000)}
Case 'already-capturing' {param($c)[IO.File]::WriteAllText((Join-Path $c.case capture-arguments.json),'[]')}
Case 'handoff-complete' {param($c)[IO.File]::WriteAllText((Join-Path $c.case foreground-handoff-complete.json),'{}')}
Case 'existing-final' {param($c)[IO.File]::WriteAllText((Join-Path $c.case foreground-handoff-release.json),'existing')}
$results|ConvertTo-Json|Set-Content (Join-Path $Root checks.json)
Write-Output "Foreground release controller checks: $($results.Count)/$($results.Count) passed"
