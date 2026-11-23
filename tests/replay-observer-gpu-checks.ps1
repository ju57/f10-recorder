param([switch]$Plan,[switch]$Run,[string]$Helper,[string]$ExpectedHelperSha256,[string]$ExpectedSdkSha256,[string]$EngineRoot,[string]$OutputRoot)
$ErrorActionPreference='Stop'
function Get-ReplayGeneratedCandidate([int]$Id){return [Diagnostics.Process]::GetProcessById($Id)}
function Get-ReplayGeneratedImage($Process){return [F10ReplayAcceptanceNative]::ReadImage($Process)}
function Adopt-ReplayGeneratedProducer($Ready,[string]$ExpectedImage){
 $candidate=$null
 try{
  $candidate=Get-ReplayGeneratedCandidate ([int]$Ready.pid);$null=$candidate.SafeHandle
  if($candidate.HasExited -or $candidate.StartTime.ToUniversalTime() -ne [DateTime]$Ready.birthUtc -or
   -not [string]::Equals((Get-ReplayGeneratedImage $candidate),$ExpectedImage,[StringComparison]::OrdinalIgnoreCase)){throw 'Exact live generated producer birth/image required before ownership transfer.'}
  $adopted=$candidate;$candidate=$null;return $adopted
 }finally{if($candidate){$candidate.Dispose()}} # Failed candidates are NEVER cleanup-owned or killed.
}
if(([int]$Plan.IsPresent+[int]$Run.IsPresent) -ne 1){throw 'Choose Plan or Run.'}
if($Plan){'Generated-GPU observer discovery check: one9-second320x192/60H264 color-to-null encoder born AFTER the first PDH query. No desktop/system/microphone input, audio, UI, playback, profile/startup/vendor/catalog/install action. PinnedSDK+frozenhelper/product+verifiedengine; helper+encoder+atmost1console+outer4processes.30secondswhole/25work5cleanup,64MiBroot/60GBfloor;32MiBobserver report. Require laterquerygeneration positive exact-birth GPUengine+memory samples, live nativeimage query, normal producer/helper closure; short/unprimed GPUrows N/A. Fresh absolute ignored OutputRoot.';return}
foreach($path in @($Helper,$EngineRoot,$OutputRoot)){if(-not [IO.Path]::IsPathFullyQualified($path)){throw 'Absolute paths required.'}}
$prefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))+[IO.Path]::DirectorySeparatorChar
if(-not $OutputRoot.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path $OutputRoot) -or -not (Test-Path (Split-Path -Parent $OutputRoot))){throw 'Fresh ignored root with existing parent required.'}
$sdk=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.tools/dotnet/dotnet.exe'))
if($ExpectedHelperSha256 -cnotmatch '^[A-F0-9]{64}$' -or $ExpectedSdkSha256 -cnotmatch '^[A-F0-9]{64}$' -or
 (Get-FileHash $Helper).Hash -cne $ExpectedHelperSha256 -or (Get-FileHash $sdk).Hash -cne $ExpectedSdkSha256){throw 'Exact independently reviewed helper/SDK launch pins required.'}
$engine=Join-Path $EngineRoot 'ffmpeg-9.0.2\bin\ffmpeg.exe'
if((Get-FileHash $engine).Hash -cne '3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC'){throw 'Exact approved engine required.'}
$helperDirectory=Split-Path -Parent $Helper
if((Get-FileHash (Join-Path $helperDirectory 'F10Recorder.Windows.dll')).Hash -cne '67B6CF9BEC22B2FD3D95D53D1F9EF046E97A9F4D3AB17DF739C6B38883F5FF55' -or
 (Get-FileHash (Join-Path $helperDirectory 'F10Recorder.Core.dll')).Hash -cne '2F445D3EE31004378B26C10F9200FDDB21743675A8E29B3AACDC04B1E81A17CA'){throw 'Unchanged qualified product required.'}
if((Get-PSDrive D).Free -lt 60067108864L){throw '64MiB+60GBfloor required.'}
. (Join-Path $PSScriptRoot 'replay-resource-observer.ps1')
. (Join-Path $PSScriptRoot 'replay-acceptance-budget.ps1')
[IO.Directory]::CreateDirectory($OutputRoot)|Out-Null
$bindingPaths=@($sdk,$Helper,(Join-Path $helperDirectory 'F10Recorder.Windows.dll'),(Join-Path $helperDirectory 'F10Recorder.Core.dll'),$engine,$PSCommandPath,
 (Join-Path $helperDirectory 'F10Recorder.WorkerChecks.deps.json'),(Join-Path $helperDirectory 'F10Recorder.WorkerChecks.runtimeconfig.json'),
 (Join-Path $PSScriptRoot '../projects/F10Recorder.WorkerChecks/ReplayObserverGpuFixture.cs'),(Join-Path $PSScriptRoot '../projects/F10Recorder.WorkerChecks/Program.cs'),
 (Join-Path $PSScriptRoot 'ReplayAcceptanceNative.cs'),(Join-Path $PSScriptRoot 'replay-resource-observer.ps1'),(Join-Path $PSScriptRoot 'replay-acceptance-budget.ps1'))
