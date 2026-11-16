param(
 [Parameter(Mandatory)][string]$PlanFile,
 [string]$ExpectedPlanSha256,
 [switch]$Plan,[switch]$Run,[switch]$Release
)
$ErrorActionPreference='Stop'
if(([int]$Plan.IsPresent+[int]$Run.IsPresent+[int]$Release.IsPresent) -ne 1){throw 'Choose Plan, Run or Release.'}
function Read-Bounded([string]$Path,[int]$Maximum){
 $reader=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{if($reader.Length -le 0 -or $reader.Length -gt $Maximum){throw 'Input size bound'};$bytes=[byte[]]::new([int]$reader.Length);$reader.ReadExactly($bytes);return ,$bytes}finally{$reader.Dispose()}
}
$path=(Resolve-Path -LiteralPath $PlanFile).Path
$raw=Read-Bounded $path 65536
$sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))
$p=[Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json
if($p.kind -cne 'issue83-acceptance-pair-v2' -or $p.maximumChildren -ne 12 -or $p.deadlineSeconds -ne 900 -or $p.captureSeconds -ne 580 -or $p.fixtureLifetimeSeconds -ne 775){throw 'Wrong acceptance-pair admission.'}
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))+[IO.Path]::DirectorySeparatorChar
if(-not ([IO.Path]::GetFullPath($p.root)).StartsWith($artifacts,[StringComparison]::OrdinalIgnoreCase)){throw 'Isolated artifact root required.'}
if($Plan){$p|ConvertTo-Json -Depth 12;return}
if($sha -cne $ExpectedPlanSha256 -or (-not $Release -and [DateTime]::UtcNow.AddSeconds(900) -ge ([DateTime]$p.createdUtc).AddHours(4))){throw 'Exact reviewed pair and full900second initial horizon required.'}
foreach($f in $p.files.PSObject.Properties){
 $sharing=if($p.sharedFiles -contains $f.Name){[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete}else{[IO.FileShare]::Read}
 $reader=[IO.FileStream]::new($f.Name,[IO.FileMode]::Open,[IO.FileAccess]::Read,$sharing)
 try{$observed=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($reader))}finally{$reader.Dispose()}
 if($observed -cne $f.Value){throw "Binding changed: $($f.Name)"}
}
if($p.files.PSObject.Properties.Name -notcontains $PSCommandPath){throw 'Controller must be bound.'}
if($p.files.PSObject.Properties.Name -notcontains $p.runtimeManifest){throw 'Runtime closure manifest must be bound.'}
$runtimeBindings=[Text.Encoding]::UTF8.GetString((Read-Bounded $p.runtimeManifest 262144))|ConvertFrom-Json
if(@($runtimeBindings.PSObject.Properties).Count -lt 1 -or @($runtimeBindings.PSObject.Properties).Count -gt 1024){throw 'Runtime closure count bound.'}
foreach($f in $runtimeBindings.PSObject.Properties){if((Get-FileHash -LiteralPath $f.Name).Hash -cne $f.Value){throw "Runtime binding changed: $($f.Name)"}}
if($Release){
 $batchAdmission=Get-Content -Raw -LiteralPath (Join-Path $p.batch.root 'batch-admitted.json')|ConvertFrom-Json
 if((Get-FileHash -LiteralPath $p.batch.masterPlan).Hash -cne $batchAdmission.planSha256 -or [DateTime]::UtcNow -ge [DateTime]$batchAdmission.deadlineUtc){throw 'Original fixed batch deadline required for pending release.'}
 $master=Get-Content -Raw -LiteralPath $p.batch.masterPlan|ConvertFrom-Json
 if([DateTime]$batchAdmission.deadlineUtc -gt ([DateTime]$master.createdUtc).AddHours(4) -or
  @($master.pairs|Where-Object {$_.plan -ieq $path -and $_.sha256 -ceq $sha -and $_.root -ieq $p.root}).Count -ne 1){throw 'Pair must belong to the exact admitted master and original horizon.'}
 $owner=[Diagnostics.Process]::GetProcessById($batchAdmission.controllerPid)
 try{$null=$owner.SafeHandle;if($owner.HasExited -or $owner.StartTime.ToUniversalTime() -ne [DateTime]$batchAdmission.controllerBirth){throw 'Exact running batch-controller birth required for direct pair release.'}}finally{$owner.Dispose()}
 if(Test-Path -LiteralPath (Join-Path $p.root foreground-handoff-complete.json)){throw 'Already released.'}
 $ready=[Text.Encoding]::UTF8.GetString((Read-Bounded (Join-Path $p.root foreground-handoff-ready.json) 4096))|ConvertFrom-Json
 $r=$ready.release
 if($ready.captureStarted -ne $false -or $ready.label -cne 'H02' -or $r.Label -cne 'H02' -or $r.Pid -ne $ready.pid -or $r.Hwnd -ne $ready.hwnd -or $r.Pid -le 0 -or $r.Hwnd -eq 0 -or $r.Nonce -cnotmatch '^[0-9a-f]{32}$'){throw 'Invalid owned challenge.'}
 # Run ONLY after every UI permission/activation call has returned.
 $temporary=Join-Path $p.root ('.release-'+[Guid]::NewGuid().ToString('N')+'.tmp');$created=$false
 try{
  $writer=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$created=$true
  try{$writer.Write([Text.Encoding]::UTF8.GetBytes(($r|ConvertTo-Json -Compress)));$writer.Flush($true)}finally{$writer.Dispose()}
  [IO.File]::Move($temporary,(Join-Path $p.root foreground-handoff-release.json))
 }finally{if($created -and (Test-Path -LiteralPath $temporary)){[IO.File]::Delete($temporary)}}
 return
}
if(Test-Path -LiteralPath $p.root){throw 'Preserve prior attempt.'}
$runtime=@($p.files.PSObject.Properties.Name|Where-Object {[IO.Path]::GetFileName($_) -ceq 'dotnet.exe'})
$helper=@($p.files.PSObject.Properties.Name|Where-Object {[IO.Path]::GetFileName($_) -ceq 'F10Recorder.WorkerChecks.dll'})
if($runtime.Count -ne 1 -or $helper.Count -ne 1){throw 'Exact host/helper required.'}
$allowedImages=@($runtime[0])+@($p.files.PSObject.Properties.Name|Where-Object {[IO.Path]::GetFileName($_) -ceq 'ffmpeg.exe'})
if($allowedImages.Count -ne 2){throw 'Exact capture/export engine required.'}
$consoleImage=[IO.Path]::Combine([Environment]::SystemDirectory,'conhost.exe')
if($p.consoleSupport.path -ine $consoleImage -or $p.consoleSupport.maximumDirectChildren -ne 1 -or $p.consoleSupport.maximumWorkingSetBytes -ne 67108864 -or
 $p.files.PSObject.Properties.Name -notcontains $consoleImage){throw 'Exact bounded system console support must be bound.'}
