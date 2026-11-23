# Dot-source from a frozen owning controller. This adds one runspace/thread in
# that controller, no process, acquisition, ETW session or settings change.
if(-not ('F10ReplayAcceptanceNative' -as [type])){Add-Type -LiteralPath (Join-Path $PSScriptRoot 'ReplayAcceptanceNative.cs')}
$observerShellRoot=[IO.Path]::GetDirectoryName([Diagnostics.Process]::GetCurrentProcess().MainModule.FileName)
$script:replayDiagnosticsManifest=Join-Path $observerShellRoot 'Modules\Microsoft.PowerShell.Diagnostics\Microsoft.PowerShell.Diagnostics.psd1'
$threadManifest=Join-Path $observerShellRoot 'Modules\Microsoft.PowerShell.ThreadJob\Microsoft.PowerShell.ThreadJob.psd1'
Import-Module -Name $threadManifest -ErrorAction Stop
function Start-ReplayResourceObserver {
 param([Collections.Concurrent.ConcurrentDictionary[string,object]]$Owned)
 $queue=[Collections.Concurrent.ConcurrentQueue[object]]::new()
 $state=[Collections.Concurrent.ConcurrentDictionary[string,object]]::new()
 $paths=@('\GPU Engine(*)\Utilization Percentage','\GPU Process Memory(*)\Dedicated Usage','\GPU Process Memory(*)\Shared Usage',
  '\Disco físico(*)\Bytes lidos de disco/seg','\Disco físico(*)\Bytes escritos em disco/seg')
 $job=Microsoft.PowerShell.ThreadJob\Start-ThreadJob -ArgumentList $Owned,$queue,$state,$paths,$script:replayDiagnosticsManifest -ScriptBlock {
  param($handles,$rows,$status,$counterPaths,$diagnosticsManifest)
  $ErrorActionPreference='Stop';$previous=@{};$previousCounterUtc=$null;$lastCounterUtc=$null;$count=0;$byteCount=0L;$generation=0;$watch=[Diagnostics.Stopwatch]::StartNew()
  try {
   Import-Module -Name $diagnosticsManifest -ErrorAction Stop
   $status['diagnosticsModule']=(Get-Command Microsoft.PowerShell.Diagnostics\Get-Counter).Module.Path
   # Get-Counter expands wildcards once per query. Rebuild finite three-sample
   # queries so later UI/capture/export births can be discovered. Each new query
   # re-primes; no GPU interval proof crosses its generation boundary.
   while($count -lt 900 -and -not $status.ContainsKey('stopRequested')){
    if(++$generation -gt 300 -or $watch.Elapsed.TotalSeconds -gt 905){throw 'Resource query generation/deadline bound.'}
    $previous=@{};$previousCounterUtc=$null;$queryRow=0
    Microsoft.PowerShell.Diagnostics\Get-Counter -Counter $counterPaths -SampleInterval 1 -MaxSamples ([Math]::Min(3,900-$count)) -ErrorAction Continue | ForEach-Object {
    $queryRow++
    if(++$count -gt 900 -or $watch.Elapsed.TotalSeconds -gt 905){throw 'Resource sampler finite bound.'}
    if($_.CounterSamples.Count -gt 8192){throw 'Counter instance count bound.'}
    $counterSet=$_;$counterUtc=$counterSet.Timestamp.ToUniversalTime();$at=[Diagnostics.Stopwatch]::GetTimestamp();$current=@{};$byPid=@{};$processes=[Collections.Generic.List[object]]::new()
    if($lastCounterUtc -and $counterUtc -le $lastCounterUtc){throw 'Counter timestamps failed to advance.'}
    $coverage=@{};foreach($counterPath in $counterPaths){$coverage[$counterPath]=[ordered]@{valid=0;invalid=0}}
    foreach($entry in $handles.Values){
     $held=$entry.process
     try {
      if($held.HasExited){continue}
      $cpu=$held.TotalProcessorTime.TotalMilliseconds;$held.Refresh();$ws=$held.WorkingSet64;$private=$held.PrivateMemorySize64
      $io=[F10ReplayAcceptanceNative]::ReadIo($held);$memory=[F10ReplayAcceptanceNative]::ReadMemory($held)
      if($held.HasExited){continue}
      $key="$($entry.pid)/$($entry.startedUtc.Ticks)";$current[$key]=$entry
      if($byPid.ContainsKey([int]$entry.pid)){throw 'Two live retained births claim one numeric PID.'};$byPid[[int]$entry.pid]=$entry
      $processes.Add([pscustomobject]@{pid=$entry.pid;birthUtc=$entry.startedUtc;cpuMilliseconds=$cpu;workingSetBytes=$ws;privateBytes=$private;
       nativePeakWorkingSetBytes=$memory.PeakWorkingSet.ToUInt64();readBytes=$io.ReadBytes;writeBytes=$io.WriteBytes;otherIoBytes=$io.OtherBytes})
     }catch{if(-not $held.HasExited){throw}}
    }
    $gpu=[Collections.Generic.List[object]]::new();$disk=[Collections.Generic.List[object]]::new();$invalid=0;$unattributed=0
    foreach($sample in $counterSet.CounterSamples){
     $pathKey=$null
     if($sample.Path.Contains('\GPU Engine(',[StringComparison]::OrdinalIgnoreCase) -and $sample.Path.EndsWith('\Utilization Percentage',[StringComparison]::OrdinalIgnoreCase)){$pathKey=$counterPaths[0]}
     elseif($sample.Path.Contains('\GPU Process Memory(',[StringComparison]::OrdinalIgnoreCase)){$pathKey=if($sample.Path.EndsWith('\Dedicated Usage',[StringComparison]::OrdinalIgnoreCase)){$counterPaths[1]}elseif($sample.Path.EndsWith('\Shared Usage',[StringComparison]::OrdinalIgnoreCase)){$counterPaths[2]}else{$null}}
     elseif($sample.Path.Contains('\Disco físico(',[StringComparison]::OrdinalIgnoreCase)){$pathKey=if($sample.Path.EndsWith('\Bytes lidos de disco/seg',[StringComparison]::OrdinalIgnoreCase)){$counterPaths[3]}elseif($sample.Path.EndsWith('\Bytes escritos em disco/seg',[StringComparison]::OrdinalIgnoreCase)){$counterPaths[4]}else{$null}}
     if(-not $pathKey){throw 'Unexpected counter path outside the frozen five.'}
     if($sample.Status -notin @(0,1) -or [double]::IsNaN($sample.CookedValue) -or [double]::IsInfinity($sample.CookedValue)){$invalid++;if($pathKey){$coverage[$pathKey].invalid++};continue}
     if($pathKey){$coverage[$pathKey].valid++}
     if($sample.Path -like '*\Disco físico(*)\*'){
      $disk.Add([pscustomobject]@{instance=$sample.InstanceName;path=$sample.Path;bytesPerSecond=$sample.CookedValue});continue
     }
     if($sample.InstanceName -notmatch '^pid_(\d+)_'){$unattributed++;continue};$pidValue=[int]$Matches[1]
     if(-not $byPid.ContainsKey($pidValue)){$unattributed++;continue};$entry=$byPid[$pidValue];$key="$pidValue/$($entry.startedUtc.Ticks)"
     # Require the same held birth alive at BOTH interval endpoints. Short
     # children without two observations are N/A, never inferred as zero.
     if(-not $previous.ContainsKey($key) -or -not $previousCounterUtc -or $entry.startedUtc -gt $previousCounterUtc -or
       $entry.startedUtc -gt $counterUtc -or $entry.process.HasExited){$unattributed++;continue}
     $gpu.Add([pscustomobject]@{pid=$pidValue;birthUtc=$entry.startedUtc;instance=$sample.InstanceName;path=$sample.Path;value=$sample.CookedValue})
    }
    if($gpu.Count -gt 2048 -or $processes.Count -gt 16 -or $disk.Count -gt 32){throw 'Retained resource row bound.'}
    $row=[pscustomobject]@{qpc=$at;processReadsCompletedQpc=[Diagnostics.Stopwatch]::GetTimestamp();utc=$counterUtc;queryGeneration=$generation;queryRow=$queryRow;
     processes=$processes.ToArray();gpu=$gpu.ToArray();disk=$disk.ToArray();coverage=$coverage;invalidInstances=$invalid;unattributedOrUnprimedGpuInstances=$unattributed}
    $encoded=[Text.Encoding]::UTF8.GetBytes(($row|ConvertTo-Json -Compress -Depth 7))
    if($encoded.Length -gt 262144 -or $byteCount+$encoded.Length+1 -gt 32505840){throw 'Resource row256KiB/retained31MiB incremental byte bound (16 framing bytes reserved); prior prefix retained.'}
    $byteCount+=$encoded.Length+1;$rows.Enqueue($encoded)
    $status['bytes']=$byteCount;$status['count']=$count;$status['lastQpc']=$at;$previous=$current;$previousCounterUtc=$counterUtc;$lastCounterUtc=$counterUtc
    }
   }
  }catch{if(-not ($status.ContainsKey('stopRequested') -and $_.Exception -is [Management.Automation.PipelineStoppedException])){$status['error']=$_.ToString()}}
  finally{$status['finished']=$true}
 }
 return [pscustomobject]@{job=$job;rows=$queue;state=$state;paths=$paths;threadModule=(Get-Command Microsoft.PowerShell.ThreadJob\Start-ThreadJob).Module.Path;startedQpc=[Diagnostics.Stopwatch]::GetTimestamp()}
}
function Assert-ReplayResourceObserver {
 param($Observer,[double]$ElapsedSeconds)
 if($Observer.state.ContainsKey('error')){throw "Resource observer failed: $($Observer.state['error'])"}
 if($ElapsedSeconds -gt 10 -and (-not $Observer.state.ContainsKey('lastQpc') -or
  [Diagnostics.Stopwatch]::GetElapsedTime([long]$Observer.state['lastQpc']).TotalSeconds -gt 5)){throw 'Resource observer stalled; retain failed measurement.'}
 if($Observer.rows.Count -gt 900 -or $Observer.job.Error.Count -gt 4096 -or $Observer.state['bytes'] -gt 32505840){throw 'Resource observer queue/error/byte cap.'}
 if($Observer.job.State -in @('Completed','Failed','Stopped') -or $Observer.state.ContainsKey('finished')){throw 'Resource observer ended before owned stop.'}
}
function Stop-ReplayResourceObserver {
 param($Observer,[string]$Path)
 $Observer.state['stopRequested']=$true;$stopFailure=$null
 try{$Observer.job.StopJobAsync()}catch{$stopFailure=$_.ToString()}
 $deadline=[Diagnostics.Stopwatch]::StartNew()
 while(($Observer.job.State -notin @('Completed','Failed','Stopped') -or -not $Observer.state.ContainsKey('finished')) -and $deadline.ElapsedMilliseconds -lt 1500){Start-Sleep -Milliseconds 20}
 $joined=$Observer.job.State -in @('Completed','Failed','Stopped') -and $Observer.state.ContainsKey('finished');$Observer.state['joined']=$joined
 $jobState=$Observer.job.State.ToString();$reason=$Observer.job.JobStateInfo.Reason
 if(-not $joined){throw 'Resource runspace stop/body closure unconfirmed; retain all held Process objects.'}
 $failure=if($Observer.state.ContainsKey('error')){$Observer.state['error']}elseif($stopFailure){$stopFailure}elseif($jobState -ceq 'Failed'){"Resource job failed: $reason"}else{$null}
 try{
  $report=[pscustomobject]@{paths=$Observer.paths;threadModule=$Observer.threadModule;diagnosticsModule=$Observer.state['diagnosticsModule'];jobState=$jobState;jobReason=if($reason){$reason.ToString().Substring(0,[Math]::Min(8192,$reason.ToString().Length))}else{$null};joined=$joined;
   errorCount=$Observer.job.Error.Count;errors=@($Observer.job.Error|Select-Object -First 128|ForEach-Object {$s=$_.ToString();$s.Substring(0,[Math]::Min(512,$s.Length))});
   failure=if($failure){$failure.Substring(0,[Math]::Min(8192,$failure.Length))}else{$null};retainedRowBytes=$Observer.state['bytes'];
   scope='One-second PDH GPU/memory and whole-physical-disk rates. GPU attributed only to exact held births alive across two endpoints and born before previous PDH timestamp. Unobserved short children are N/A. Process I/O receipts may include pipes; whole-disk rates include other applications. Controller CPU/RAM includes sampler/owning-observer overhead; no causal subtraction.'}
  $metadata=[Text.Encoding]::UTF8.GetBytes(($report|ConvertTo-Json -Compress -Depth 4));if($metadata.Length -gt 1048575){throw 'Resource metadata1MiB cap.'}
  if($metadata.Length+[long]$Observer.state['bytes']+10 -gt 33554432){throw 'Resource JSON framing/metadata/prefix cap BEFORE creating output.'}
  if(Test-Path -LiteralPath $Path){throw 'Prior resource evidence exists.'}
  $writer=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
  try{
   $writer.Write($metadata,0,$metadata.Length-1);$writer.Write([Text.Encoding]::UTF8.GetBytes(',"rows":['));$first=$true
   foreach($row in $Observer.rows.ToArray()){if(-not $first){$writer.WriteByte(44)};$writer.Write([byte[]]$row);$first=$false}
   $writer.Write([Text.Encoding]::UTF8.GetBytes(']}'));$writer.Flush($true)
   if($writer.Length -gt 33554432){throw 'Resource report32MiB hard cap.'}
  }finally{$writer.Dispose()}
 }finally{Remove-Job -Job $Observer.job}
 if($failure){throw $failure}
}