$bindings=@{};foreach($file in $bindingPaths){$bindings[$file]=(Get-FileHash $file).Hash}
$bindings|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputRoot 'inputs.json')
$self=[Diagnostics.Process]::GetCurrentProcess();$null=$self.SafeHandle;$birth=$self.StartTime.ToUniversalTime()
$owned=[Collections.Concurrent.ConcurrentDictionary[string,object]]::new();$owned.TryAdd("$($self.Id)/$($birth.Ticks)",[pscustomobject]@{process=$self;pid=$self.Id;startedUtc=$birth})|Out-Null
$clock=[Diagnostics.Stopwatch]::StartNew();$observer=$null;$helperProcess=$null;$helperLaunchAttempted=$false;$producer=$null;$failure=$null;$forced=$false;$outCopy=$null;$errCopy=$null;$outFile=$null;$errFile=$null;$gpuRows=@();$ready=$null;$producerClosure=$null;$closureSha=$null
function Guard {
 if($clock.Elapsed.TotalSeconds -gt 25){throw '25second work deadline/5second cleanup reserve.'}
 Assert-ReplayResourceObserver $observer $clock.Elapsed.TotalSeconds
 $inventory=Measure-ReplayAcceptanceStorage @($OutputRoot) 256 32
 if($inventory.bytes -gt 33554432 -or (Get-PSDrive D).Free -lt 60000000000L){throw 'Generated probe early32MiB/60GBfloor.'}
 foreach($copy in @($outCopy,$errCopy)){if($copy -and ($copy.Overflow -or $copy.Completion.IsFaulted)){throw 'Generated helper bounded-log failure.'}}
}
try{
 $observer=Start-ReplayResourceObserver $owned
 while($observer.rows.Count -lt 2){Guard;Start-Sleep -Milliseconds 50}
 $start=[Diagnostics.ProcessStartInfo]::new($sdk);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($arg in @($Helper,'--replay-observer-gpu-fixture',(Join-Path $OutputRoot 'media'),$EngineRoot)){$start.ArgumentList.Add($arg)}
 $helperLaunchAttempted=$true;$helperProcess=[Diagnostics.Process]::Start($start);$null=$helperProcess.SafeHandle;$helperBirth=$helperProcess.StartTime.ToUniversalTime()
 $owned.TryAdd("$($helperProcess.Id)/$($helperBirth.Ticks)",[pscustomobject]@{process=$helperProcess;pid=$helperProcess.Id;startedUtc=$helperBirth})|Out-Null
 $outFile=[IO.FileStream]::new((Join-Path $OutputRoot 'stdout.txt'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
 $errFile=[IO.FileStream]::new((Join-Path $OutputRoot 'stderr.txt'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
 $outCopy=[F10ReplayAcceptanceNative+PrefixCopy]::new($helperProcess.StandardOutput.BaseStream,$outFile,2097152);$errCopy=[F10ReplayAcceptanceNative+PrefixCopy]::new($helperProcess.StandardError.BaseStream,$errFile,2097152)
 $readyPath=Join-Path $OutputRoot 'media\producer.json'
 while(-not (Test-Path $readyPath)){Guard;if($helperProcess.HasExited){throw 'Generated helper failed before producer receipt.'};Start-Sleep -Milliseconds 50}
 if(([IO.FileInfo]::new($readyPath)).Length -gt 65536){throw 'Producer identity64KiB cap.'};$ready=Get-Content -Raw $readyPath|ConvertFrom-Json
 $producer=Adopt-ReplayGeneratedProducer $ready $engine;$producerBirth=$producer.StartTime.ToUniversalTime()
 $owned.TryAdd("$($producer.Id)/$($producerBirth.Ticks)",[pscustomobject]@{process=$producer;pid=$producer.Id;startedUtc=$producerBirth})|Out-Null
 while(-not $helperProcess.WaitForExit(50)){Guard}
 if($helperProcess.ExitCode -ne 0 -or -not $producer.WaitForExit(2000) -or $producer.ExitCode -ne 0){throw 'Normal generated producer/helper closure required.'}
 $closurePath=Join-Path $OutputRoot 'media\producer-closure.json'
 if(([IO.FileInfo]::new($closurePath)).Length -gt 262144){throw 'Producer closure262144byte cap.'}
 $producerClosure=Get-Content -Raw $closurePath|ConvertFrom-Json;$closureSha=(Get-FileHash $closurePath).Hash
 if($producerClosure.ProcessId -ne $producer.Id -or [DateTime]$producerClosure.ProcessStartedUtc -ne $producerBirth -or
  -not $producerClosure.ProducerAbsent -or -not $producerClosure.ActualExitObserved -or -not $producerClosure.ResourcesFinal -or
  $producerClosure.ExitCode -ne 0 -or $producerClosure.Error -or $producerClosure.Failure -or $producerClosure.ResourceError){throw 'Exact producer closure/identity/resource receipt required.'}
 $parsed=@($observer.rows.ToArray()|ForEach-Object {[Text.Encoding]::UTF8.GetString([byte[]]$_)|ConvertFrom-Json})
 if($producerBirth -le $parsed[0].utc){throw 'Producer must be born AFTER initial query sample.'}
 $gpuRows=@($parsed|ForEach-Object {$generation=$_.queryGeneration;foreach($row in $_.gpu){if($row.pid -eq $producer.Id -and [DateTime]$row.birthUtc -eq $producerBirth){[pscustomobject]@{generation=$generation;instance=$row.instance;path=$row.path;value=$row.value}}}})
 if(@($gpuRows|Where-Object {$_.generation -ge 2 -and $_.path -like '*Utilization Percentage' -and $_.value -gt 0}).Count -eq 0 -or
  @($gpuRows|Where-Object {$_.generation -ge 2 -and $_.path -like '*Dedicated Usage' -and $_.value -gt 0}).Count -eq 0){throw 'Later-born generated producer lacks positive GPU engine/memory attribution after refresh.'}
}catch{$failure=$_.ToString()}
finally{
 $cleanup=[Diagnostics.Stopwatch]::StartNew();function Remaining{return [Math]::Max(0,5000-[int]$cleanup.ElapsedMilliseconds)}
 try{if($observer){Stop-ReplayResourceObserver $observer (Join-Path $OutputRoot 'resources.json')}}catch{$failure="$failure Observer:$_"}
 foreach($process in @($helperProcess,$producer)){if($process){try{if(-not $process.HasExited){$forced=$true;$process.Kill()};if(-not $process.WaitForExit((Remaining))){throw 'Owned generated process absence unconfirmed.'}}catch{$failure="$failure Cleanup:$_"}}}
 foreach($copy in @($outCopy,$errCopy)){if($copy){try{if(-not $copy.Completion.Wait((Remaining))){$copy.Cancel();throw 'Generated log reader unjoined.'};$null=$copy.Completion.GetAwaiter().GetResult();if($copy.Overflow){throw 'Generated helper log overflow.'}}catch{$failure="$failure Logs:$_"}}}
 foreach($pair in @(@($outCopy,$outFile),@($errCopy,$errFile))){if($pair[1] -and ($null -eq $pair[0] -or $pair[0].Completion.IsCompleted)){$pair[1].Dispose();if($pair[0]){$pair[0].Dispose()}}}
 if($forced){$failure="$failure Forced cleanup required."}
 try{foreach($file in $bindings.Keys){if((Get-FileHash $file).Hash -cne $bindings[$file]){throw 'Generated probe binding changed.'}};$inventory=Measure-ReplayAcceptanceStorage @($OutputRoot) 256 32;if($inventory.bytes -gt 67108864){throw 'Generated probe64MiB hard cap.'}}catch{$failure="$failure Terminal binding/budget:$_"}
 $producerAbsent=if($producer){$producer.HasExited}elseif(-not $helperLaunchAttempted){$true}else{$null}
 if($null -eq $producerAbsent){
  $closurePath=Join-Path $OutputRoot 'media\producer-closure.json'
  if($ready -and (Test-Path $closurePath)){try{if(([IO.FileInfo]::new($closurePath)).Length -gt 262144){throw 'Closure input bound.'};$closure=Get-Content -Raw $closurePath|ConvertFrom-Json;if($closure.ProcessId -eq $ready.pid -and [DateTime]$closure.ProcessStartedUtc -eq [DateTime]$ready.birthUtc -and $closure.ProducerAbsent -and $closure.ActualExitObserved){$producerAbsent=$true}}catch{$failure="$failure Closure evidence:$_"}}
 }
 [pscustomobject]@{utc=[DateTime]::UtcNow;seconds=$clock.Elapsed.TotalSeconds;error=$failure;forced=$forced;expectedHelperSha256=$ExpectedHelperSha256;expectedSdkSha256=$ExpectedSdkSha256;helperPid=$helperProcess.Id;helperBirth=$helperBirth;helperAbsent=if($helperProcess){$helperProcess.HasExited}elseif(-not $helperLaunchAttempted){$true}else{$null};helperExit=if($helperProcess -and $helperProcess.HasExited){$helperProcess.ExitCode}else{$null};producer=$ready;producerClosureSha256=$closureSha;producerClosure=$producerClosure;producerAbsent=$producerAbsent;gpuRows=$gpuRows;scope='Generated9secondGPUcolorencode only; positive later-query PID+birth attribution control, no live capture/playback/performance/resource acceptance. Closure claim only for held helper/producer; console not observed separately. Unadopted producer absence stays unknown unless exact durable closure proves it.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $OutputRoot 'result.json')
 if(-not $observer -or $observer.state['joined']){foreach($process in @($helperProcess,$producer,$self)){if($process){$process.Dispose()}}}
}
if($clock.Elapsed.TotalSeconds -gt 30){throw 'Generated probe30second whole deadline.'}
if($failure){throw $failure}
'Generated GPU producer discovered after query refresh; native processes absent, no user capture.'
