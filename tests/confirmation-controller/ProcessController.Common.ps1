function Read-Bounded([string]$Path,[int]$Maximum,[switch]$AllowEmpty){
 $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete))
 try{if(($stream.Length-le0-and-not$AllowEmpty)-or$stream.Length-gt$Maximum){throw 'Bounded input length'};$bytes=[byte[]]::new([int]$stream.Length);$stream.ReadExactly($bytes);return ,$bytes}finally{$stream.Dispose()}
}

function Read-Handoff([string]$Path){
 $bytes=Read-Bounded $Path 4096 -AllowEmpty
 if($bytes.Length-eq0){return $null}
 try{return [Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json}catch [ArgumentException]{return $null} # Producer may still be flushing; helper's handoff timeout remains binding.
}

function Write-Terminal($Snapshot){
 $data=$null;$encodingError=$null
 try{$data=[Text.Encoding]::UTF8.GetBytes(($Snapshot|ConvertTo-Json -Depth 7))}catch{$encodingError=$_.ToString()}
 if($encodingError-or$data.Length-gt262144){
  $primary=[string]$Snapshot.error
  $script:failure+=' Terminal receipt required bounded failure representation'
  $prefix=$primary.Substring(0,[Math]::Min(8192,$primary.Length))
  $fallback=[ordered]@{planHash=$Snapshot.planHash;pid=$Snapshot.pid;startedUtc=$Snapshot.startedUtc;absent=$Snapshot.absent;exitCode=$Snapshot.exitCode;seconds=$Snapshot.seconds;
   retainedManagedRows=$Snapshot.retainedManagedRows;cumulativeManagedRows=$Snapshot.cumulativeManagedRows;retainedElapsedSeconds=$Snapshot.retainedElapsedSeconds;cumulativeElapsedSeconds=$Snapshot.cumulativeElapsedSeconds;error='Terminal receipt could not fit or serialize; qualification failed';primaryErrorPrefix=$prefix;primaryErrorCharacters=$primary.Length;
   completeReceiptBytes=if($data){$data.Length}else{$null};completeReceiptSha256=if($data){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($data))}else{$null};
   encodingErrorPrefix=if($encodingError){$encodingError.Substring(0,[Math]::Min(8192,$encodingError.Length))}else{$null};
   observedClosureRows=@($Snapshot.observedChildClosure).Count;confirmedAbsentRows=@($Snapshot.observedChildClosure|Where-Object {$_.absent-eq$true}).Count;boundedFailure=$true;
   scope='Bounded terminal failure; full in-memory receipt was not written; retained raw/helper evidence remains separate'}
  $data=[Text.Encoding]::UTF8.GetBytes(($fallback|ConvertTo-Json -Depth 5))
 }
 if($data.Length-gt262144){throw 'Terminal failure representation exceeds256KiB before write'}
 Write-ControllerBytes $receipt $data
}

function Test-LookupAbsenceSnapshot($Fact,[DateTime]$ParentBirth,[bool]$ParentStillAlive,[string]$ExceptionCall,[string]$ExceptionType){
 if(-not$ParentStillAlive-or$ExceptionCall-cne'Process.GetProcessById(Int32)'-or$ExceptionType-cne'System.ArgumentException'){return $false}
 if($Fact.ProcessId-le0-or$Fact.ProcessId-gt[int]::MaxValue-or([DateTimeOffset]$Fact.CreationDate).UtcDateTime-lt$ParentBirth-or[string]::IsNullOrEmpty($Fact.ExecutablePath)){return $false}
 return $Fact.ExecutablePath-ieq$runtime[0]-or$Fact.ExecutablePath-ieq$engine[0]-or$Fact.ExecutablePath-ieq$consoleImage
}

