param([switch]$Plan,[Parameter(Mandatory)][string]$Helper,[Parameter(Mandatory)][string]$Root,
 [ValidateSet('--replay-calibration-selected-history-checks','--replay-calibration-frozen-history-checks','--replay-coverage-checks')][string]$Selector='--replay-calibration-selected-history-checks')
$ErrorActionPreference='Stop'
if($Plan){'One pinned helper; 45s work/60s outer including5s cleanup; 512MiB/4096handles; 8MiB fresh private dummy-filesystem evidence; 60GBfloor; no engine, media, capture, audio, UI or devices.';return}
$rootPath=[IO.Path]::GetFullPath($Root);$helperPath=(Resolve-Path -LiteralPath $Helper).Path
$prefix=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/issue83-same-capture-plan-20261114'))+'\'
if(-not$rootPath.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $rootPath)){throw 'Fresh readiness dummy root required'}
$runtime=(Resolve-Path (Join-Path $PSScriptRoot '../.tools/dotnet/dotnet.exe')).Path
$start=[Diagnostics.ProcessStartInfo]::new($runtime);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
foreach($argument in @($helperPath,$Selector,$rootPath)){$start.ArgumentList.Add($argument)}
$watch=[Diagnostics.Stopwatch]::StartNew();$owned=$null;$errorText=$null;$forced=$false;$exitCode=$null;$peak=0L;$handles=0
try{
 if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($rootPath)).AvailableFreeSpace-lt60008388608){throw 'Free-space admission'}
 $owned=[Diagnostics.Process]::Start($start);$null=$owned.SafeHandle;$birth=$owned.StartTime.ToUniversalTime()
 $outTask=$owned.StandardOutput.ReadToEndAsync();$errTask=$owned.StandardError.ReadToEndAsync()
 while(-not$owned.WaitForExit(50)){
  $owned.Refresh();$peak=[Math]::Max($peak,$owned.WorkingSet64);$handles=[Math]::Max($handles,$owned.HandleCount)
  $bytes=0L;if(Test-Path -LiteralPath $rootPath){foreach($f in Get-ChildItem -LiteralPath $rootPath -Recurse -File){$bytes+=$f.Length}}
  if($watch.Elapsed.TotalSeconds-ge55-or$peak-gt536870912-or$handles-gt4096-or$bytes-gt8388608-or[IO.DriveInfo]::new('D').AvailableFreeSpace-lt60000000000){throw 'Dummy fixture sampled process/time/storage bound'}
 }
 $exitCode=$owned.ExitCode;if($exitCode-ne0){throw "Dummy fixture exit:$exitCode"}
 if($Selector-ceq'--replay-calibration-frozen-history-checks'){
  $closedDummy=Join-Path $rootPath 'monitored-past/closed.ts'
  $beforeLength=(Get-Item -LiteralPath $closedDummy).Length
  $writer=[IO.FileStream]::new($closedDummy,'Append','Write',([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete))
  try{$writer.WriteByte(1);$writer.Flush($true)}finally{$writer.Dispose()}
  if((Get-Item -LiteralPath $closedDummy).Length-ne$beforeLength+1){throw 'Post-exit dummy lease-release length check'}
 }
}catch{$errorText=$_.ToString()}
finally{
 if($owned-and-not$owned.HasExited){$forced=$true;$owned.Kill();if(-not$owned.WaitForExit(5000)){$errorText+=' Helper absence unconfirmed'}}
 if($owned){
  if($outTask.Wait(1000)){$outTask.Result|Set-Content -LiteralPath ($rootPath+'.stdout.txt')}else{$errorText+=' stdout not joined'}
  if($errTask.Wait(1000)){$errTask.Result|Set-Content -LiteralPath ($rootPath+'.stderr.txt')}else{$errorText+=' stderr not joined'}
 }
 if($watch.Elapsed.TotalSeconds-ge60){$errorText+=' Whole60s bound'}
 $receipt=[ordered]@{selector=$Selector;helper=$helperPath;helperSha256=(Get-FileHash -LiteralPath $helperPath).Hash;pid=$owned.Id;birth=$birth;
  absent=($owned-and$owned.HasExited);exitCode=$exitCode;forced=$forced;seconds=$watch.Elapsed.TotalSeconds;peakRam=$peak;peakHandles=$handles;error=$errorText}
 if($owned){$owned.Dispose()}
 $receipt|ConvertTo-Json|Set-Content -LiteralPath ($rootPath+'.controller.json')
}
if($errorText){throw $errorText}
Get-Content -LiteralPath ($rootPath+'.stdout.txt')
