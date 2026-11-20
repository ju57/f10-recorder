try{
 Bounds -Full
 $env:DOTNET_CLI_HOME=Join-Path $private cli;$env:TEMP=Join-Path $private tmp;$env:TMP=$env:TEMP;$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0';$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false';$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1';[IO.Directory]::CreateDirectory($env:TEMP)|Out-Null
 $start=[Diagnostics.ProcessStartInfo]::new($runtime[0]);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($arg in $controllerArguments){$start.ArgumentList.Add($arg)}
 $owned=[Diagnostics.Process]::Start($start);Register-ControllerCreatedRoot $owned;$actualRootHandle=Read-ControllerHandle $owned;$startTime=Read-ControllerBirth $owned
 if($null-eq$actualRootHandle-or$actualRootHandle.IsInvalid-or$actualRootHandle.IsClosed-or$null-eq$startTime){throw 'Held collector handle/birth unavailable'};$birth=$startTime.ToUniversalTime();Register-ControllerRoot $owned $birth
 Write-CollectorIdentityBinding
 $outFile=[IO.FileStream]::new($stdout,'CreateNew','Write',([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete),16384,[IO.FileOptions]::Asynchronous)
 $errFile=[IO.FileStream]::new($stderr,'CreateNew','Write',([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete),16384,[IO.FileOptions]::Asynchronous)
 $outCopy=$owned.StandardOutput.BaseStream.CopyToAsync($outFile);$errCopy=$owned.StandardError.BaseStream.CopyToAsync($errFile)
 while(-not(Join-ControllerProcess $owned 100 'poll')){
  if(Test-Path -LiteralPath $a.stopFile){throw 'User requested safe stop of owned cohort'}
  $owned.Refresh();$peakRam=[Math]::Max($peakRam,$owned.WorkingSet64);$peakHandles=[Math]::Max($peakHandles,$owned.HandleCount)
  if($clock.Elapsed.TotalSeconds-ge($wholeLimitSeconds-25)-or$peakRam-gt805306368-or$peakHandles-gt$a.parentHandleBudget){throw 'Collector bound with25second terminal reserve inside original authorization'}
  Bounds
  if($clock.Elapsed.TotalSeconds-$lastQuery-ge.5){$lastQuery=$clock.Elapsed.TotalSeconds;Observe-Children;Complete-Handoffs}
  if($clock.Elapsed.TotalSeconds-$lastPreparation-ge30){$lastPreparation=$clock.Elapsed.TotalSeconds;Bounds -Full}
 }
 $owned.WaitForExit();$exitCode=$owned.ExitCode;Resolve-PendingControlImages;Resolve-PendingCreatorImages;if($pendingControlImages.Count-or$pendingCreatorImages.Count){throw 'Unresolved closed creator/control proof prevents success'};if($exitCode-ne0){throw "Qualification failed:$exitCode; preserve all cases, no retry/refit"}
 Assert-ControllerCompleted;Assert-RecoveryWriterAndModel
} catch{$failure=$_.ToString()+' at '+$_.ScriptStackTrace}
finally{
 $cleanup=[Diagnostics.Stopwatch]::StartNew()
 function Remaining{return [Math]::Max(0,10000-[int]$cleanup.ElapsedMilliseconds)}
 try{Observe-Children}catch{$finalObservationError=$_.ToString();$failure+=" Final observation:$_"}
 try{if($owned-and-not$owned.HasExited){Stop-ControllerProcess $owned;if(-not(Join-ControllerProcess $owned (Remaining) 'cleanup')){throw 'Collector absence unconfirmed'}}}catch{$collectorCleanupError=$_.ToString();$failure+=" Collector cleanup:$_"}
 foreach($row in $tracked.Values){$forced=$false;$closed=$false;$childCode=$null;$closeError=$null;try{$forced=-not$row.process.HasExited;if($forced){$failure+=' Forced child cleanup required; qualification cannot pass';Stop-ControllerProcess $row.process};if(-not(Join-ControllerProcess $row.process (Remaining) 'cleanup')){throw 'Child absence unconfirmed'};$closed=$row.process.HasExited;$childCode=$row.process.ExitCode}catch{$closeError=$_.ToString();$failure+=" Child cleanup:$_"}finally{$childClosure+=[ordered]@{pid=$row.pid;birth=$row.birth;absent=$closed;forced=$forced;exitCode=$childCode;error=$closeError}}}
 foreach($name in $envNames){try{Restore-ControllerEnvironment $name $old[$name]}catch{$failure+=" Environment restore $name :$_"}}
 foreach($copy in @($outCopy,$errCopy)){if($copy){try{Drain-ControllerCopy $copy (Remaining)}catch{$failure+=" Log drain:$_"}}}
 try{if($outFile-and($null-eq$outCopy-or$outCopy.IsCompleted)){Close-ControllerStream $outFile 'stdout'}}catch{$failure+=" stdout disposal:$_"}
 try{if($errFile-and($null-eq$errCopy-or$errCopy.IsCompleted)){Close-ControllerStream $errFile 'stderr'}}catch{$failure+=" stderr disposal:$_"}
 try{Bounds -Full -Final}catch{$failure+=" Final bounds:$_"}
 $report=Join-Path $directory raw-child-observations.jsonl;$stream=$null
 try{
  $stream=[IO.FileStream]::new($report,'CreateNew','Write',[IO.FileShare]::Read)
  Write-ControllerRawReport $stream
 }catch{$failure+=" Raw report:$_"}finally{try{if($stream){Close-ControllerStream $stream 'raw'}}catch{$failure+=" Raw report stream disposal:$_"}}
 try{Write-GlobalAdmissionMap}catch{$failure+=" Global map:$_"}
 $terminalSeconds=$clock.Elapsed.TotalSeconds
 $terminal=[ordered]@{planHash=$hash;pid=$owned.Id;startedUtc=$birth;absent=(-not$owned-or$owned.HasExited);exitCode=$exitCode;seconds=$terminalSeconds;peakRam=$peakRam;peakHandles=$peakHandles;phaseBytesBeforeRaw=$phaseBytes;newManagedRows=$managedRows;trainingRetainedManagedRows=$p.RetainedChildren;priorFailedManagedRows=$a.priorFailedManagedRoles;retainedManagedRows=$a.retainedManagedRoles;cumulativeManagedRows=([int]$a.retainedManagedRoles+$managedRows);trainingRetainedElapsedSeconds=$p.RetainedElapsedSeconds;priorFailedElapsedSeconds=$a.priorFailedElapsedSeconds;retainedElapsedSeconds=($p.RetainedElapsedSeconds+$a.priorFailedElapsedSeconds);cumulativeElapsedSeconds=($p.RetainedElapsedSeconds+$a.priorFailedElapsedSeconds+$terminalSeconds);children=@($children.Values);observedInstances=@($observedInstances.Values);observedChildClosure=$childClosure;lookupAbsentChildren=@($lookupAbsentChildren.Values);queryMaximumMs=$queryMaximumMs;handoffs=$handoffs.ToArray();pendingControlProofCount=$pendingControlImages.Count;pendingCreatorProofCount=$pendingCreatorImages.Count;finalObservationError=$finalObservationError;collectorCleanupError=$collectorCleanupError;rawReport=@{rows=$rawChildren.Count;written=$rawWritten;bytes=$rawBytes;complete=$rawComplete};error=$failure;scope='Shared controller lifecycle receipt; acceptance scope comes from its reviewed entry. This receipt alone does not establish capture qualification, a storage model, observable Saved latency or performance acceptance'}
 try{Write-Terminal $terminal}catch{$failure+=" Terminal receipt write:$_"}
 try{Bounds -Full -Final;if(-not(Test-Path -LiteralPath $receipt)-or(Get-Item -LiteralPath $receipt).Length-gt262144){throw 'Receipt absent/256KiB bound'}}catch{$failure+=" Postreceipt:$_"}
 foreach($row in $tracked.Values){try{Dispose-ControllerProcess $row.process}catch{$failure+=" Child handle disposal:$_"}};try{if($owned){Dispose-ControllerProcess $owned}}catch{$failure+=" Collector handle disposal:$_"}
 # Always seal a post-disposal receipt, including clean closure and its elapsed
 # time. A pre-disposal successful result is never the terminal owner proof.
 $terminal.operation=$a.operation
 $terminal.processHandlesDisposed=($failure-notlike'*handle disposal*')
 $terminal.postDisposal=$true
 $terminalSeconds=$clock.Elapsed.TotalSeconds
 if($terminalSeconds-ge$wholeLimitSeconds-or[DateTime]::UtcNow-ge$deadline-or[DateTime]::UtcNow-ge$purposeDeadline){$failure+=' Post-disposal whole deadline'}
 $terminal.error=$failure;$terminal.seconds=$terminalSeconds;$terminal.cumulativeElapsedSeconds=$p.RetainedElapsedSeconds+$a.priorFailedElapsedSeconds+$terminalSeconds
 try{Write-Terminal $terminal}catch{$failure+=" Final post-disposal receipt write:$_"}
}
if($failure){throw $failure}
Get-Content -LiteralPath $receipt
