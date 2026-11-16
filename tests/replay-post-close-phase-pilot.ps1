param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference='Stop'
if($Plan -eq $Run){throw 'Choose Plan or Run'}
$path=(Resolve-Path -LiteralPath $PlanFile).Path
$input=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{if($input.Length -le 0 -or $input.Length -gt 65536){throw '64KiB plan limit'};$raw=[byte[]]::new([int]$input.Length);$input.ReadExactly($raw)}finally{$input.Dispose()}
$sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw));$p=[Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json
if($p.kind -cne 'issue83-post-close-phase-v1' -or $p.activeSeconds -ne 80 -or $p.outerSeconds -ne 90 -or $p.outputBytes -ne 800000000 -or $p.writePasses -ne 2 -or $p.callbackCadenceMilliseconds -ne 100){throw 'Wrong finite phase scope'}
foreach($f in $p.files.PSObject.Properties){if((Get-FileHash -LiteralPath $f.Name).Hash -cne $f.Value){throw 'Changed phase binding'}}
foreach($role in @($PSCommandPath,$p.runtime,$p.helper)){if($p.files.PSObject.Properties.Name -notcontains $role){throw 'Unbound execution role'}}
if((Test-Path -LiteralPath $p.root) -or [DateTime]::UtcNow.AddSeconds(90) -ge ([DateTimeOffset]$p.expiresUtc).UtcDateTime){throw 'Fresh root/full90s window required'}
if($Plan){[ordered]@{planSha256=$sha;effects=$p.effects;resources=$p.resources;expiresUtc=$p.expiresUtc}|ConvertTo-Json -Depth 6;return}
if($sha -cne $ExpectedPlanSha256){throw 'Exact reviewed raw plan required'}
$directory=Split-Path -Parent $path;$stdout=Join-Path $directory stdout.txt;$stderr=Join-Path $directory stderr.txt;$receipt=Join-Path $directory controller.json
foreach($file in @($stdout,$stderr,$receipt)){if(Test-Path -LiteralPath $file){throw 'Preserve prior attempt'}}
$clock=[Diagnostics.Stopwatch]::StartNew();$owned=$null;$failure=$null;$exitCode=$null;$peakRam=0L;$peakHandles=0
$outFile=$null;$errFile=$null;$outCopy=$null;$errCopy=$null
function Logs {
 foreach($file in @($stdout,$stderr)){
  if(Test-Path -LiteralPath $file){$r=[IO.FileStream]::new($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));try{if($r.Length -gt 1048576){throw '1MiB log cap'}}finally{$r.Dispose()}}
 }
}
try{
 $start=[Diagnostics.ProcessStartInfo]::new($p.runtime);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($a in @($p.helper,'--replay-post-close-phase-pilot',$path,$sha)){$start.ArgumentList.Add($a)}
 $owned=[Diagnostics.Process]::Start($start);$null=$owned.SafeHandle
 $outFile=[IO.FileStream]::new($stdout,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete),16384,[IO.FileOptions]::Asynchronous)
 $errFile=[IO.FileStream]::new($stderr,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete),16384,[IO.FileOptions]::Asynchronous)
 $outCopy=$owned.StandardOutput.BaseStream.CopyToAsync($outFile);$errCopy=$owned.StandardError.BaseStream.CopyToAsync($errFile)
 while(-not $owned.WaitForExit(100)){
  $owned.Refresh();$peakRam=[Math]::Max($peakRam,$owned.WorkingSet64);$peakHandles=[Math]::Max($peakHandles,$owned.HandleCount)
  if($clock.Elapsed.TotalSeconds -ge 80 -or $peakRam -gt 268435456 -or $peakHandles -gt 4096){throw 'Outer active/resource bound'}
  Logs
 }
 $exitCode=$owned.ExitCode;if($exitCode -ne 0 -or $clock.Elapsed.TotalSeconds -ge 80){throw 'Phase helper failed or active deadline exceeded'}
}catch{$failure=$_.ToString()}
finally{
 $cleanup=[Diagnostics.Stopwatch]::StartNew()
 function Remaining {return [Math]::Max(0,10000-[int]$cleanup.ElapsedMilliseconds)}
 try{if($owned -and -not $owned.HasExited){$owned.Kill();if(-not $owned.WaitForExit((Remaining))){throw 'Owned helper absence unconfirmed'}}}catch{$failure="$failure Cleanup:$_"}
 foreach($copy in @($outCopy,$errCopy)){if($copy){try{if(-not $copy.Wait((Remaining))){throw 'Log drain incomplete'};$null=$copy.GetAwaiter().GetResult()}catch{$failure="$failure LogDrain:$_"}}}
 if($outFile -and $outCopy.IsCompleted){$outFile.Dispose()};if($errFile -and $errCopy.IsCompleted){$errFile.Dispose()}
 try{Logs}catch{$failure="$failure FinalLogs:$_"}
 [ordered]@{planSha256=$sha;pid=$owned.Id;absent=(-not $owned -or $owned.HasExited);exitCode=$exitCode;seconds=$clock.Elapsed.TotalSeconds;peakRam=$peakRam;peakHandles=$peakHandles;error=$failure;
 scope='One file-only helper/no media child/capture/audio/UI/globalSampler. No outer inventory/CIM/extra output reader. Native component cap and local output limits belong to the helper.'}|ConvertTo-Json|Set-Content -LiteralPath $receipt
 if($owned){$owned.Dispose()}
}
if($failure){throw $failure}
