param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference='Stop'
if($Plan -eq $Run){throw 'Choose Plan or Run'}
$path=(Resolve-Path -LiteralPath $PlanFile).Path
$reader=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{if($reader.Length -le 0 -or $reader.Length -gt 65536){throw 'Phase plan64KiB bound'};$raw=[byte[]]::new([int]$reader.Length);$reader.ReadExactly($raw)}finally{$reader.Dispose()}
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw));$p=[Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json
if($p.kind -cnotin @('issue83-storage-capacity-phases-v1','issue83-storage-input-metadata-reuse-v1') -or $p.seconds -ne 60 -or $p.callbacks -ne 16){throw 'Wrong component scope'}
foreach($f in $p.files.PSObject.Properties){if((Get-FileHash -LiteralPath $f.Name).Hash -cne $f.Value){throw 'Phase input changed'}}
foreach($role in @($PSCommandPath,$p.runtime,$p.helper)){if($p.files.PSObject.Properties.Name -notcontains $role){throw 'Actual phase runtime/helper/controller must be bound'}}
if((Test-Path -LiteralPath $p.root) -or [DateTime]::UtcNow.AddSeconds(80) -ge ([DateTimeOffset]$p.expiresUtc).UtcDateTime){throw 'Fresh root/full80s remaining window required'}
if($Plan){[ordered]@{planSha256=$hash;effects=$p.effects;resources=$p.resources;expiresUtc=$p.expiresUtc}|ConvertTo-Json -Depth 5;return}
if($hash -cne $ExpectedPlanSha256){throw 'Exact reviewed phase admission required'}
$directory=Split-Path -Parent $path;$stdout=Join-Path $directory stdout.txt;$stderr=Join-Path $directory stderr.txt;$receipt=Join-Path $directory controller.json
foreach($f in @($stdout,$stderr,$receipt)){if(Test-Path $f){throw 'Preserve earlier component evidence'}}
$clock=[Diagnostics.Stopwatch]::StartNew();$owned=$null;$failure=$null;$code=$null;$peakRam=0L;$peakHandles=0;$outFile=$null;$errFile=$null;$outCopy=$null;$errCopy=$null
function Check-Logs {foreach($f in @($stdout,$stderr)){if(Test-Path $f){$h=[IO.FileStream]::new($f,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));try{if($h.Length -gt 1048576){throw 'Phase outer log bound'}}finally{$h.Dispose()}}}}
try{
 $start=[Diagnostics.ProcessStartInfo]::new($p.runtime);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($a in @($p.helper,'--replay-storage-capacity-phases',$path,$hash)){$start.ArgumentList.Add($a)}
 $owned=[Diagnostics.Process]::Start($start);$null=$owned.SafeHandle
 $outFile=[IO.FileStream]::new($stdout,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete),16384,[IO.FileOptions]::Asynchronous)
 $errFile=[IO.FileStream]::new($stderr,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete),16384,[IO.FileOptions]::Asynchronous)
 $outCopy=$owned.StandardOutput.BaseStream.CopyToAsync($outFile);$errCopy=$owned.StandardError.BaseStream.CopyToAsync($errFile)
 while(-not $owned.WaitForExit(100)){$owned.Refresh();$peakRam=[Math]::Max($peakRam,$owned.WorkingSet64);$peakHandles=[Math]::Max($peakHandles,$owned.HandleCount);if($clock.Elapsed.TotalSeconds -ge 75 -or $peakRam -gt 268435456 -or $peakHandles -gt 4096){throw 'Phase outer process/time cap'};Check-Logs}
 $code=$owned.ExitCode;if($code -ne 0){throw "Phase component failed:$code"}
}catch{$failure=$_.ToString()}
finally{
 $cleanup=[Diagnostics.Stopwatch]::StartNew()
 function Remaining {return [Math]::Max(0,5000-[int]$cleanup.ElapsedMilliseconds)}
 try{if($owned -and -not $owned.HasExited){$owned.Kill();if(-not $owned.WaitForExit((Remaining))){throw 'Owned component termination unconfirmed'}}}catch{$failure="$failure Cleanup:$_"}
 foreach($copy in @($outCopy,$errCopy)){if($copy){try{if(-not $copy.Wait((Remaining))){throw 'Phase logs not drained'};$null=$copy.GetAwaiter().GetResult()}catch{$failure="$failure Log drain:$_"}}}
 if($outFile -and $outCopy.IsCompleted){$outFile.Dispose()};if($errFile -and $errCopy.IsCompleted){$errFile.Dispose()}
 try{Check-Logs}catch{$failure="$failure Final logs:$_"}
 [ordered]@{planSha256=$hash;pid=$owned.Id;absent=(-not $owned -or $owned.HasExited);exitCode=$code;seconds=$clock.Elapsed.TotalSeconds;peakRam=$peakRam;peakHandles=$peakHandles;error=$failure;scope='One owned metadata helper; no media/capture child; no intrusive outer namespace walk/CIM'}|ConvertTo-Json|Set-Content $receipt
 if($owned){$owned.Dispose()}
}
if($failure){throw $failure}
