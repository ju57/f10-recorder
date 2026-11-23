param([switch]$Plan)
$ErrorActionPreference='Stop'
if($Plan){'Four managed-only controls through actual generated-producer adoption: reused birth/wrong image/exited/valid. Fake retained objects; no native API/process/UI/media/filesystem mutation. Failed adoption must dispose only and never make replacement cleanup-owned.';return}
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'replay-observer-gpu-checks.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Generated controller parse failed.'}
$functions=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Adopt-ReplayGeneratedProducer'},$true))
if($functions.Count -ne 1){throw 'Exact actual adoption function required.'};. ([ScriptBlock]::Create($functions[0].Extent.Text))
function Get-ReplayGeneratedCandidate {param($Id)return $script:candidate}
function Get-ReplayGeneratedImage {param($Process)return $Process.Image}
$passed=0
foreach($case in @('reused-birth','wrong-image','exited','valid')){
 $killCount=0;$disposeCount=0;$birth=[DateTime]::UtcNow;$expected='C:\pinned\ffmpeg.exe';$producer=$null
 $candidate=[pscustomobject]@{SafeHandle=[object]::new();HasExited=($case -ceq 'exited');StartTime=if($case -ceq 'reused-birth'){$birth.AddSeconds(1)}else{$birth};Image=if($case -ceq 'wrong-image'){'C:\unrelated.exe'}else{$expected}}
 $candidate|Add-Member -MemberType ScriptMethod -Name Kill -Value {$script:killCount++}
 $candidate|Add-Member -MemberType ScriptMethod -Name Dispose -Value {$script:disposeCount++}
 $rejected=$false
 try{$producer=Adopt-ReplayGeneratedProducer ([pscustomobject]@{pid=123;birthUtc=$birth}) $expected}catch{$rejected=$true}
 # This is the controller's cleanup ownership boundary: only the returned
 # adopted object can enter a later owned stop, never a failed candidate.
 if($producer -and -not $producer.HasExited){$producer.Kill()}
 if($case -ceq 'valid'){if($rejected -or $null -eq $producer -or $killCount -ne 1 -or $disposeCount -ne 0){throw 'Valid ownership transfer failed.'}}
 elseif(-not $rejected -or $null -ne $producer -or $killCount -ne 0 -or $disposeCount -ne 1){throw "Failed adoption made unrelated process cleanup-owned: $case"}
 $passed++
}
"Passed $passed generated-producer ownership controls."
