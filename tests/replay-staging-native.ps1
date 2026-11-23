param(
 [Parameter(Mandatory)][string]$PlanFile,
 [string]$ExpectedPlanSha256,
 [switch]$Plan,[switch]$Run
)
$ErrorActionPreference='Stop'
if(([int]$Plan.IsPresent+[int]$Run.IsPresent) -ne 1){throw 'Choose Plan or Run.'}
function Read-Bounded([string]$Path,[int]$Maximum){
 $reader=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{if($reader.Length -le 0 -or $reader.Length -gt $Maximum){throw 'Input size bound'};$bytes=[byte[]]::new([int]$reader.Length);$reader.ReadExactly($bytes);return ,$bytes}finally{$reader.Dispose()}
}
$path=(Resolve-Path -LiteralPath $PlanFile).Path
$raw=Read-Bounded $path 65536
$sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))
$p=[Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json
if($p.kind -cne 'issue83-native-staging-fallback-v1' -or $p.maximumChildren -ne 9 -or $p.deadlineSeconds -ne 120 -or $p.captureSeconds -ne 0 -or $p.fixtureLifetimeSeconds -ne 0){throw 'Wrong drain admission.'}
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))+[IO.Path]::DirectorySeparatorChar
if(-not ([IO.Path]::GetFullPath($p.root)).StartsWith($artifacts,[StringComparison]::OrdinalIgnoreCase)){throw 'Isolated artifact root required.'}
if($Plan){$p|ConvertTo-Json -Depth 12;return}
if($sha -cne $ExpectedPlanSha256 -or [DateTime]::UtcNow.AddSeconds(120) -ge ([DateTime]$p.createdUtc).AddHours(4)){throw 'Current full120second reviewed native control required.'}
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
$clock=[Diagnostics.Stopwatch]::StartNew();$owned=$null;$ownedStart=$null;$failure=$null;$exitCode=$null;$peakRam=0L;$peakHandles=0
$children=@{};$tracked=@{};$rawChildren=[Collections.Generic.List[object]]::new();$childClosure=@()
$lastQuery=-1.0;$queryMaximumMs=0.0;$outFile=$null;$errFile=$null;$outCopy=$null;$errCopy=$null
function Logs {
 foreach($log in @($stdout,$stderr)){
  if(Test-Path -LiteralPath $log){
   $reader=[IO.FileStream]::new($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
   try{if($reader.Length -gt 2097152){throw 'Log cap.'}}finally{$reader.Dispose()}
  }
 }
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
   $process=$null
   try{
    $process=[Diagnostics.Process]::GetProcessById($child.ProcessId);$actualStart=$process.StartTime.ToUniversalTime()
    if($actualStart -lt $ownedStart -or [Math]::Abs(($actualStart-$created).Ticks) -gt 10){$process.Dispose();throw 'Child PID/creation mismatch.'}
    $null=$process.SafeHandle;$tracked[$key]=[pscustomobject]@{process=$process;pid=$process.Id;startedUtc=$actualStart}
   }catch [ArgumentException]{if($process){$process.Dispose()}} # Already exited; helper retains codec closure receipts.
  }
  # Retain the exact child object BEFORE evaluating rejection policies.
  $image=$child.ExecutablePath;$heldImage=$false;$heldExited=$false;$exitImageProof=$false
  if($tracked.ContainsKey($key)){
   $held=$tracked[$key].process;$heldExited=$held.HasExited
   if(-not $heldExited){
    try{$image=$held.MainModule.FileName;$heldImage=$true}
    catch{if(-not $held.HasExited){throw};$heldExited=$true;$image=$null}
    if([string]::IsNullOrEmpty($image) -and $held.HasExited){$heldExited=$true;$heldImage=$false}
   }
   # CIM can lose ExecutablePath while an exact retained child is exiting.
   # Reuse only this birth's earlier held-handle image, after confirmed exit.
   if($heldExited -and $children.ContainsKey($key) -and $children[$key].heldImageObserved){
    if([string]::IsNullOrEmpty($image)){$image=$children[$key].path}
    if(-not [string]::Equals($image,$children[$key].path,[StringComparison]::OrdinalIgnoreCase)){throw 'Current child image/resource/count cap: contradictory exited image.'}
    $exitImageProof=$true
   }
  }
  $console=($heldImage -or $exitImageProof) -and [string]::Equals($image,$consoleImage,[StringComparison]::OrdinalIgnoreCase)
  if($console){$currentConsole++}else{$currentMedia++}
  if(-not $children.ContainsKey($key)){$children[$key]=[ordered]@{pid=$child.ProcessId;startedUtc=$created;path=$image;cimPath=$child.ExecutablePath;heldImageObserved=$heldImage;consoleSupport=$console;peakWorkingSet=[long]$child.WorkingSetSize}}
  else{$children[$key].peakWorkingSet=[Math]::Max($children[$key].peakWorkingSet,[long]$child.WorkingSetSize)}
  if($exitImageProof){$children[$key].exitedHeldImageChecks=1+[int]$children[$key].exitedHeldImageChecks}
  $consoleAdmissions=@($children.Values|Where-Object {$_.consoleSupport}).Count
  $mediaAdmissions=@($children.Values|Where-Object {-not $_.consoleSupport}).Count
  if($currentCount -gt 4 -or $currentMedia -gt 3 -or $currentConsole -gt 1 -or $tracked.Count -gt 9 -or $mediaAdmissions -gt 8 -or $consoleAdmissions -gt 1 -or
   $child.WorkingSetSize -gt 2147483648 -or ($console -and $child.WorkingSetSize -gt 67108864) -or
   (-not $console -and $allowedImages -notcontains $image)){throw 'Current child image/resource/count cap.'}
 }
}
try{
 $start=[Diagnostics.ProcessStartInfo]::new($runtime[0]);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($arg in @($helper[0],'--replay-staging-native-run',$path,$sha)){$start.ArgumentList.Add($arg)}
 $owned=[Diagnostics.Process]::Start($start);$ownedStart=$owned.StartTime.ToUniversalTime()
 $outFile=[IO.FileStream]::new($stdout,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete),16384,[IO.FileOptions]::Asynchronous)
 $errFile=[IO.FileStream]::new($stderr,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete),16384,[IO.FileOptions]::Asynchronous)
 $outCopy=$owned.StandardOutput.BaseStream.CopyToAsync($outFile);$errCopy=$owned.StandardError.BaseStream.CopyToAsync($errFile)
 while(-not $owned.WaitForExit(100)){
  $owned.Refresh();$peakRam=[Math]::Max($peakRam,$owned.WorkingSet64);$peakHandles=[Math]::Max($peakHandles,$owned.HandleCount)
  if($clock.Elapsed.TotalSeconds -ge 115 -or $peakRam -gt 805306368 -or $peakHandles -gt 32768){throw 'Outer resource/deadline bound, five seconds cleanup reserved.'}
  Logs
  if($clock.Elapsed.TotalSeconds-$lastQuery -ge .5){
   $lastQuery=$clock.Elapsed.TotalSeconds;Observe-Children
  }
 }
 $owned.WaitForExit();$exitCode=$owned.ExitCode;if($exitCode -ne 0){throw "Pilot failed:$exitCode; retain attempt, no retry."}
}catch{$failure=$_.ToString()}
finally{
 try{Observe-Children}catch{$failure="$failure Final child observation:$_"}
 $cleanup=[Diagnostics.Stopwatch]::StartNew()
 function Remaining{return [Math]::Max(0,5000-[int]$cleanup.ElapsedMilliseconds)}
 try{if($owned -and -not $owned.HasExited){$owned.Kill();if(-not $owned.WaitForExit((Remaining))){$failure="$failure Owned helper absence unconfirmed."}}}catch{$failure="$failure Cleanup:$_"}
 foreach($entry in $tracked.Values){
  try{
   $forced=$false
   if(-not $entry.process.HasExited){$forced=$true;$failure="$failure Unexpected live child required forced cleanup.";$entry.process.Kill()}
   if(-not $entry.process.WaitForExit((Remaining))){throw 'Observed child absence unconfirmed.'}
   $childClosure+=[pscustomobject]@{pid=$entry.pid;startedUtc=$entry.startedUtc;absent=$entry.process.HasExited;forced=$forced;exitCode=$entry.process.ExitCode}
  }
  catch{$failure="$failure Child cleanup:$_"}finally{$entry.process.Dispose()}
 }
 foreach($copy in @($outCopy,$errCopy)){if($copy){try{if(-not $copy.Wait((Remaining))){throw 'Log drain unconfirmed.'};$null=$copy.GetAwaiter().GetResult()}catch{$failure="$failure Log drain:$_"}}}
 if($outFile -and $outCopy.IsCompleted){$outFile.Dispose()};if($errFile -and $errCopy.IsCompleted){$errFile.Dispose()}
 try{Logs}catch{$failure="$failure Final/post-kill log check:$_"}
 [pscustomobject]@{planSha256=$sha;pid=$owned.Id;startedUtc=$ownedStart;absent=(-not $owned -or $owned.HasExited);exitCode=$exitCode;seconds=$clock.Elapsed.TotalSeconds;peakRam=$peakRam;peakHandles=$peakHandles;rawChildObservations=$rawChildren;children=@($children.Values);observedChildClosure=$childClosure;queryMaximumMs=$queryMaximumMs;error=$failure;scope='Generated-media native staging controls; exact current child handles retained before policy/joined, stale-birth rows never opened/stopped; outer observer overhead included, no percentile/physical feedback/GPU qualification claim'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $receipt
 if($owned){$owned.Dispose()}
}
if($failure){throw $failure}
