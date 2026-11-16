# Bounded owner for the five prospectively declared METADATA helper roles.
# These selectors have no producer/codec/capture path. Caller owns authorization.
function Invoke-ConfirmationMetadata([string]$Runtime,[string]$Helper,[string[]]$Arguments,[string]$EvidenceRoot,[DateTime]$DeadlineUtc){
 $selectors=@('--replay-same-C00-freeze','--replay-owned-history-outside-prepare','--replay-calibration-preflight','--replay-same-C00-accept')
 if($Arguments.Count-lt2-or$Arguments[0]-cnotin$selectors){throw 'Only the four reviewed metadata selectors are supported'}
 $prefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/issue83-same-capture-plan-20261114/admissions'))+'\'
 $root=[IO.Path]::GetFullPath($EvidenceRoot)
 if(-not$root.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $root)){throw 'Fresh bounded metadata evidence root required'}
 if([DateTime]::UtcNow.AddSeconds(60)-ge$DeadlineUtc){throw 'Original metadata deadline cannot fit60s; no refresh'}
 [void][IO.Directory]::CreateDirectory($root)
 $start=[Diagnostics.ProcessStartInfo]::new($Runtime);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($value in @($Helper)+$Arguments){$start.ArgumentList.Add($value)}
 $clock=[Diagnostics.Stopwatch]::StartNew();$owned=$null;$failure=$null;$forced=$false;$exit=$null;$peak=0L;$handles=0;$birth=$null;$helperPid=$null;$stdout=$null;$stderr=$null;$absent=$null;$disposed=$false;$cpu=$null
 $runtimeHash=$null;$helperHash=$null
 try{
  if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($root)).AvailableFreeSpace-lt60000000000){throw 'Metadata admission60GB free floor'}
  $runtimeHash=(Get-FileHash -LiteralPath $Runtime).Hash;$helperHash=(Get-FileHash -LiteralPath $Helper).Hash
  $owned=[Diagnostics.Process]::Start($start);$helperPid=$owned.Id;$held=$owned.SafeHandle;$birth=$owned.StartTime.ToUniversalTime()
  if($held.IsInvalid-or$held.IsClosed){throw 'Actual held metadata helper handle required'}
  $stdout=$owned.StandardOutput.ReadToEndAsync();$stderr=$owned.StandardError.ReadToEndAsync()
  while(-not$owned.WaitForExit(50)){
   $owned.Refresh();$peak=[Math]::Max($peak,$owned.WorkingSet64);$handles=[Math]::Max($handles,$owned.HandleCount)
   if($clock.Elapsed.TotalSeconds-ge50-or[DateTime]::UtcNow-ge$DeadlineUtc.AddSeconds(-10)-or$peak-gt536870912-or$handles-gt4096-or
    [IO.DriveInfo]::new([IO.Path]::GetPathRoot($root)).AvailableFreeSpace-lt60000000000){throw 'Metadata helper sampled time/RAM/handles/free bound'}
  }
  $exit=$owned.ExitCode;if($exit-ne0){throw "Metadata helper exit:$exit; no retry"}
 }catch{$failure=$_.ToString()}
 finally{
  try{if($owned-and-not$owned.HasExited){$forced=$true;$owned.Kill();if(-not$owned.WaitForExit([Math]::Max(0,[Math]::Min(5000,57000-[int]$clock.ElapsedMilliseconds)))){$failure+=' Metadata helper absence unconfirmed'}}}catch{$failure+=' Metadata cleanup:'+$_}
  if($owned){
   foreach($pair in @(@('stdout',$stdout),@('stderr',$stderr))){
    try{
     if($null-eq$pair[1]){continue}
     if(-not$pair[1].Wait([Math]::Max(0,[Math]::Min(1000,59000-[int]$clock.ElapsedMilliseconds)))){$failure+=' Metadata log not joined';continue}
     # These pinned selectors emit fixed summaries or bounded admission errors;
     # post-exit cap is an assertion, not an OS pipe/memory quota.
     $bytes=[Text.Encoding]::UTF8.GetBytes($pair[1].Result);if($bytes.Length-gt1048576){$failure+=' Metadata log1MiB bound';continue}
     $f=[IO.FileStream]::new((Join-Path $root ($pair[0]+'.txt')),'CreateNew','Write','Read');try{$f.Write($bytes);$f.Flush($true)}finally{$f.Dispose()}
    }catch{$failure+=' Metadata log drain/persistence:'+$_}
   }
  }
  try{if($owned){$absent=$owned.HasExited;if($absent){$cpu=$owned.TotalProcessorTime.TotalMilliseconds}}}catch{$failure+=' Metadata closure/resources:'+$_}
  try{if($owned){$owned.Dispose();$disposed=$true}}catch{$failure+=' Metadata handle disposal:'+$_}
  if($clock.Elapsed.TotalSeconds-ge60-or[DateTime]::UtcNow-ge$DeadlineUtc){$failure+=' Metadata whole60s/original deadline'}
  $receipt=@{kind='bounded-confirmation-metadata-v1';selector=$Arguments[0];runtime=$Runtime;runtimeSha256=$runtimeHash;
   helper=$Helper;helperSha256=$helperHash;arguments=$Arguments;pid=$helperPid;birth=$birth;absent=$absent;exitCode=$exit;forced=$forced;
   postDisposal=$true;processHandleDisposed=$disposed;seconds=$clock.Elapsed.TotalSeconds;cpuMilliseconds=$cpu;peakRam=$peak;peakHandles=$handles;error=$failure;mediaStarted=$false}
  $bytes=[Text.Encoding]::UTF8.GetBytes(($receipt|ConvertTo-Json -Depth 8));$file=[IO.FileStream]::new((Join-Path $root 'controller.json'),'CreateNew','Write','Read')
  try{$file.Write($bytes);$file.Flush($true)}finally{$file.Dispose()}
 }
 if($failure){throw $failure}
}
