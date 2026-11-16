param([Parameter(Mandatory)][string]$AssemblyPath,[Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference='Stop'
if($Plan -eq $Run){throw 'Choose -Plan or -Run.'}
if($Plan){
 if(Test-Path -LiteralPath $PlanFile){$preview=Get-Content -LiteralPath $PlanFile -Raw|ConvertFrom-Json;return [pscustomobject]@{Helpers=$preview.maximumChildren;MediaChildrenConcurrent=1;DecoderQueues=$preview.modes;Seconds=$preview.deadlineSeconds;Cap=$preview.capBytes;Stop=$preview.stopBytes;Floor=$preview.floorBytes;ChildRam=$preview.maximumChildRamBytes;Effects=$preview.effects}}
 return [pscustomobject]@{Helpers=5;MediaChildrenConcurrent=1;DecoderQueues=@(1,4,8,16);Seconds=360;Cap=16000000000;Stop=15000000000;Floor=60000000000;ChildRam=3000000000;Effects='Four serial software full closed-MP4 decoder helpers using pinned test DLLs in a private root. No GPU/capture/playback/worker/UI/publication/preferences/catalog/install.'}
}
if(-not $ExpectedPlanSha256 -or (Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256){throw 'Reviewed exact plan required.'}
$p=Get-Content -LiteralPath $PlanFile -Raw|ConvertFrom-Json
if($p.files.PSObject.Properties.Name -notcontains [IO.Path]::GetFullPath($AssemblyPath)){throw 'Wrong bound coordinator.'}
foreach($f in $p.files.PSObject.Properties){if((Get-FileHash -LiteralPath $f.Name).Hash -ne $f.Value){throw "Bound file changed: $($f.Name)"}}
$receipt="$PlanFile.controller.json";if(Test-Path -LiteralPath $receipt){throw 'Existing native cohort; retain it.'}
$sdk=Join-Path (Split-Path $PSScriptRoot) '.tools/dotnet/dotnet.exe'
$ownedArgs=@([IO.Path]::GetFullPath($AssemblyPath),'--native-api-run',[IO.Path]::GetFullPath($PlanFile),$ExpectedPlanSha256)
$quoted=$ownedArgs|ForEach-Object{if($_.Contains('"')){throw 'Quotes unsupported.'};'"'+$_+'"'}
$clock=[Diagnostics.Stopwatch]::StartNew();$owned=$null;$exit=$null;$failure=$null
@{state='admitted';sha256=$ExpectedPlanSha256;utc=[datetime]::UtcNow}|ConvertTo-Json|Set-Content -LiteralPath $receipt
try{
 $owned=Start-Process -FilePath $sdk -ArgumentList $quoted -WindowStyle Hidden -WorkingDirectory (Split-Path $PSScriptRoot) -PassThru -RedirectStandardOutput "$PlanFile.stdout.txt" -RedirectStandardError "$PlanFile.stderr.txt"
 while(-not $owned.WaitForExit(200)){
  if($clock.Elapsed.TotalSeconds -gt ($p.deadlineSeconds-5)){throw 'Whole native cohort deadline;5s cleanup remains.'}
  if((Get-Item -LiteralPath "$PlanFile.stdout.txt").Length+(Get-Item -LiteralPath "$PlanFile.stderr.txt").Length -gt 131072){throw 'Native controller log cap.'}
 }
 $exit=$owned.ExitCode;if($exit -ne 0){throw "Native API cohort failed $exit; preserve all evidence."}
}catch{$failure=$_.Exception.Message;throw}
finally{
 if($owned -and -not $owned.HasExited){$owned.Kill($true);if(-not $owned.WaitForExit(5000)){$failure='Owned native tree exit unconfirmed.'}}
 @{exit=$exit;error=$failure;elapsedSeconds=$clock.Elapsed.TotalSeconds;pid=if($owned){$owned.Id}else{$null};confirmedExited=if($owned){$owned.HasExited}else{$true}}|ConvertTo-Json|Set-Content -LiteralPath $receipt
 if($owned){$owned.Dispose()}
}
