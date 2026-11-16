param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference = 'Stop'
if ($Plan -eq $Run) { throw 'Choose -Plan or -Run.' }
$admission = Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
if ($Plan) { return $admission | Select-Object kind,effects,bounds,order,decision,claims,claim,contract,timing,exclusions }
if ((Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256) { throw 'Reviewed frozen plan required.' }
if ($admission.kind -notin @('hybrid-physical-precision-v1','indexed-reference-feasibility-v1','mux-stage-counterfactual-v1','intra-structure-counterfactual-v1','live-file-guard-control-v1')) { throw 'Wrong admission.' }
$runner = if ($admission.runner) { [IO.Path]::GetFullPath($admission.runner) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'replay-hybrid-precision.py')) }
$launcher = [IO.Path]::GetFullPath($PSCommandPath)
foreach ($path in @($runner,$launcher)) {
    if ($admission.files.PSObject.Properties.Name -notcontains $path) { throw "Unbound runner: $path" }
}
foreach ($file in $admission.files.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath $file.Name).Hash -ne $file.Value) { throw "Bound input changed: $($file.Name)" }
}
$root = [IO.Path]::GetFullPath($admission.root)
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/issue83')) + [IO.Path]::DirectorySeparatorChar
if (-not $root.StartsWith($workspace,[StringComparison]::OrdinalIgnoreCase)) { throw 'Owned issue83 root required.' }
if (@(Get-ChildItem -LiteralPath $root -Force).Count) { throw 'Preserve existing attempt.' }
$python = $admission.python
if ($admission.files.PSObject.Properties.Name -notcontains $python) { throw 'Unbound Python runtime.' }
$outer = "$PlanFile.controller.json"
if (Test-Path -LiteralPath $outer) { throw 'Preserve prior controller receipt.' }
$argsOwned = @($runner,'--run','--plan-file',[IO.Path]::GetFullPath($PlanFile),'--expected-sha256',$ExpectedPlanSha256)
$quoted = $argsOwned | ForEach-Object { if ($_.Contains('"')) { throw 'Quotes unsupported.' }; '"'+$_+'"' }
$clock = [Diagnostics.Stopwatch]::StartNew(); $owned = $null; $failure = $null; $exitCode = $null
try {
    $owned = Start-Process -FilePath $python -ArgumentList $quoted -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$PlanFile.stdout.txt" -RedirectStandardError "$PlanFile.stderr.txt"
    while (-not $owned.HasExited) {
        $owned.Refresh()
        if ($clock.Elapsed.TotalSeconds -gt $admission.bounds.outer_seconds -or $owned.WorkingSet64 -gt $admission.bounds.outer_memory_bytes -or $owned.Threads.Count -gt $admission.bounds.outer_sampled_threads) { throw 'Outer process/time/memory/thread bound.' }
        foreach ($log in @("$PlanFile.stdout.txt","$PlanFile.stderr.txt")) {
            $logReader = [IO.File]::Open($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,
                [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            try {
                if ([IO.RandomAccess]::GetLength($logReader.SafeFileHandle) -gt $admission.bounds.outer_log_bytes) {
                    throw 'Outer diagnostic bound.'
                }
            } finally { $logReader.Dispose() }
        }
        Start-Sleep -Milliseconds 100
    }
    $owned.WaitForExit(); $exitCode = $owned.ExitCode
    if ($exitCode -ne 0) { throw "Generated correctness pilot failed with exit $exitCode; retain attempt." }
} catch { $failure = $_.Exception.Message; throw }
finally {
    if ($owned -and -not $owned.HasExited) {
        $owned.Kill($true)
        if (-not $owned.WaitForExit(5000)) { $failure = 'Owned process tree did not confirm exit in cleanup bound.' }
    }
    [ordered]@{pid=if($owned){$owned.Id}else{$null};exit=$exitCode;failure=$failure;seconds=$clock.Elapsed.TotalSeconds;
        stopped=(-not $owned -or $owned.HasExited);planSha256=$ExpectedPlanSha256;utc=[datetime]::UtcNow} |
        ConvertTo-Json | Set-Content -LiteralPath $outer
}
