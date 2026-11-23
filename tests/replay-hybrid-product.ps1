param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference = 'Stop'
if ($Plan -eq $Run) { throw 'Choose -Plan or -Run.' }
$admission = Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
if ($admission.Kind -notin @('issue83-hybrid-product-pilot-v1','issue83-hybrid-file-profile-v1','issue83-hybrid-packet-product-pilot-v1','issue83-hybrid-packet-batch-product-pilot-v1','issue83-hybrid-packet-cancellable-product-pilot-v1')) { throw 'Wrong pilot plan.' }
if ($Plan) { return $admission | Select-Object Kind,Root,Bounds,Cases,RetainedBytes,Effects,Claim,ExpiresUtc }
if ((Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256) { throw 'Frozen reviewed plan required.' }
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/issue83')) + [IO.Path]::DirectorySeparatorChar
if (-not $admission.Root.StartsWith($workspace,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $admission.Root)) { throw 'Fresh owned pilot root required.' }
$wrapper = [IO.Path]::GetFullPath($PSCommandPath)
if ($admission.Files.PSObject.Properties.Name -notcontains $wrapper) { throw 'Unbound controller.' }
foreach ($binding in $admission.Files.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath $binding.Name).Hash -ne $binding.Value) { throw "Bound file changed: $($binding.Name)" }
}
$sdk = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.tools/dotnet/dotnet.exe'))
$assembly = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../projects/F10Recorder.WorkerChecks/bin/Release/net10.0-windows/F10Recorder.WorkerChecks.dll'))
foreach ($runtime in @($sdk,$assembly)) { if ($admission.Files.PSObject.Properties.Name -notcontains $runtime) { throw 'Actual runtime role is unbound.' } }
$fileOnly = $admission.Kind -eq 'issue83-hybrid-file-profile-v1'
$outerSeconds = if($fileOnly){90}else{240}
$memoryBytes = if($fileOnly){268435456}else{805306368}
if ([datetime]::UtcNow.AddSeconds($outerSeconds) -ge ([datetime]$admission.ExpiresUtc).ToUniversalTime()) { throw 'Fresh complete pilot no longer fits its admission window.' }
foreach ($suffix in @('.stdout.txt','.stderr.txt','.controller.json')) { if (Test-Path -LiteralPath "$PlanFile$suffix") { throw 'Preserve prior attempt.' } }
$route = if($fileOnly){'--replay-hybrid-file-profile-run'}else{'--replay-hybrid-product-run'}
$argumentsOwned = @($assembly,$route,[IO.Path]::GetFullPath($PlanFile),$ExpectedPlanSha256)
$quoted = $argumentsOwned | ForEach-Object { if ($_.Contains('"')) { throw 'Quotes unsupported.' }; '"'+$_+'"' }
$watch = [Diagnostics.Stopwatch]::StartNew(); $child = $null; $failure = $null; $exitCode = $null
try {
    $child = Start-Process -FilePath $sdk -ArgumentList $quoted -WorkingDirectory (Split-Path $PSScriptRoot -Parent) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$PlanFile.stdout.txt" -RedirectStandardError "$PlanFile.stderr.txt"
    while (-not $child.HasExited) {
        $child.Refresh()
        if ($watch.Elapsed.TotalSeconds -gt $outerSeconds -or $child.WorkingSet64 -gt $memoryBytes -or $child.HandleCount -gt 1536) { throw 'Pilot outer process/time/memory/handle bound.' }
        foreach ($log in @("$PlanFile.stdout.txt","$PlanFile.stderr.txt")) {
            $reader = [IO.File]::Open($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            try { if ([IO.RandomAccess]::GetLength($reader.SafeFileHandle) -gt 2097152) { throw 'Outer diagnostic bound.' } }
            finally { $reader.Dispose() }
        }
        Start-Sleep -Milliseconds 100
    }
    $child.WaitForExit(); $exitCode = $child.ExitCode
    if ($exitCode -ne 0) { throw "Hybrid product-code pilot failed: $exitCode. Preserve attempt." }
} catch { $failure = $_.Exception.Message; throw }
finally {
    if ($child -and -not $child.HasExited) { $child.Kill($true); if (-not $child.WaitForExit(5000)) { $failure = 'Owned pilot process cleanup unconfirmed.' } }
    [ordered]@{pid=if($child){$child.Id}else{$null};exit=$exitCode;failure=$failure;seconds=$watch.Elapsed.TotalSeconds;
        stopped=(-not $child -or $child.HasExited);planSha256=$ExpectedPlanSha256;utc=[datetime]::UtcNow} |
        ConvertTo-Json | Set-Content -LiteralPath "$PlanFile.controller.json"
}
