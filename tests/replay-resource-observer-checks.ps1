param([switch]$Plan,[string]$OutputFile,[ValidateSet(3,6)][int]$Samples=3)
$ErrorActionPreference='Stop'
if($Plan){"Read-only Windows PDH sampler probe: one current-process held birth; no child process, UI, recording, audio, ETW or settings changes. At most$($Samples+9)seconds whole/32MiB report,60GBfloor,$Samples one-second samples. Six-sample route checks query refresh/re-prime; held-image ABI also checked. Fresh absolute ignored output required.";return}
if(-not $OutputFile -or -not [IO.Path]::IsPathFullyQualified($OutputFile) -or
 -not $OutputFile.StartsWith(([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))+[IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase) -or
 (Test-Path -LiteralPath $OutputFile) -or -not (Test-Path -LiteralPath (Split-Path -Parent $OutputFile))){throw 'Fresh private existing artifact parent/output required.'}
if((Get-PSDrive D).Free -lt 60033554432L){throw 'Probe32MiB+60GBfloor.'}
. (Join-Path $PSScriptRoot 'replay-resource-observer.ps1')
$self=[Diagnostics.Process]::GetCurrentProcess();$null=$self.SafeHandle;$birth=$self.StartTime.ToUniversalTime()
$image=[F10ReplayAcceptanceNative]::ReadImage($self)
if(-not [string]::Equals($image,$self.MainModule.FileName,[StringComparison]::OrdinalIgnoreCase)){throw 'Held-process native image differs from initialized current-process image.'}
$owned=[Collections.Concurrent.ConcurrentDictionary[string,object]]::new()
$owned.TryAdd("$($self.Id)/$($birth.Ticks)",[pscustomobject]@{process=$self;pid=$self.Id;startedUtc=$birth})|Out-Null
$clock=[Diagnostics.Stopwatch]::StartNew();$observer=$null;$failure=$null
try{
 $observer=Start-ReplayResourceObserver $owned
 while($observer.rows.Count -lt $Samples){Assert-ReplayResourceObserver $observer $clock.Elapsed.TotalSeconds;if($clock.Elapsed.TotalSeconds -gt $Samples+7){throw 'Finite sample bootstrap deadline.'};Start-Sleep -Milliseconds 50}
 foreach($bytes in $observer.rows.ToArray()){
  $row=[Text.Encoding]::UTF8.GetString([byte[]]$bytes)|ConvertFrom-Json
  if($row.processes.Count -ne 1 -or $row.processes[0].pid -ne $self.Id -or $row.processes[0].birthUtc -ne $birth -or $row.disk.Count -lt 2){throw 'Real held-process/disk sample missing.'}
  if($null -eq $row.processes[0].readBytes -or $null -eq $row.processes[0].writeBytes -or $row.processes[0].nativePeakWorkingSetBytes -lt $row.processes[0].workingSetBytes){throw 'Native held-process I/O/peak memory receipt missing.'}
  foreach($path in $observer.paths){if($row.coverage.$path.valid -lt 1){throw "No valid counter instances for $path"}}
  if($row.processReadsCompletedQpc -lt $row.qpc -or ($row.queryRow -eq 1 -and $row.gpu.Count -ne 0)){throw 'Process read bracket or query re-prime evidence failed.'}
 }
 if($Samples -eq 6){$parsed=@($observer.rows.ToArray()|ForEach-Object {[Text.Encoding]::UTF8.GetString([byte[]]$_)|ConvertFrom-Json});if(@($parsed.queryGeneration|Sort-Object -Unique).Count -lt 2){throw 'Finite queries did not refresh.'}}
}catch{$failure=$_}
finally{try{if($observer){Stop-ReplayResourceObserver $observer $OutputFile}}catch{$failure=$_};if(-not $observer -or $observer.state['joined']){$self.Dispose()}}
if($clock.Elapsed.TotalSeconds -gt $Samples+9){throw 'Probe whole deadline.'}
if($failure){throw $failure}
"Passed read-only sampler probe: $($observer.rows.Count) samples; $($clock.Elapsed.TotalSeconds) seconds; exact held birth; runspace stopped."