function Test-ControlReceipt($Document,[string]$Name,[int]$ChildPid,[DateTime]$ChildBirth,[int]$ChildExit){
 $expected=@(switch -CaseSensitive ($Name){
  'version'{@('-version')};'nonzero'{@('-this-option-is-not-real')};'timeout'{@('-hide_banner','-nostdin','-re','-f','lavfi','-i','sine','-t','10','-f','null','-')};'output-limit'{@('-version')};default{throw 'Unknown control proof name'}
 })
 $r=$Document.receipt
 if($r.ProcessId-ne$ChildPid-or([DateTimeOffset]$r.ProcessStartedUtc).UtcDateTime-ne$ChildBirth){return $false}
 $actualArguments=@($Document.arguments)
 if($actualArguments.Count-ne$expected.Count){throw 'Control creator argument count changed'}
 for($i=0;$i-lt$expected.Count;$i++){if($actualArguments[$i]-isnot[string]-or$actualArguments[$i]-cne$expected[$i]){throw 'Control creator argument value changed'}}
 if($r.ExitCode-ne$ChildExit-or$r.ProducerAbsent-ne$true-or$r.ActualExitObserved-ne$true-or$r.ResourcesFinal-ne$true-or$r.ResourceError){throw 'Matching control creator receipt has invalid closure or resources'}
 $limit=if($Name-ceq'output-limit'){128}else{2097152}
 if($r.CapturedBytes-lt0-or$r.CapturedBytes-gt$limit){throw 'Creator receipt diagnostic byte cap'}
 $valid=switch -CaseSensitive ($Name){
  'version'{$r.Success-eq$true-and$r.ExitCode-eq0-and-not$r.Failure-and$r.CaptureComplete-eq$true-and-not$r.TimedOut-and-not$r.OutputLimitExceeded}
  'nonzero'{$r.Success-eq$false-and$r.ExitCode-ne0-and$r.Failure-ceq'exit-nonzero'-and-not$r.TimedOut-and-not$r.OutputLimitExceeded}
  'timeout'{$r.Success-eq$false-and$r.Failure-ceq'timeout'-and$r.TimedOut-eq$true-and-not$r.OutputLimitExceeded}
  'output-limit'{$r.Success-eq$false-and$r.Failure-ceq'output-limit'-and$r.OutputLimitExceeded-eq$true-and-not$r.TimedOut}
 }
 if(-not$valid){throw 'Matching control creator receipt does not prove its intended outcome'}
 return $true
}

function Read-ControlImageProof([string]$Key){
 if(-not$tracked.ContainsKey($Key)-or-not$tracked[$Key].process.HasExited){return $null}
 $held=$tracked[$Key];$matches=@()
 foreach($name in @('version','nonzero','timeout','output-limit')){
  $file=Join-Path $p.Root "controls/$name.json"
  if(-not(Test-Path -LiteralPath $file)){continue}
  $bytes=Read-ControllerProofBytes $file
  if($null-eq$bytes){continue} # Producer may still hold its writer; fixed pending deadline is not refreshed.
  if($bytes.Length-eq0){continue}
  try{$document=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json}catch [ArgumentException]{continue}
  if(Test-ControlReceipt $document $name $held.pid $held.birth $held.process.ExitCode){$matches+=@{path=$file;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));kind=$name}}
 }
 if($matches.Count-gt1){throw 'Duplicate creator proof for one exact held child'}
 if($matches.Count-eq1){return $matches[0]};return $null
}

function Resolve-PendingControlImages {
 foreach($key in @($pendingControlImages.Keys)){
  $pending=$pendingControlImages[$key]
  $unheld=$pending-is[Collections.IDictionary]-and$pending.unheld-eq$true
  $started=if($unheld){[double]$pending.started}else{[double]$pending}
  if((Get-ControllerPolicyMilliseconds)-$started-ge1000){throw 'Closed control image proof exceeded fixed1second deadline'}
  $proof=if($unheld){Read-UnheldControlImageProof $pending}else{Read-ControlImageProof $key}
  if((Get-ControllerPolicyMilliseconds)-$started-ge1000){throw 'Closed control image proof exceeded work-inclusive1second deadline'}
  if($proof){$children[$key].path=$engine[0];$children[$key].creatorReceiptVerified=$true;$children[$key].imageEvidence='verified-creation-receipt';$children[$key].creatorProof=$proof;
   if($pending -is [Collections.IDictionary] -and $pending.handleExit){
    $children[$key].closureEvidence='creator-confirmed-exit-before-handle'
   }elseif($unheld){$children[$key].imageEvidence='verified-unheld-closed-control-receipt';$lookupAbsentChildren[$key].verifiedImage=$engine[0];$lookupAbsentChildren[$key].creatorProof=$proof;$lookupAbsentChildren[$key].closureEvidence='typed-local-lookup-absence-plus-control-creator-proof'}
   $pendingControlImages.Remove($key)}
 }
}

