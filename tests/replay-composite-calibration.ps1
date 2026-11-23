param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference='Stop'
$clock=[Diagnostics.Stopwatch]::StartNew()
if ($Plan -eq $Run) { throw 'Choose -Plan or -Run.' }
$planPath=(Resolve-Path -LiteralPath $PlanFile).Path
$raw=[IO.File]::ReadAllBytes($planPath)
if ($raw.Length -gt 65536) { throw 'Plan exceeds64KiB.' }
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))
$admission=[Text.Encoding]::UTF8.GetString($raw) | ConvertFrom-Json
$p=$admission.plan
if (-not $p.CompositeQualification -or $p.ReplayExporterRecipe -ne 'replay-avc-aac-owned-completed-history-direct-avcc-with-reserved-staging-exact-fallback-v5' -or $p.DeadlineSeconds -ne 5400) { throw 'Wrong current composite qualification scope; historical plans require their frozen controller.' }
if ($Run -and (-not $ExpectedPlanSha256 -or $hash -ne $ExpectedPlanSha256)) { throw 'Reviewed raw plan required.' }
if (Test-Path -LiteralPath $p.Root) { throw 'Fresh root required; preserve failed acquisitions.' }
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))+[IO.Path]::DirectorySeparatorChar
if (-not $p.Root.StartsWith($workspace,[StringComparison]::OrdinalIgnoreCase)) { throw 'Isolated workspace artifact root required.' }
$runtime=@($p.Files.PSObject.Properties.Name | Where-Object {[IO.Path]::GetFileName($_) -eq 'dotnet.exe'})
$helper=@($p.Files.PSObject.Properties.Name | Where-Object {[IO.Path]::GetFileName($_) -eq 'F10Recorder.WorkerChecks.dll'})
if ($runtime.Count -ne 1 -or $helper.Count -ne 1 -or $p.Files.PSObject.Properties.Name -notcontains $PSCommandPath) { throw 'Actual host/helper/controller roles must be bound.' }
foreach ($binding in $p.Files.PSObject.Properties) { if ((Get-FileHash -LiteralPath $binding.Name).Hash -ne $binding.Value) { throw "Binding changed: $($binding.Name)" } }
$resourceFiles=@($p.Files.PSObject.Properties.Name | Where-Object { [IO.Path]::GetFileName($_) -ieq 'resources.json' })
if ($resourceFiles.Count -gt 1) { throw 'Ambiguous collector resource role.' }
$parentHandleBudget=32768
if ($resourceFiles.Count -eq 1) {
  if ((Get-Item -LiteralPath $resourceFiles[0]).Length -gt 65536) { throw 'Resource64KiB bound.' }
  $resources=Get-Content -LiteralPath $resourceFiles[0] -Raw | ConvertFrom-Json
  if ($resources.PSObject.Properties['parentHandles']) {
    if ($resources.parentHandles -isnot [int] -and $resources.parentHandles -isnot [long]) { throw 'Integer collector handle budget required.' }
    $parentHandleBudget=[int]$resources.parentHandles
  }
}
if ($parentHandleBudget -notin @(32768,49152)) { throw 'Unknown collector handle budget.' }
$deadline=([DateTimeOffset]$admission.expiresUtc).UtcDateTime
$remainingSeconds=5400-[double]$p.RetainedElapsedSeconds
if ($remainingSeconds -le 0 -or [datetime]::UtcNow.AddSeconds($remainingSeconds) -ge $deadline) { throw 'Full remaining native cohort no longer fits admission.' }
if ($Plan) { [pscustomobject]@{planSha256=$hash;effects=$admission.effects;recipe=$admission.recipe;resources=$p;expiresUtc=$deadline} | ConvertTo-Json -Depth 12; return }
$directory=Split-Path -Parent $planPath
$stdout=Join-Path $directory 'stdout.txt'; $stderr=Join-Path $directory 'stderr.txt'; $receipt=Join-Path $directory 'controller.json'
foreach ($file in @($stdout,$stderr,$receipt)) { if (Test-Path -LiteralPath $file) { throw 'Existing run evidence preserved.' } }
$argsOwned=@($helper[0],'--replay-calibration-run',$planPath,$hash)
$quoted=$argsOwned | ForEach-Object { if ($_.Contains('"')) { throw 'Quotes unsupported.' }; '"'+$_+'"' }
$owned=$null; $failure=$null; $exitCode=$null; $peakRam=0L; $peakHandles=0
$childPeaks=@{}; $childQueries=0; $maximumChildQueryMilliseconds=0.0
function Test-OwnedLogs {
  foreach ($log in @($stdout,$stderr)) {
    if (-not (Test-Path -LiteralPath $log)) { continue }
    $handle=[IO.FileStream]::new($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { if ($handle.Length -gt 2097152) { throw 'Outer qualification log cap.' } } finally { $handle.Dispose() }
  }
}
try {
  if ($clock.Elapsed.TotalSeconds -ge ($remainingSeconds - 5)) { throw 'Qualification binding validation exhausted the whole-run budget.' }
  $owned=Start-Process -FilePath $runtime[0] -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
  while (-not $owned.WaitForExit(100)) {
    $owned.Refresh(); $peakRam=[Math]::Max($peakRam,$owned.WorkingSet64); $peakHandles=[Math]::Max($peakHandles,$owned.HandleCount)
    if ($clock.Elapsed.TotalSeconds -ge ($remainingSeconds - 5) -or $peakRam -gt 805306368 -or $peakHandles -gt $parentHandleBudget) { throw 'Outer qualification process/time bound; five-second cleanup reserve.' }
    $queryClock=[Diagnostics.Stopwatch]::StartNew()
    $children=@(Get-CimInstance -ClassName Win32_Process -Filter "ParentProcessId = $($owned.Id)" -Property ProcessId,ParentProcessId,CreationDate,ExecutablePath,WorkingSetSize)
    $childQueries++; $maximumChildQueryMilliseconds=[Math]::Max($maximumChildQueryMilliseconds,$queryClock.Elapsed.TotalMilliseconds)
    if ($children.Count -gt 4) { throw 'Concurrent owned child limit exceeded.' }
    foreach ($child in $children) {
      $key="$($child.ProcessId)/$(([DateTimeOffset]$child.CreationDate).UtcDateTime.Ticks)"
      if ($child.WorkingSetSize -gt 2147483648) { throw 'Sampled owned child working set exceeded2GiB.' }
      if (-not $childPeaks.ContainsKey($key)) { $childPeaks[$key]=[ordered]@{pid=$child.ProcessId;startedUtc=([DateTimeOffset]$child.CreationDate).UtcDateTime;path=$child.ExecutablePath;peakSampledWorkingSet=[long]$child.WorkingSetSize} }
      else { $childPeaks[$key].peakSampledWorkingSet=[Math]::Max($childPeaks[$key].peakSampledWorkingSet,[long]$child.WorkingSetSize) }
    }
    Test-OwnedLogs
  }
  $exitCode=$owned.ExitCode
  if ($exitCode -ne 0) { throw "Qualification failed:$exitCode; no retry/refit in this root." }
} catch { $failure=$_.ToString() }
finally {
  try { if ($owned -and -not $owned.HasExited) { $owned.Kill($true); if (-not $owned.WaitForExit(5000)) { $failure="$failure Owned tree termination unconfirmed." } } }
  catch { $failure="$failure Cleanup: $_" }
  try { Test-OwnedLogs } catch { $failure="$failure Final logs: $_" }
  if ($clock.Elapsed.TotalSeconds -ge $remainingSeconds) { $failure="$failure Qualification whole-run deadline including validation/cleanup." }
  [pscustomobject]@{planSha256=$hash;pid=$owned.Id;absent=(-not $owned -or $owned.HasExited);exitCode=$exitCode;seconds=$clock.Elapsed.TotalSeconds;peakRam=$peakRam;peakHandles=$peakHandles;parentHandleBudget=$parentHandleBudget;childQueries=$childQueries;maximumChildQueryMilliseconds=$maximumChildQueryMilliseconds;children=@($childPeaks.Values);resourceScope='Sampled direct task-owned children; actual media exits/CPU/IO and bounded cumulative admissions are retained by collector';error=$failure} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receipt
  if ($clock.Elapsed.TotalSeconds -ge $remainingSeconds) {
    $failure="$failure Qualification post-receipt whole-run deadline."
    $terminalReceipt=Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    $terminalReceipt.error=$failure; $terminalReceipt.seconds=$clock.Elapsed.TotalSeconds
    $terminalReceipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receipt
  }
  if ($owned) {$owned.Dispose()}
}
if ($failure) { throw $failure }
Get-Content -LiteralPath $stdout
