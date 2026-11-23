param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference='Stop'
if($Plan -eq $Run){throw 'Choose Plan or Run'}
$path=(Resolve-Path -LiteralPath $PlanFile).Path
$raw=[IO.File]::ReadAllBytes($path);if($raw.Length -gt 65536){throw '64KiB plan bound'}
$sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))
$p=[Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json
if($p.Kind -cne 'issue83-reference-envelope-diagnostic-v1'){throw 'Wrong diagnostic scope'}
if($Plan){$p|ConvertTo-Json -Depth 6;return}
if($sha -cne $ExpectedPlanSha256 -or [DateTime]::UtcNow.AddSeconds(30) -ge ([DateTimeOffset]$p.ExpiresUtc).UtcDateTime -or (Test-Path -LiteralPath $p.Root)){throw 'Exact fresh30second plan required'}
foreach($f in $p.Files.PSObject.Properties){if((Get-FileHash -LiteralPath $f.Name).Hash -cne $f.Value){throw 'Diagnostic binding changed'}}
$runtime=@($p.Files.PSObject.Properties.Name|Where-Object {[IO.Path]::GetFileName($_) -ceq 'dotnet.exe'})
$helper=@($p.Files.PSObject.Properties.Name|Where-Object {[IO.Path]::GetFileName($_) -ceq 'F10Recorder.WorkerChecks.dll'})
if($runtime.Count -ne 1 -or $helper.Count -ne 1 -or $p.Files.PSObject.Properties.Name -notcontains $PSCommandPath){throw 'Exact runtime/helper/controller required'}
if(([IO.DriveInfo]::new([IO.Path]::GetPathRoot($p.Root))).AvailableFreeSpace -lt 60000000000){throw 'Free-space floor'}
$directory=Split-Path -Parent $path
$stdout=Join-Path $directory stdout.txt;$stderr=Join-Path $directory stderr.txt;$receipt=Join-Path $directory controller.json
foreach($f in @($stdout,$stderr,$receipt)){if(Test-Path -LiteralPath $f){throw 'Preserve previous attempt'}}
$start=[Diagnostics.ProcessStartInfo]::new($runtime[0]);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
foreach($arg in @($helper[0],'--replay-reference-envelope-diagnostic',$path,$sha)){$start.ArgumentList.Add($arg)}
$clock=[Diagnostics.Stopwatch]::StartNew();$owned=$null;$failure=$null;$exit=$null;$peakRam=0L;$peakHandles=0
$outFile=$null;$errFile=$null;$outCopy=$null;$errCopy=$null
$childExitObservedSeconds=$null;$finalStdoutBytes=$null;$finalStderrBytes=$null;$finalReportBytes=$null
try{
 $owned=[Diagnostics.Process]::Start($start)
 $outFile=[IO.FileStream]::new($stdout,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite,4096,[IO.FileOptions]::Asynchronous)
 $errFile=[IO.FileStream]::new($stderr,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite,4096,[IO.FileOptions]::Asynchronous)
 $outCopy=$owned.StandardOutput.BaseStream.CopyToAsync($outFile);$errCopy=$owned.StandardError.BaseStream.CopyToAsync($errFile)
 while(-not $owned.WaitForExit(100)){
  $owned.Refresh();$peakRam=[Math]::Max($peakRam,$owned.WorkingSet64);$peakHandles=[Math]::Max($peakHandles,$owned.HandleCount)
  if($clock.Elapsed.TotalSeconds -ge 20 -or $peakRam -gt 268435456 -or $peakHandles -gt 4096 -or $outFile.Length -gt 1048576 -or $errFile.Length -gt 1048576){throw 'Diagnostic process/time/log bound'}
 }
 $childExitObservedSeconds=$clock.Elapsed.TotalSeconds
 $exit=$owned.ExitCode;if($exit -ne 0){throw "Diagnostic failed:$exit"}
 if($childExitObservedSeconds -ge 20){throw 'Diagnostic active deadline, including fast exit'}
}catch{$failure=$_.ToString()}
finally{
 try{if($owned -and -not $owned.HasExited){$owned.Kill();if(-not $owned.WaitForExit(5000)){$failure="$failure Owned absence unconfirmed"}}}catch{$failure="$failure Cleanup:$_"}
 try{
  if($outCopy){
   [Threading.Tasks.Task[]]$drainTasks=@($outCopy,$errCopy)
   $drain=[Threading.Tasks.Task]::WhenAll($drainTasks)
   if(-not $drain.Wait(1000)){$failure="$failure Log drain unconfirmed"}
  }
 }catch{$failure="$failure Log drain:$_"}
 if($outFile){$outFile.Dispose()};if($errFile){$errFile.Dispose()}
 try{
  $finalStdoutBytes=(Get-Item -LiteralPath $stdout).Length;$finalStderrBytes=(Get-Item -LiteralPath $stderr).Length
  $reports=@(Get-ChildItem -LiteralPath $p.Root -File -Recurse -Force -ErrorAction Stop)
  if($reports.Count -gt 16 -or @($reports|Where-Object {($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0}).Count){throw 'Diagnostic report count/reparse bound'}
  $finalReportBytes=($reports|Measure-Object Length -Sum).Sum
  if($finalStdoutBytes -gt 1048576 -or $finalStderrBytes -gt 1048576 -or $finalReportBytes+$finalStdoutBytes+$finalStderrBytes -gt 1048576){throw 'Final aggregate1MiB report/log bound'}
 }catch{$failure="$failure Final output checks:$_"}
 if($clock.Elapsed.TotalSeconds -ge 30){$failure="$failure Whole30second bound"}
 [ordered]@{planSha256=$sha;pid=$owned.Id;absent=(-not $owned -or $owned.HasExited);exitCode=$exit;seconds=$clock.Elapsed.TotalSeconds;childExitObservedSeconds=$childExitObservedSeconds;finalStdoutBytes=$finalStdoutBytes;finalStderrBytes=$finalStderrBytes;finalReportBytes=$finalReportBytes;peakRam=$peakRam;peakHandles=$peakHandles;error=$failure;claim='One metadata-only helper/no media child or publication; unqualified model diagnostic only. Reconstructed prefix is not the unknown actual press-time prefix. Resource peaks are sampled plus helper selfchecks.'}|ConvertTo-Json|Set-Content -LiteralPath $receipt
 if($owned){$owned.Dispose()}
}
if($failure){throw $failure}
Get-Content -LiteralPath $stdout