function Resolve-ExitedChildImage([string]$Key,[string]$CimImage){
 if(-not[string]::IsNullOrEmpty($CimImage)){return $CimImage}
 if(-not$tracked.ContainsKey($Key)-or-not$children.ContainsKey($Key)){return $null}
 if(-not$tracked[$Key].process.HasExited-or($children[$Key].heldImageObserved-ne$true-and$children[$Key].creatorReceiptVerified-ne$true)){return $null}
 $cached=$children[$Key].path
 if($cached-ine$runtime[0]-and$cached-ine$engine[0]-and$cached-ine$consoleImage){return $null}
 return $cached # Same PID/birth key and exact held exited instance; never infer ownership from a blank CIM image.
}

function Observe-Children {
 Resolve-PendingControlImages
 Resolve-PendingCreatorImages
 if(-not(Test-ControllerParentAlive)){return}
 if($null-eq$birth){throw 'Held collector birth unavailable; never adopt children by parent PID alone'}
 $query=[Diagnostics.Stopwatch]::StartNew()
 $facts=@(Get-ControllerFacts $owned)
 $script:queryMaximumMs=[Math]::Max($script:queryMaximumMs,$query.Elapsed.TotalMilliseconds)
 if(-not(Test-ControllerParentAlive)){return} # Never open children of a reused, exited parent PID.
 $current=0;$media=0;$support=0
 foreach($fact in $facts){
  $created=([DateTimeOffset]$fact.CreationDate).UtcDateTime
  if($fact.ProcessId-le0-or$fact.ProcessId-gt[int]::MaxValue){throw 'Invalid local child PID snapshot'}
  Add-ControllerRawObservation $fact $created
  if($created-lt$birth){continue}
  $key="$($fact.ProcessId)/$($created.Ticks)";$current++
  if(-not$observedInstances.ContainsKey($key)){$observedInstances[$key]=[ordered]@{pid=$fact.ProcessId;birth=$created;path=$fact.ExecutablePath;peakWorkingSet=[long]$fact.WorkingSetSize;consoleSnapshot=($fact.ExecutablePath-ieq$consoleImage)}}
  else{$observedInstances[$key].peakWorkingSet=[Math]::Max($observedInstances[$key].peakWorkingSet,[long]$fact.WorkingSetSize)}
  $seenSupport=@($observedInstances.Values|Where-Object {$_.consoleSnapshot}).Count
  if($current-gt4-or$seenSupport-gt1-or$observedInstances.Count-$seenSupport-gt([int]$p.MaximumChildren-[int]$p.RetainedChildren-1)-or$fact.WorkingSetSize-gt2147483648-or($fact.ExecutablePath-ieq$consoleImage-and$fact.WorkingSetSize-gt67108864)){throw 'Observed snapshot current/cumulative/resource bound before acquisition'}
  if(-not$tracked.ContainsKey($key)){
   $process=$null
   $lookupAbsent=$lookupAbsentChildren.ContainsKey($key)
   $lookupPid=[int]$fact.ProcessId
   $exitAtHandle=$children.ContainsKey($key) -and $children[$key].exitAtHandle
   if(-not($lookupAbsent -or $exitAtHandle)){try{$process=Open-ControllerProcess $lookupPid}catch [ArgumentException]{
    $actualType=$_.Exception.GetType().FullName
    # PowerShell wraps static method exceptions; only its exact inner ArgumentException qualifies.
    if($_.Exception.InnerException){$actualType=$_.Exception.InnerException.GetType().FullName}
    $parentAlive=-not$owned.HasExited
    if(-not(Test-LookupAbsenceSnapshot $fact $birth $parentAlive 'Process.GetProcessById(Int32)' $actualType)){
     if((Test-CreatorProofMode)-and$actualType-ceq'System.ArgumentException'-and$parentAlive-and[string]::IsNullOrEmpty($fact.ExecutablePath)){
      if(-not(Get-CreatorCase)-and(Test-UnheldControlContext)){Add-PendingUnheldControlImage $key $fact}
      else{Add-PendingCreatorImage $key $fact $true}
     }else{throw ("Unproven lookup absence; no PID-only action; type=$actualType; parentAlive=$parentAlive; pid=$lookupPid; birth=$($created.ToString('o')); image=$($fact.ExecutablePath); original=$($_.Exception.Message)")}
    }
    $lookupAbsent=$true
    $lookupAbsentChildren[$key]=[ordered]@{pid=[int]$fact.ProcessId;birth=$created;path=$fact.ExecutablePath;imageEvidence='complete-cim-snapshot';closureEvidence='local-typed-lookup-absence';observedUtc=[DateTime]::UtcNow.ToString('o');exceptionCall='Process.GetProcessById(Int32)';exceptionType=$actualType;exceptionMessage=$_.Exception.Message;heldHandleAcquired=$false;exitCode=$null;neverReopenOrStopByPid=$true}
    if($pendingCreatorImages.ContainsKey($key)){$lookupAbsentChildren[$key].imageEvidence='blank-image-awaiting-retrospective-proof'}
    if($pendingControlImages.ContainsKey($key)){$lookupAbsentChildren[$key].imageEvidence='blank-image-awaiting-control-creator-proof'}
   }}
   if($process){
    try{$actualHandle=Read-ControllerHandle $process}
    catch{
     $handleFailure=$_
     $failedAt=Get-ControllerPolicyMilliseconds
     Dispose-ControllerProcess $process
     $process=$null
     if(!(Test-ControlExitAtHandle $handleFailure.Exception $fact)){throw $handleFailure}
     $exitAtHandle=$true
     $pendingControlImages[$key]=@{unheld=$true;handleExit=$true;fact=$fact;started=$failedAt}
     $children[$key]=[ordered]@{pid=$lookupPid;birth=$created;path=$fact.ExecutablePath;commandLine=$fact.CommandLine;
      parentPid=$owned.Id;exitAtHandle=$true;heldHandleAcquired=$false;heldImageObserved=$false;creatorReceiptVerified=$false;
      imageEvidence='complete-cim-snapshot';closureEvidence='pending-exit-before-handle';consoleSupport=$false;peakWorkingSet=[long]$fact.WorkingSetSize}
    }
   }
   if($lookupAbsent -or $exitAtHandle){
    if($exitAtHandle){
     $seen=$children[$key]
     if($seen.path -ine $fact.ExecutablePath -or $seen.commandLine -cne $fact.CommandLine -or $seen.parentPid -ne $fact.ParentProcessId -or $fact.Name -cne 'ffmpeg.exe'){
      throw 'Exited control snapshot changed'
     }
    }elseif($lookupAbsentChildren[$key].path-ine$fact.ExecutablePath){throw 'Previously absent instance image snapshot changed'}
    $console=$fact.ExecutablePath-ieq$consoleImage
    if($console){$support++}else{$media++}
    if(-not$children.ContainsKey($key)){$children[$key]=[ordered]@{pid=$fact.ProcessId;birth=$created;path=$fact.ExecutablePath;heldImageObserved=$false;creatorReceiptVerified=$false;imageEvidence='complete-cim-snapshot';consoleSupport=$console;peakWorkingSet=[long]$fact.WorkingSetSize;closureEvidence='local-typed-lookup-absence'}}
    else{$children[$key].peakWorkingSet=[Math]::Max($children[$key].peakWorkingSet,[long]$fact.WorkingSetSize)}
    $supportTotal=@($children.Values|Where-Object {$_.consoleSupport}).Count;$mediaTotal=$children.Count-$supportTotal
    if($current-gt4-or$media-gt3-or$support-gt1-or$mediaTotal-gt([int]$p.MaximumChildren-[int]$p.RetainedChildren-1)-or$supportTotal-gt1-or$fact.WorkingSetSize-gt2147483648-or($console-and$fact.WorkingSetSize-gt67108864)){throw 'Lookup-absent snapshot still exceeds child resource/count caps'}
    continue
   }
   try{
    $startTime=Read-ControllerBirth $process
    if($null-eq$startTime-or$null-eq$actualHandle-or$actualHandle.IsInvalid-or$actualHandle.IsClosed){throw 'Child birth/handle unavailable; no PID-only ownership'}
    $actual=$startTime.ToUniversalTime()
    if([Math]::Abs(($actual-$created).Ticks)-gt10-or$actual-lt$birth){throw 'Child PID/birth mismatch; no stale process stopped'}
    $tracked[$key]=[pscustomobject]@{process=$process;pid=$fact.ProcessId;birth=$actual;handle=$actualHandle}
   }catch{$acquisitionError=$_;try{if($process){Dispose-ControllerProcess $process}}catch{throw [InvalidOperationException]::new(("Identity acquisition: "+$acquisitionError+"; handle disposal: "+$_),$acquisitionError.Exception)};throw $acquisitionError} # Handle/birth failures are never reclassified as lookup absence.
  }
  if(-not$tracked.ContainsKey($key)){throw 'Observed child identity/closure unsupported; no PID-only stop'}
  if(-not$tracked[$key].process.HasExited){
   $tracked[$key].process.Refresh()
   if($tracked[$key].process.HandleCount-gt4096){throw 'Direct child4096handle bound'}
  }
  $image=Resolve-ExitedChildImage $key $fact.ExecutablePath;$held=$false
  if($tracked.ContainsKey($key)-and-not$tracked[$key].process.HasExited){
   $moduleImage=(Read-ControllerImage $tracked[$key].process $tracked[$key].handle)
   if(-not[string]::IsNullOrEmpty($moduleImage)){$image=$moduleImage;$held=$true}
   elseif($tracked[$key].process.HasExited){$image=Resolve-ExitedChildImage $key $fact.ExecutablePath}
   elseif([string]::IsNullOrEmpty($image)){throw 'Live held executable image unavailable; no creator fallback'}
  }
  $console=($held-and$image-ieq$consoleImage)-or($children.ContainsKey($key)-and$children[$key].consoleSupport)
  if($console){$support++}else{$media++}
  if(-not$children.ContainsKey($key)){$children[$key]=[ordered]@{pid=$fact.ProcessId;birth=$created;path=$image;heldImageObserved=$held;creatorReceiptVerified=$false;imageEvidence=if($held){'held-kernel-executable-image'}else{'cim-image'};consoleSupport=$console;peakWorkingSet=[long]$fact.WorkingSetSize}}
  else{$children[$key].peakWorkingSet=[Math]::Max($children[$key].peakWorkingSet,[long]$fact.WorkingSetSize);if($held){$children[$key].heldImageObserved=$true;$children[$key].path=$image;$children[$key].imageEvidence='held-kernel-executable-image'}}
  $supportTotal=@($children.Values|Where-Object {$_.consoleSupport}).Count
  $mediaTotal=$children.Count-$supportTotal
  $pending=$false
  if([string]::IsNullOrEmpty($image)-and$tracked[$key].process.HasExited-and$fact.Name-ceq'ffmpeg.exe'){
   if(Get-CreatorCase){Add-PendingCreatorImage $key $fact $false;$pending=$true;$children[$key].imageEvidence='pending-retrospective-creator-proof'}
   else{
    if(@($p.Order|Where-Object {Test-Path -LiteralPath (Join-Path $p.Root "$_/capture-arguments.json")}).Count){throw 'Creator fallback is restricted to pre-capture engine controls'}
    if(-not$pendingControlImages.ContainsKey($key)){$pendingControlImages[$key]=Get-ControllerPolicyMilliseconds}
    $pending=$true;$children[$key].imageEvidence='pending-closed-control'
   }
  }
  if($current-gt4-or$media-gt3-or$support-gt1-or$mediaTotal-gt([int]$p.MaximumChildren-[int]$p.RetainedChildren-1)-or$supportTotal-gt1-or
   $fact.WorkingSetSize-gt2147483648-or($console-and$fact.WorkingSetSize-gt67108864)-or(-not$pending-and-not$console-and$image-ine$runtime[0]-and$image-ine$engine[0])){throw 'Owned child image/resource/count bound'}
 }
}

