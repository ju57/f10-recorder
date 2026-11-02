param(
    [switch]$Run,
    [Parameter(Mandatory=$true)][string]$PlanFile,
    [Parameter(Mandatory=$true)][string]$ExpectedPlanSha256,
    [Parameter(Mandatory=$true)][string]$AssemblyPath,
    [Parameter(Mandatory=$true)][string]$ExpectedAssemblySha256,
    [string]$Sdk
)
$ErrorActionPreference = 'Stop'
if ((Get-Item -LiteralPath $PlanFile).Length -gt 65536) { throw 'Calibration plan exceeds64KiB.' }
$plan = Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
if (-not $Sdk) {
    $hosts = @($plan.plan.Files.PSObject.Properties | Where-Object { [IO.Path]::GetFileName($_.Name) -eq 'dotnet.exe' })
    if ($hosts.Count -ne 1) { throw 'Plan must bind exactly one dotnet host.' }
    $Sdk = $hosts[0].Name
}
$arguments = @([IO.Path]::GetFullPath($AssemblyPath), '--replay-calibration-run', [IO.Path]::GetFullPath($PlanFile), $ExpectedPlanSha256)
if (-not $Run) {
    [pscustomobject]@{Executable=$Sdk;Arguments=$arguments;Root=$plan.plan.Root;Effects=@('Primary monitor pixels','Actual WASAPI system audio','Quiet repeating chirp playback','Controlled fullscreen window','NVIDIA NVENC',"$($plan.plan.BudgetBytes) byte operational cap;$($plan.plan.MaximumChildren) cumulative calibration children","$($plan.plan.DeadlineSeconds) second cumulative deadline;separate active/offline phases",'Task-private held-out capture/export claims;no account catalog activation','No installed profile/startup/display/vendor preferences changes')}
    return
}
if ((Get-FileHash -LiteralPath $AssemblyPath).Hash -ne $ExpectedAssemblySha256 -or
    (Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256) { throw 'Reviewed assembly/plan identity changed' }
if (-not $plan.plan.Files.PSObject.Properties[[IO.Path]::GetFullPath($Sdk)]) { throw 'Actual SDK is not bound by the plan.' }
foreach ($file in $plan.plan.Files.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath $file.Name).Hash -ne $file.Value) { throw "Reviewed input identity changed before launch: $($file.Name)" }
}
$directory = Split-Path -Parent (Resolve-Path -LiteralPath $PlanFile)
$nonce = [guid]::NewGuid().ToString('N')
$stdout = Join-Path $directory ("controller-$nonce.stdout.txt")
$stderr = Join-Path $directory ("controller-$nonce.stderr.txt")
$timer = [Diagnostics.Stopwatch]::StartNew()
$absoluteDeadline = if ($plan.plan.ProductionQualification) {
    $start = ([datetime]$plan.nativePhase.startedUtc).ToUniversalTime()
    $deadline = ([datetime]$plan.nativePhase.absoluteDeadlineUtc).ToUniversalTime()
    $reserve = if ($plan.plan.ProductionTrainingRoot) { 2820 } else { 4200 }
    if ($deadline -ne $start.AddSeconds(7200) -or $plan.nativePhase.priorElapsedSeconds -ne 16200 -or $plan.plan.DeadlineSeconds -ne 23400 -or $plan.nativePhase.conservativeRemainingSeconds -ne $reserve -or [datetime]::UtcNow -ge $deadline.AddSeconds(-$reserve)) {
        throw 'Complete fresh production qualification no longer fits; no controller launched'
    }
    $deadline
} elseif ($plan.nativePhase.resumeStartedUtc) {
    $resume = ([datetime]$plan.nativePhase.resumeStartedUtc).ToUniversalTime()
    $deadline = ([datetime]$plan.nativePhase.absoluteDeadlineUtc).ToUniversalTime()
    $freshSeconds = if ($plan.plan.MaximumChildren -eq 151) { 7200 } elseif ($plan.plan.MaximumChildren -eq 147) { 6300 } elseif ($plan.plan.MaximumChildren -in @(137,141,145)) { 5400 } else { 3600 }
    if ($deadline -ne $resume.AddSeconds($freshSeconds) -or $plan.nativePhase.priorElapsedSeconds -ne 10120 -or $plan.plan.DeadlineSeconds -ne (10120+$freshSeconds)) {
        throw 'Resumed phase must preserve prior time, original resume instant and declared absolute deadline'
    }
    $deadline
} elseif ($plan.nativePhase) { ([datetime]$plan.nativePhase.startedUtc).ToUniversalTime().AddSeconds($plan.plan.DeadlineSeconds) } else { [datetime]::UtcNow.AddSeconds($plan.plan.DeadlineSeconds - $plan.plan.RetainedElapsedSeconds) }
if ($plan.nativePhase.resumeStartedUtc) {
    $remaining = if ($plan.plan.MaximumChildren -in @(145,147,151) -and $plan.plan.ExactHeldoutContinuationRoot) { 960 } elseif ($plan.plan.MaximumChildren -eq 141 -and $plan.plan.ExactHeldoutContinuationRoot) { 1200 } elseif ($plan.plan.UserForegroundHandoff -and $plan.plan.MaximumChildren -in @(125,129,137)) { 2340 } else { 2160 }
    if ($plan.nativePhase.conservativeRemainingSeconds -ne $remaining -or [datetime]::UtcNow -ge $absoluteDeadline.AddSeconds(-$remaining)) {
        throw 'Complete resumed native schedule no longer fits; no controller launched'
    }
}
$owned = $null
try {
    $quoted = $arguments | ForEach-Object { if ($_ -match '"') { throw 'Quotes unsupported in reviewed launcher arguments' }; '"' + $_ + '"' }
    $working = if ($plan.plan.ProductionQualification) { $plan.nativePhase.workingRepository } else { (Get-Location).Path }
    $owned = Start-Process -FilePath $Sdk -ArgumentList $quoted -WorkingDirectory $working -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $pidOwned = $owned.Id; $started = $owned.StartTime.ToUniversalTime()
    while (-not $owned.WaitForExit(1000)) {
        if ([datetime]::UtcNow -gt $absoluteDeadline.AddSeconds(-5)) { throw 'Outer calibration deadline reached; five-second owned cleanup reserve remains' }
        $bytes = (Get-Item -LiteralPath $stdout).Length + (Get-Item -LiteralPath $stderr).Length
        if ($bytes -gt 131072) { throw 'Controller diagnostics exceeded128KiB' }
    }
    $code = $owned.ExitCode
    [pscustomobject]@{pid=$pidOwned;startedUtc=$started;exit=$code;confirmedExited=$true;elapsedSeconds=$timer.Elapsed.TotalSeconds;stdout=$stdout;stderr=$stderr;root=$plan.plan.Root} |
        ConvertTo-Json | Set-Content (Join-Path $directory ("controller-$nonce.receipt.json"))
    Get-Content -LiteralPath $stdout
    Get-Content -LiteralPath $stderr
    if ($code -ne 0) { throw "Calibration failed with exit $code; retain all evidence" }
}
finally {
    if ($owned -and -not $owned.HasExited) {
        $owned.Kill($true) # Only this reviewed controller and its task-owned descendants.
        if (-not $owned.WaitForExit(5000)) { Write-Error 'Owned controller cleanup unconfirmed' }
    }
    if ($owned) { $owned.Dispose() }
}
