param([Parameter(Mandatory)][string]$AssemblyPath,[Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference = 'Stop'
if($Plan -eq $Run){throw 'Choose -Plan or -Run.'}
if($Plan){return [pscustomobject]@{Children=66;MaximumConcurrentMedia=33;Seconds=600;Cap=16000000000;Stop=15000000000;Floor=60000000000;ChildRamCap=6000000000;Effects='CPU-only offline full-output decoder shard groups2/8/16/32, complete global packet clock and audio decode, one copy-to-null diagnostic. No capture/GPU/playback/worker/UI/Saved/preferences/catalog/install.'}}
if(-not $ExpectedPlanSha256 -or (Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256){throw 'Reviewed plan hash required.'}
$p=Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
if($p.kind -ne 'issue83-parallel-full-pilot-v1' -or $p.files.PSObject.Properties.Name -notcontains [IO.Path]::GetFullPath($AssemblyPath)){throw 'Wrong bound helper/plan.'}
foreach($f in $p.files.PSObject.Properties){if((Get-FileHash -LiteralPath $f.Name).Hash -ne $f.Value){throw "Bound input changed: $($f.Name)"}}
$receipt="$PlanFile.controller.json";if(Test-Path -LiteralPath $receipt){throw 'Existing attempt; preserve it.'}
$sdk=Join-Path (Split-Path $PSScriptRoot) '.tools/dotnet/dotnet.exe'
$ownedArgs=@([IO.Path]::GetFullPath($AssemblyPath),'--replay-parallel-pilot-run',[IO.Path]::GetFullPath($PlanFile),$ExpectedPlanSha256)
$quoted=$ownedArgs|ForEach-Object{if($_.Contains('"')){throw 'Quotes unsupported.'};'"'+$_+'"'}
$clock=[Diagnostics.Stopwatch]::StartNew();$owned=$null;$exit=$null;$failure=$null
@{state='admitted';utc=[datetime]::UtcNow;sha256=$ExpectedPlanSha256}|ConvertTo-Json|Set-Content -LiteralPath $receipt
try{
 $owned=Start-Process -FilePath $sdk -ArgumentList $quoted -WindowStyle Hidden -WorkingDirectory (Split-Path $PSScriptRoot) -PassThru -RedirectStandardOutput "$PlanFile.stdout.txt" -RedirectStandardError "$PlanFile.stderr.txt"
 while(-not $owned.WaitForExit(200)){
  if($clock.Elapsed.TotalSeconds -gt 595){throw 'Parallel pilot deadline;5s cleanup remains.'}
  if((Get-Item -LiteralPath "$PlanFile.stdout.txt").Length+(Get-Item -LiteralPath "$PlanFile.stderr.txt").Length -gt 131072){throw 'Controller log cap.'}
 }
 $exit=$owned.ExitCode;if($exit -ne 0){throw "Parallel pilot failed $exit; retain all stages."}
}catch{$failure=$_.Exception.Message;throw}
finally{
 if($owned -and -not $owned.HasExited){$owned.Kill($true);if(-not $owned.WaitForExit(5000)){$failure='Owned tree exit unconfirmed.'}}
 @{exit=$exit;error=$failure;elapsedSeconds=$clock.Elapsed.TotalSeconds;pid=if($owned){$owned.Id}else{$null};confirmedExited=if($owned){$owned.HasExited}else{$true}}|ConvertTo-Json|Set-Content -LiteralPath $receipt
 if($owned){$owned.Dispose()}
}