function Complete-Handoffs {
 if($retained){return}
 if($pendingControlImages.Count-or$pendingCreatorImages.Count){return}
 foreach($label in $p.Order){
  if($released.Contains($label)){continue}
  $case=Join-Path $p.Root $label;$marker=Join-Path $case foreground-handoff-ready.json
  if(-not(Test-Path -LiteralPath $marker)){continue}
  if((Test-Path -LiteralPath (Join-Path $case capture-arguments.json))-or(Test-Path -LiteralPath (Join-Path $case foreground-handoff-complete.json))-or(Test-Path -LiteralPath (Join-Path $case foreground-handoff-release.json))){throw 'Unexpected release/capture outside owner'}
  $ready=Read-Handoff $marker;if($null-eq$ready){continue};$r=$ready.release
  if($ready.captureStarted-ne$false-or$ready.label-cne$label-or$r.Label-cne$label-or$r.Pid-ne$ready.pid-or$r.Hwnd-ne$ready.hwnd-or$r.Pid-le0-or$r.Hwnd-eq0-or$r.Nonce-cnotmatch'^[0-9a-f]{32}$'){throw 'Exact disarmed fixture challenge required'}
  $matches=@($tracked.Values|Where-Object {$_.pid-eq$ready.pid-and-not$_.process.HasExited})
  if($matches.Count-eq0){continue};if($matches.Count-ne1-or(Read-ControllerImage $matches[0].process $matches[0].handle)-ine$runtime[0]){throw 'Fixture not uniquely current direct owned helper'}
  if($reported.Add($label)){[IO.File]::WriteAllText($a.watchStatusFile,([ordered]@{label=$label;pid=$r.Pid;hwnd=$r.Hwnd;captureStarted=$false;readyUtc=[DateTime]::UtcNow.ToString('o');instruction='CUA only; complete all UI calls before matching nonce acknowledgement'}|ConvertTo-Json -Compress))}
  $ackPath=Join-Path $case computer-use-ready.json
  if(-not(Test-Path -LiteralPath $ackPath)){continue}
  $ack=Read-Handoff $ackPath;if($null-eq$ack){continue}
  if($ack.Label-cne$label-or$ack.Pid-ne$r.Pid-or$ack.Hwnd-ne$r.Hwnd-or$ack.Nonce-cne$r.Nonce-or$ack.allUiCallsReturned-ne$true){throw 'Exact completed CUA acknowledgement required'}
  # No helper process or UI call for release: same held collector verifies native foreground.
  $temporary=Join-Path $case ('.release-'+[Guid]::NewGuid().ToString('N')+'.tmp')
  $writer=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
  try{$writer.Write([Text.Encoding]::UTF8.GetBytes(($r|ConvertTo-Json -Compress)));$writer.Flush($true)}finally{$writer.Dispose()}
  [IO.File]::Move($temporary,(Join-Path $case foreground-handoff-release.json))
  $handoffs.Add([ordered]@{label=$label;ack=$ack;releasedUtc=[DateTime]::UtcNow.ToString('o')});$null=$released.Add($label)
 }
}

