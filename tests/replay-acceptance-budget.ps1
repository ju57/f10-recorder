function Measure-ReplayAcceptanceStorage {
 param([string[]]$Roots,[int]$MaximumFiles=16384,[int]$MaximumDirectories=2048)
 $bytes=0L;$files=0;$directories=0;$pending=[Collections.Generic.Stack[string]]::new()
 foreach($root in $Roots){if(-not [IO.Directory]::Exists($root)){throw 'Acceptance storage root missing.'};$pending.Push($root)}
 while($pending.Count){
  $directory=$pending.Pop()
  try{$entries=[IO.Directory]::GetFileSystemEntries($directory)}catch [IO.DirectoryNotFoundException]{if($Roots -contains $directory){throw};continue}
  if(++$directories -gt $MaximumDirectories){throw 'Acceptance directory cap.'}
  foreach($entry in $entries){
   try{
    $attributes=[IO.File]::GetAttributes($entry)
    if(($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Acceptance reparse rejected.'}
    if(($attributes -band [IO.FileAttributes]::Directory) -ne 0){$pending.Push($entry)}
    else{if(++$files -gt $MaximumFiles){throw 'Acceptance file cap.'};$length=([IO.FileInfo]::new($entry)).Length;if($length -gt [long]::MaxValue-$bytes){throw 'Acceptance byte overflow.'};$bytes+=$length}
   }catch [IO.FileNotFoundException]{}catch [IO.DirectoryNotFoundException]{}
  }
 }
 [pscustomobject]@{bytes=$bytes;files=$files;directories=$directories}
}
function Assert-ReplayAcceptanceStorage {
 param($Batch)
 $scan=[Diagnostics.Stopwatch]::StartNew()
 $inventory=Measure-ReplayAcceptanceStorage @($Batch.root,$Batch.admission)
 if($Batch.budgetBytes -ne 31000000000L -or $Batch.stopBytes -ne 30500000000L -or $Batch.minimumFreeBytes -ne 60000000000L){throw 'Exact conditional-retention storage policy required.'}
 if($inventory.bytes -ge [long]$Batch.stopBytes -or [IO.DriveInfo]::new([IO.Path]::GetPathRoot($Batch.root)).AvailableFreeSpace -lt 60000000000L){throw 'Whole batch storage stop/floor.'}
 if($scan.Elapsed.TotalMilliseconds -gt 500){throw 'Whole batch scan exceeded500ms; failed observer remains retained.'}
 return $inventory
}
function Assert-ReplaySealedPairBytes([long]$Bytes,[long]$Maximum=2000000000L){
 if($Maximum -ne 2000000000L -or $Bytes -lt 0){throw 'Exact nonnegative2GB experiment-retention rule required.'}
 if($Bytes -gt $Maximum){throw 'Experiment storage-budget failure: complete sealed pair exceeds prospective2GB STOP; retain full12GB current slot, fail whole batch, no next capture/substitution/quantile acceptance.'}
}
