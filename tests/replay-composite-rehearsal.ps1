param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference='Stop'
$clock=[Diagnostics.Stopwatch]::StartNew()
if ($Plan -eq $Run) { throw 'Choose -Plan or -Run.' }
$raw=[IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $PlanFile))
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))
$admission=[Text.Encoding]::UTF8.GetString($raw) | ConvertFrom-Json
$historyOnly=$admission.kind -eq 'issue83-composite-history-probe-v1'
if ($admission.kind -notin @('issue83-composite-storage-rehearsal-v1','issue83-composite-history-probe-v1') -or $raw.Length -gt 65536) { throw 'Wrong rehearsal scope.' }
if ($Run -and (-not $ExpectedPlanSha256 -or $hash -ne $ExpectedPlanSha256)) { throw 'Reviewed raw plan required.' }
$directory=Split-Path -Parent (Resolve-Path -LiteralPath $PlanFile)
$inner=Join-Path $directory 'calibration-plan.json'
if ((Get-FileHash -LiteralPath $inner).Hash -ne $admission.innerPlanSha256) { throw 'Bound inner plan changed.' }
$innerDocument=Get-Content -LiteralPath $inner -Raw | ConvertFrom-Json
$innerPlan=$innerDocument.plan
if ($innerPlan.Root -ne $admission.plan.Root) { throw 'Inner/outer root mismatch.' }
if (-not $historyOnly -and ($admission.outputReadOwnership -cne 'continuous-qualification-shape-v1' -or
    $innerDocument.outputReadOwnership -cne $admission.outputReadOwnership)) {
  throw 'Qualification-shaped output read ownership must be bound in both inner and outer rehearsal plans.'
}
$publicationPath=$admission.PSObject.Properties['publicationPath']
if ($publicationPath -and ($historyOnly -or $publicationPath.Value -cne 'retain-files-validated-owner-v1' -or
    $innerDocument.publicationPath -cne $publicationPath.Value)) {
  throw 'Explicit publication route must match in both rehearsal plans.'
}
if ($innerDocument.PSObject.Properties['publicationPath'] -and -not $publicationPath) { throw 'Inner publication route must be declared by the outer admission.' }
$outerSeconds=if($historyOnly){30}else{210}
$internalSeconds=if($historyOnly){25}else{180}
$forwardBytes=if($historyOnly){2000000}else{3200000000}
if ($admission.outerSeconds -ne $outerSeconds -or $admission.internalSeconds -ne $internalSeconds -or $admission.mediaChildren -ne 0 -or $admission.forwardBytes -ne $forwardBytes) { throw 'Finite limits changed.' }
if ([datetime]::UtcNow.AddSeconds($outerSeconds) -ge ([DateTimeOffset]$admission.expiresUtc).UtcDateTime) { throw 'Complete run no longer fits admission.' }
if (Test-Path -LiteralPath $admission.plan.Root) { throw 'Fresh rehearsal root required; never retry in place.' }
foreach ($file in $innerPlan.Files.PSObject.Properties) {
  if ((Get-FileHash -LiteralPath $file.Name).Hash -ne $file.Value) { throw "Changed binding: $($file.Name)" }
}
$resourceFiles=@($innerPlan.Files.PSObject.Properties.Name | Where-Object { [IO.Path]::GetFileName($_) -ieq 'resources.json' })
if ($resourceFiles.Count -gt 1) { throw 'Ambiguous rehearsal resource role.' }
$parentHandleBudget=32768
if ($resourceFiles.Count -eq 1) {
  if ((Get-Item -LiteralPath $resourceFiles[0]).Length -gt 65536) { throw 'Resource64KiB bound.' }
  $resources=Get-Content -LiteralPath $resourceFiles[0] -Raw | ConvertFrom-Json
  if ($resources.PSObject.Properties['parentHandles']) {
    if ($resources.parentHandles -isnot [int] -and $resources.parentHandles -isnot [long]) { throw 'Integer rehearsal handle budget required.' }
    $parentHandleBudget=[int]$resources.parentHandles
  }
}
if ($parentHandleBudget -notin @(32768,49152)) { throw 'Unknown rehearsal handle budget.' }
foreach ($role in @($admission.runtime,$admission.helper,$PSCommandPath)) {
  if ($admission.plan.Files.PSObject.Properties.Name -notcontains [IO.Path]::GetFullPath($role)) { throw 'Actual runtime/helper/controller unbound.' }
}
if ($Plan) { [pscustomobject]@{planSha256=$hash;effects=if($historyOnly){'Zero media/capture/UI/audio/devices/children; read-only retained-history leases and tiny own reports only; no model/catalog'}else{'Zero media/capture/UI/audio/devices; one bounded helper writing non-playable full-size dummy sources and paired outputs; native storage accounting; no model/catalog'};admission=$admission} | ConvertTo-Json -Depth 8; return }
$stdout=Join-Path $directory 'stdout.txt'; $stderr=Join-Path $directory 'stderr.txt'
foreach ($file in @($stdout,$stderr,(Join-Path $directory 'controller.json'))) { if (Test-Path -LiteralPath $file) { throw 'Existing run evidence preserved.' } }
function Test-OwnedLogs {
  foreach ($log in @($stdout,$stderr)) {
    if (-not (Test-Path -LiteralPath $log)) { continue }
    $handle=[IO.FileStream]::new($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { if ($handle.Length -gt 2097152) { throw 'Rehearsal outer log cap.' } } finally { $handle.Dispose() }
  }
}
$route=if($historyOnly){'--replay-composite-history-probe'}else{'--replay-composite-storage-rehearsal'}
$argsOwned=@($admission.helper,$route,$inner,$admission.innerPlanSha256)
$quoted=$argsOwned | ForEach-Object { if ($_.Contains('"')) { throw 'Quotes unsupported.' }; '"'+$_+'"' }
$owned=$null; $failure=$null; $exitCode=$null
try {
  if ($clock.Elapsed.TotalSeconds -ge ($outerSeconds - 5)) { throw 'Rehearsal binding validation exhausted the whole-run budget.' }
  $owned=Start-Process -FilePath $admission.runtime -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
  while (-not $owned.WaitForExit(100)) {
    $owned.Refresh()
    if ($clock.Elapsed.TotalSeconds -ge ($outerSeconds - 5) -or $owned.WorkingSet64 -gt 805306368 -or $owned.HandleCount -gt $parentHandleBudget) { throw 'Rehearsal outer time/process limit; five-second cleanup reserve.' }
    Test-OwnedLogs
  }
  $exitCode=$owned.ExitCode
  if ($exitCode -ne 0) { throw "Rehearsal failed: $exitCode. Preserve root." }
} catch { $failure=$_.ToString() }
finally {
  try { if ($owned -and -not $owned.HasExited) { $owned.Kill($true); if (-not $owned.WaitForExit(5000)) { $failure="$failure Owned termination unconfirmed." } } }
  catch { $failure="$failure Cleanup: $_" }
  try { Test-OwnedLogs } catch { $failure="$failure Final logs: $_" }
  if ($clock.Elapsed.TotalSeconds -ge $outerSeconds) { $failure="$failure Rehearsal whole-run deadline including validation/cleanup." }
  [pscustomobject]@{planSha256=$hash;pid=$owned.Id;absent=(-not $owned -or $owned.HasExited);exitCode=$exitCode;seconds=$clock.Elapsed.TotalSeconds;parentHandleBudget=$parentHandleBudget;error=$failure} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'controller.json')
  if ($clock.Elapsed.TotalSeconds -ge $outerSeconds) {
    $failure="$failure Rehearsal post-receipt whole-run deadline."
    $receipt=Get-Content -LiteralPath (Join-Path $directory 'controller.json') -Raw | ConvertFrom-Json
    $receipt.error=$failure; $receipt.seconds=$clock.Elapsed.TotalSeconds
    $receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'controller.json')
  }
  if ($owned) { $owned.Dispose() }
}
if ($failure) { throw $failure }
Get-Content -LiteralPath $stdout