$directory=Split-Path -Parent $path
$stdout=Join-Path $directory stdout.txt;$stderr=Join-Path $directory stderr.txt;$receipt=Join-Path $directory controller.json
foreach($f in @($stdout,$stderr,$receipt)){if(Test-Path -LiteralPath $f){throw 'Prior controller output exists.'}}
. (Join-Path $PSScriptRoot 'replay-resource-observer.ps1')
. (Join-Path $PSScriptRoot 'replay-acceptance-budget.ps1')
$batchAdmitted=Get-Content -Raw -LiteralPath (Join-Path $p.batch.root 'batch-admitted.json')|ConvertFrom-Json
$master=Get-Content -Raw -LiteralPath $p.batch.masterPlan|ConvertFrom-Json
if((Get-FileHash -LiteralPath $p.batch.masterPlan).Hash -cne $batchAdmitted.planSha256 -or
 @($master.pairs|Where-Object {$_.plan -ieq $path -and $_.sha256 -ceq $sha}).Count -ne 1){throw 'Owning frozen batch admission required.'}
$resourceOwned=[Collections.Concurrent.ConcurrentDictionary[string,object]]::new();$resourceObserver=$null
$controllerProcess=[Diagnostics.Process]::GetCurrentProcess();$null=$controllerProcess.SafeHandle;$controllerBirth=$controllerProcess.StartTime.ToUniversalTime()
$controllerCpuStart=$controllerProcess.TotalProcessorTime.TotalMilliseconds
$controllerIoStart=[F10ReplayAcceptanceNative]::ReadIo($controllerProcess)
$resourceOwned.TryAdd("$($controllerProcess.Id)/$($controllerBirth.Ticks)",[pscustomobject]@{process=$controllerProcess;pid=$controllerProcess.Id;startedUtc=$controllerBirth})|Out-Null
$clock=[Diagnostics.Stopwatch]::StartNew();$owned=$null;$ownedStart=$null;$failure=$null;$exitCode=$null;$peakRam=0L;$peakHandles=0;$helperForced=$false
$children=@{};$tracked=@{};$rawChildren=[Collections.Generic.List[object]]::new();$childClosure=@()
$lastQuery=-1.0;$queryMaximumMs=0.0;$outFile=$null;$errFile=$null;$outCopy=$null;$errCopy=$null;$lastBatchScan=-1.0;$maximumBatchBytes=0L;$nativePeaks=@{}
function Get-ReplayFinalIo($Process){return [F10ReplayAcceptanceNative]::ReadIo($Process)}
function Get-ReplayHeldImage($Process){return [F10ReplayAcceptanceNative]::ReadImage($Process)}
function Get-ReplayUnboundChild([int]$Id){return [Diagnostics.Process]::GetProcessById($Id)}
function Open-ReplayObservedChild([int]$Id,[DateTime]$Created,[DateTime]$OwnerStart){
 $candidate=$null
 try{
  $candidate=Get-ReplayUnboundChild $Id
  $handle=$candidate.SafeHandle # Pin BEFORE StartTime can cache a temporary PID lookup.
  if($null -eq $handle -or $handle.IsClosed -or $handle.IsInvalid){throw 'Unable to pin child process handle.'}
  $birth=$candidate.StartTime.ToUniversalTime()
  if($birth -lt $OwnerStart -or [Math]::Abs(($birth-$Created).Ticks) -gt 10){throw 'Child PID/creation mismatch.'}
  $entry=[pscustomobject]@{process=$candidate;pid=$candidate.Id;startedUtc=$birth};$candidate=$null;return $entry
 }catch [ArgumentException]{return $null} # Already gone; never adopt a replacement without its matching held birth.
 finally{if($candidate){$candidate.Dispose()}}
}
function Logs {
 foreach($log in @($stdout,$stderr)){
  if(Test-Path -LiteralPath $log){
   $reader=[IO.FileStream]::new($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
   try{if($reader.Length -gt 2097152){throw 'Log cap.'}}finally{$reader.Dispose()}
  }
 }
 foreach($copy in @($outCopy,$errCopy)){if($copy){if($copy.Overflow){throw 'Helper log overflow; fixed2MiB prefix retained.'};if($copy.Completion.IsFaulted){$null=$copy.Completion.GetAwaiter().GetResult()}}}
}
function Observe-Children {
 # A PID is not ownership after the retained parent has exited. Check on both
 # sides of CIM before adopting any child from its numeric parent snapshot.
 if(-not $owned -or $owned.HasExited){return}
 $query=[Diagnostics.Stopwatch]::StartNew()
 $active=@(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($owned.Id)" -Property ProcessId,CreationDate,ExecutablePath,WorkingSetSize)
 $script:queryMaximumMs=[Math]::Max($script:queryMaximumMs,$query.Elapsed.TotalMilliseconds)
 if($owned.HasExited){return}
 $currentCount=0;$currentMedia=0;$currentConsole=0
 foreach($child in $active){
  $created=([DateTime]$child.CreationDate).ToUniversalTime();$current=$created -ge $ownedStart
  if($rawChildren.Count -ge 8192){throw 'Raw child observation cap.'}
  $rawChildren.Add([pscustomobject]@{pid=$child.ProcessId;createdUtc=$created;path=$child.ExecutablePath;ram=$child.WorkingSetSize;bornAfterParent=$current;classification=if($current){'candidate-owned'}else{'stale-parent-id-never-open-or-stop'}})
  if(-not $current){continue};$currentCount++
  $key="$($child.ProcessId)/$($created.Ticks)"
  if(-not $tracked.ContainsKey($key)){
   $entry=Open-ReplayObservedChild ([int]$child.ProcessId) $created $ownedStart
   if($null -ne $entry){$tracked[$key]=$entry}
  }
  # Retain the exact child object BEFORE evaluating rejection policies.
  $image=$child.ExecutablePath;$heldImage=$false;$heldExited=$false;$exitImageProof=$false
  if($tracked.ContainsKey($key)){
   $held=$tracked[$key].process;$heldExited=$held.HasExited
   if(-not $heldExited){
    try{$image=Get-ReplayHeldImage $held;$heldImage=-not [string]::IsNullOrWhiteSpace($image)}
    catch{if(-not $held.HasExited){throw};$heldExited=$true;$image=$null}
    if([string]::IsNullOrEmpty($image) -and $held.HasExited){$heldExited=$true;$heldImage=$false}
   }
   # CIM can lose ExecutablePath while an exact retained child is exiting.
   # Reuse only this birth's earlier held-handle image, after confirmed exit.
   if($heldExited -and $children.ContainsKey($key) -and $children[$key].heldImageObserved -and -not [string]::IsNullOrWhiteSpace($children[$key].path)){
    if([string]::IsNullOrEmpty($image)){$image=$children[$key].path}
    if(-not [string]::Equals($image,$children[$key].path,[StringComparison]::OrdinalIgnoreCase)){throw 'Current child image/resource/count cap: contradictory exited image.'}
    $exitImageProof=$true
   }
  }
  $console=($heldImage -or $exitImageProof) -and [string]::Equals($image,$consoleImage,[StringComparison]::OrdinalIgnoreCase)
  if($console){$currentConsole++}else{$currentMedia++}
  if(-not $children.ContainsKey($key)){$children[$key]=[ordered]@{pid=$child.ProcessId;startedUtc=$created;path=$image;cimPath=$child.ExecutablePath;heldImageObserved=$heldImage;consoleSupport=$console;peakWorkingSet=[long]$child.WorkingSetSize}}
  else{
   $children[$key].peakWorkingSet=[Math]::Max($children[$key].peakWorkingSet,[long]$child.WorkingSetSize)
   if($heldImage){
    if($children[$key].heldImageObserved -and -not [string]::IsNullOrWhiteSpace($children[$key].path) -and
     -not [string]::Equals($children[$key].path,$image,[StringComparison]::OrdinalIgnoreCase)){throw 'Current child image/resource/count cap: contradictory held image.'}
    $children[$key].path=$image;$children[$key].heldImageObserved=$true
   }
  }
  if($exitImageProof){$children[$key].exitedHeldImageChecks=1+[int]$children[$key].exitedHeldImageChecks}
  $consoleAdmissions=@($children.Values|Where-Object {$_.consoleSupport}).Count
  $mediaAdmissions=@($children.Values|Where-Object {-not $_.consoleSupport}).Count
  if($currentCount -gt 5 -or $currentMedia -gt 4 -or $currentConsole -gt 1 -or $tracked.Count -gt 12 -or $mediaAdmissions -gt 11 -or $consoleAdmissions -gt 1 -or
   $child.WorkingSetSize -gt 2147483648 -or ($console -and $child.WorkingSetSize -gt 67108864) -or
   (-not $console -and $allowedImages -notcontains $image)){throw 'Current child image/resource/count cap.'}
 }
}
try{
 $start=[Diagnostics.ProcessStartInfo]::new($runtime[0]);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($arg in @($helper[0],'--replay-worker-latency-pilot-run',$path,$sha)){$start.ArgumentList.Add($arg)}
 $owned=[Diagnostics.Process]::Start($start);$ownedStart=$owned.StartTime.ToUniversalTime()
 $null=$owned.SafeHandle;$resourceOwned.TryAdd("$($owned.Id)/$($ownedStart.Ticks)",[pscustomobject]@{process=$owned;pid=$owned.Id;startedUtc=$ownedStart})|Out-Null
 $resourceObserver=Start-ReplayResourceObserver $resourceOwned
 $outFile=[IO.FileStream]::new($stdout,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete),16384,[IO.FileOptions]::Asynchronous)
 $errFile=[IO.FileStream]::new($stderr,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete),16384,[IO.FileOptions]::Asynchronous)
 $outCopy=[F10ReplayAcceptanceNative+PrefixCopy]::new($owned.StandardOutput.BaseStream,$outFile,2097152)
 $errCopy=[F10ReplayAcceptanceNative+PrefixCopy]::new($owned.StandardError.BaseStream,$errFile,2097152)
 while(-not $owned.WaitForExit(100)){
  $owned.Refresh();$peakRam=[Math]::Max($peakRam,$owned.WorkingSet64);$peakHandles=[Math]::Max($peakHandles,$owned.HandleCount)
  if($clock.Elapsed.TotalSeconds -ge 893 -or $peakRam -gt 805306368 -or $peakHandles -gt 32768){throw 'Outer resource/deadline bound, 1.5seconds sampler plus five seconds cleanup reserved.'}
  Logs
  Assert-ReplayResourceObserver $resourceObserver $clock.Elapsed.TotalSeconds
  if($clock.Elapsed.TotalSeconds-$lastBatchScan -ge 1){$lastBatchScan=$clock.Elapsed.TotalSeconds;$whole=Assert-ReplayAcceptanceStorage $p.batch;$maximumBatchBytes=[Math]::Max($maximumBatchBytes,$whole.bytes)}
  if($clock.Elapsed.TotalSeconds-$lastQuery -ge .5){
   $lastQuery=$clock.Elapsed.TotalSeconds;Observe-Children
   foreach($entry in $tracked.GetEnumerator()){
    $resourceOwned.TryAdd($entry.Key,$entry.Value)|Out-Null
    try{if(-not $entry.Value.process.HasExited){$memory=[F10ReplayAcceptanceNative]::ReadMemory($entry.Value.process);$peakKey="$($entry.Value.pid)/$($entry.Value.startedUtc.Ticks)";$nativePeaks[$peakKey]=[Math]::Max([ulong]$nativePeaks[$peakKey],$memory.PeakWorkingSet.ToUInt64())}}catch{if(-not $entry.Value.process.HasExited){throw}}
   }
  }
 }
 $owned.WaitForExit();$exitCode=$owned.ExitCode;if($exitCode -ne 0){throw "Pilot failed:$exitCode; retain attempt, no retry."}
}catch{$failure=$_.ToString()}
finally{
 try{Observe-Children}catch{$failure="$failure Final child observation:$_"}
 # Stop the shared-handle reader BEFORE any Process is disposed. This cost is
 # inside the existing overall deadline; it adds no child process.
 if($resourceObserver){try{Stop-ReplayResourceObserver $resourceObserver (Join-Path $directory 'resources.json')}catch{$failure="$failure Resource observer stop:$_"}}
 $cleanup=[Diagnostics.Stopwatch]::StartNew()
 function Remaining{return [Math]::Max(0,5000-[int]$cleanup.ElapsedMilliseconds)}
 try{if($owned -and -not $owned.HasExited){$helperForced=$true;$owned.Kill();if(-not $owned.WaitForExit((Remaining))){$failure="$failure Owned helper absence unconfirmed."}}}catch{$failure="$failure Cleanup:$_"}
 foreach($entry in $tracked.Values){
  try{
   $forced=$false
   if(-not $entry.process.HasExited){$forced=$true;$failure="$failure Unexpected live child required forced cleanup.";$entry.process.Kill()}
   if(-not $entry.process.WaitForExit((Remaining))){throw 'Observed child absence unconfirmed.'}
   $io=$null;$ioError=$null;try{$io=Get-ReplayFinalIo $entry.process}catch{$ioError=$_.ToString();$failure="$failure Child final I/O:$ioError"}
   $childClosure+=[pscustomobject]@{pid=$entry.pid;startedUtc=$entry.startedUtc;absent=$entry.process.HasExited;forced=$forced;exitCode=$entry.process.ExitCode;cpuMilliseconds=$entry.process.TotalProcessorTime.TotalMilliseconds;
    readBytes=$io.ReadBytes;writeBytes=$io.WriteBytes;otherIoBytes=$io.OtherBytes;ioError=$ioError;nativePeakWorkingSetBytes=if($nativePeaks){$nativePeaks["$($entry.pid)/$($entry.startedUtc.Ticks)"]}else{$null}}
  }
  catch{$failure="$failure Child cleanup:$_"}finally{if(-not $resourceObserver -or $resourceObserver.state['joined']){$entry.process.Dispose()}}
 }
 foreach($copy in @($outCopy,$errCopy)){if($copy){try{if(-not $copy.Completion.Wait((Remaining))){$copy.Cancel();throw 'Log drain unconfirmed; reader cancellation requested.'};$null=$copy.Completion.GetAwaiter().GetResult();if($copy.Overflow){throw 'Log exceeded fixed retained2MiB prefix.'}}catch{$failure="$failure Log drain:$_"}}}
 if($outFile -and ($null -eq $outCopy -or $outCopy.Completion.IsCompleted)){$outFile.Dispose()};if($errFile -and ($null -eq $errCopy -or $errCopy.Completion.IsCompleted)){$errFile.Dispose()}
 foreach($copy in @($outCopy,$errCopy)){if($copy -and $copy.Completion.IsCompleted){$copy.Dispose()}}
 try{Logs}catch{$failure="$failure Final/post-kill log check:$_"}
 $helperIo=$null;$controllerIo=$null
 try{if($owned){$helperIo=[F10ReplayAcceptanceNative]::ReadIo($owned)}}catch{$failure="$failure Helper final I/O:$_"}
 try{$controllerIo=[F10ReplayAcceptanceNative]::ReadIo($controllerProcess)}catch{$failure="$failure Controller final I/O:$_"}
 [pscustomobject]@{planSha256=$sha;pid=$owned.Id;startedUtc=$ownedStart;absent=(-not $owned -or $owned.HasExited);exitCode=$exitCode;helperForced=$helperForced;observedHelperExitCode=if($owned -and $owned.HasExited){$owned.ExitCode}else{$null};seconds=$clock.Elapsed.TotalSeconds;peakRam=$peakRam;peakHandles=$peakHandles;helperCpuMilliseconds=if($owned -and $owned.HasExited){$owned.TotalProcessorTime.TotalMilliseconds}else{$null};helperIo=$helperIo;controllerIoStart=$controllerIoStart;controllerIoEnd=$controllerIo;rawChildObservations=$rawChildren;children=@($children.Values);observedChildClosure=$childClosure;queryMaximumMs=$queryMaximumMs;controllerPid=$controllerProcess.Id;controllerBirth=$controllerBirth;controllerCpuMilliseconds=($controllerProcess.TotalProcessorTime.TotalMilliseconds-$controllerCpuStart);maximumBatchBytes=$maximumBatchBytes;error=$failure;scope='One fresh-worker first/repeat acceptance pair, actual hidden UI. Exact owning process handles retained; counter/controller overhead included. GPU short-lived children without two valid held-birth samples are N/A; physical disk counters include other applications. No standalone percentile/physical-feedback/whole-resource qualification.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $receipt
 if(-not $resourceObserver -or $resourceObserver.state['joined']){if($owned){$owned.Dispose()};$controllerProcess.Dispose()}
}
if($failure){throw $failure}