function Bounds([switch]$Full,[switch]$Final){
 if($clock.Elapsed.TotalSeconds-ge$wholeLimitSeconds-or[DateTime]::UtcNow-ge$deadline-or[DateTime]::UtcNow-ge$purposeDeadline){throw 'Original authorized whole deadline including checks/cleanup'}
 foreach($log in @($stdout,$stderr)){if(Test-Path -LiteralPath $log){$s=[IO.FileStream]::new($log,'Open','Read',([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete));try{if($s.Length-gt1048576){throw 'Outer1MiB log bound'}}finally{$s.Dispose()}}}
 if(Test-Path -LiteralPath $private){$count=0;$bytes=0L;foreach($entry in Get-ChildItem -LiteralPath $private -Recurse -Force){if(++$count-gt128-or($entry.Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Private128entry/no reparse bound'};if(-not$entry.PSIsContainer){$bytes+=$entry.Length}};if($bytes-gt1000000){throw 'Private1MB bound'}}
 $admissions=Join-Path $p.Root child-admissions.jsonl
 if($Final-and-not(Test-Path -LiteralPath $admissions)-and$exitCode-eq0){throw 'Successful cohort lacks managed admission receipt'}
 if(Test-Path -LiteralPath $admissions){
  $s=[IO.FileStream]::new($admissions,'Open','Read',([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete))
  try{if($s.Length-gt65536){throw 'Managed admission64KiB bound'};$bytes=[byte[]]::new([int]$s.Length);$s.ReadExactly($bytes)}finally{$s.Dispose()}
  $text=[Text.Encoding]::UTF8.GetString($bytes);$lines=$text.Split("`n");$count=0
  if($Final-and-not$text.EndsWith("`n")){throw 'Final managed admission record incomplete'}
  for($i=0;$i-lt$lines.Length-1;$i++){if([string]::IsNullOrWhiteSpace($lines[$i])){continue};$row=$lines[$i]|ConvertFrom-Json;if($row.ordinal-ne([int]$p.RetainedChildren+(++$count))){throw 'Fresh managed admission ordinal mismatch'}}
  $script:managedRows=$count;if($count-gt([int]$p.MaximumChildren-[int]$p.RetainedChildren)-or($Final-and$exitCode-eq0-and$count-lt1)){throw 'Managed1through61 cumulative roles required on success'}
  if([int]$p.RetainedChildren+[int]$a.priorFailedManagedRoles+$count-gt$a.cumulativeManagedRoleCeiling){throw 'Global retained/prior/current managed role ceiling'}
 }
 if($Full){
  $bytes=0L;foreach($file in Get-ChildItem -LiteralPath $phase -Recurse -File){if($file.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Unexpected preparation reparse'};$bytes+=$file.Length}
  $script:phaseBytes=$bytes
  if($bytes-gt$phaseByteLimit-or([IO.DriveInfo]::new('D:\')).AvailableFreeSpace-lt60000000000){throw 'Whole preparation byte/free60GB bound'}
 }
}


function Add-ControllerRawObservation($Fact,[DateTime]$Created){
 if($rawChildren.Count-ge$rawChildRowLimit){throw 'Finite raw child observation row bound'}
 $row=[ordered]@{pid=$Fact.ProcessId;parentPid=$Fact.ParentProcessId;birth=$Created;path=$Fact.ExecutablePath;name=$Fact.Name;arguments=$Fact.CommandLine;ram=$Fact.WorkingSetSize;observedMilliseconds=$clock.Elapsed.TotalMilliseconds;bornAfterHeldParent=($Created-ge$birth)}
 $serialized=[Text.Encoding]::UTF8.GetByteCount(($row|ConvertTo-Json -Compress)+"`n")
 if($serialized-gt$rawChildRowBytes){throw 'Finite raw child observation row byte bound'}
 $rawChildren.Add($row)
}

function Write-ControllerRawReport($Stream){
 foreach($row in $rawChildren){
  if($clock.Elapsed.TotalSeconds-ge($wholeLimitSeconds-5)){throw 'Raw report terminal reserve'}
  $line=[Text.Encoding]::UTF8.GetBytes(($row|ConvertTo-Json -Compress)+"`n")
  if($line.Length-gt$rawChildRowBytes-or$rawBytes+$line.Length-gt$rawChildByteLimit){throw 'Finite raw report byte bound'}
  Write-ControllerRaw $Stream $line;$script:rawBytes+=$line.Length;$script:rawWritten++
 }
 $Stream.Flush();$script:rawComplete=$true
}
