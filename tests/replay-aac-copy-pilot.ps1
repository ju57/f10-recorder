param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference = 'Stop'
if ($Plan -eq $Run) { throw 'Choose -Plan or -Run.' }
$admission = Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
if ($admission.Kind -notin @('issue83-aac-copy-short-gate-v1','issue83-direct-packet-mux-short-v1')) { throw 'Wrong short packet/AAC pilot.' }
if ($Plan) { return $admission | Select-Object Kind,Root,Input,RetainedBytes,Bounds,Decision,ExpiresUtc }
if ((Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256) { throw 'Frozen reviewed plan required.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/issue83')) + '\'
if (-not $admission.Root.StartsWith($taskRoot,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $admission.Root)) { throw 'Fresh owned AAC-copy root required.' }
$wrapper = [IO.Path]::GetFullPath($PSCommandPath)
if ($admission.Files.PSObject.Properties.Name -notcontains $wrapper) { throw 'Unbound controller.' }
foreach ($binding in $admission.Files.PSObject.Properties) {
    # Normal state/settings are preservation snapshots, not experiment inputs
    # to pin against legitimate recorder writes.
    if ($admission.Preserved.PSObject.Properties.Name -contains $binding.Name) {
        $snapshot = [IO.File]::Open($binding.Name,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($snapshot)) } finally { $snapshot.Dispose() }
    } else { $hash = (Get-FileHash -LiteralPath $binding.Name).Hash }
    if ($hash -ne $binding.Value) { throw "Changed binding: $($binding.Name)" }
}
$sdk = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.tools/dotnet/dotnet.exe'))
$assembly = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../projects/F10Recorder.WorkerChecks/bin/Release/net10.0-windows/F10Recorder.WorkerChecks.dll'))
foreach ($role in @($sdk,$assembly)) { if ($admission.Files.PSObject.Properties.Name -notcontains $role) { throw 'Unbound actual runtime.' } }
if ([datetime]::UtcNow.AddSeconds(75) -ge ([datetime]$admission.ExpiresUtc).ToUniversalTime()) { throw 'Complete AAC-copy admission window expired.' }
foreach ($suffix in @('.stdout.txt','.stderr.txt','.controller.json')) { if (Test-Path -LiteralPath "$PlanFile$suffix") { throw 'Preserve prior attempt.' } }
$ownedArgs = @($assembly,'--replay-aac-copy-run',[IO.Path]::GetFullPath($PlanFile),$ExpectedPlanSha256)
$quoted = $ownedArgs | ForEach-Object { if ($_.Contains('"')) { throw 'Quotes unsupported.' }; '"'+$_+'"' }
function Read-PilotLogs {
    $bytes = 0L
    foreach ($log in @("$PlanFile.stdout.txt","$PlanFile.stderr.txt")) {
        $reader = [IO.File]::Open($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        try { $length = [IO.RandomAccess]::GetLength($reader.SafeFileHandle); if ($length -gt 2097152) { throw 'AAC-copy outer diagnostic bound.' }; $bytes += $length } finally { $reader.Dispose() }
    }
    return $bytes
}
$watch = [Diagnostics.Stopwatch]::StartNew(); $child = $null; $failure = $null; $exitCode = $null; $finalLogBytes = $null; $peakParent = 0L
try {
    $child = Start-Process -FilePath $sdk -ArgumentList $quoted -WorkingDirectory (Split-Path $PSScriptRoot -Parent) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$PlanFile.stdout.txt" -RedirectStandardError "$PlanFile.stderr.txt"
    while (-not $child.HasExited) {
        $child.Refresh()
        $peakParent = [Math]::Max($peakParent, $child.WorkingSet64)
        if ($watch.Elapsed.TotalSeconds -gt 75 -or $child.WorkingSet64 -gt 268435456 -or $child.HandleCount -gt 1536) { throw 'AAC-copy outer process/time/resource bound.' }
        $null = Read-PilotLogs
        Start-Sleep -Milliseconds 100
    }
    $child.WaitForExit(); $exitCode = $child.ExitCode
    $finalLogBytes = Read-PilotLogs
    if ($exitCode -ne 0) { throw "AAC-copy pilot failed: $exitCode. Preserve attempt." }
} catch { $failure = $_.Exception.Message; throw }
finally {
    if ($child -and -not $child.HasExited) { $child.Kill($true); if (-not $child.WaitForExit(5000)) { $failure = 'AAC-copy pilot cleanup unconfirmed.' } }
    if ($child -and $child.HasExited -and $null -eq $finalLogBytes) { try { $finalLogBytes = Read-PilotLogs } catch { $failure = $_.Exception.Message } }
    [ordered]@{pid=if($child){$child.Id}else{$null};exit=$exitCode;failure=$failure;seconds=$watch.Elapsed.TotalSeconds;
        stopped=(-not $child -or $child.HasExited);finalLogBytes=$finalLogBytes;maximumObservedParentWorkingSet=$peakParent;planSha256=$ExpectedPlanSha256;utc=[datetime]::UtcNow} | ConvertTo-Json | Set-Content -LiteralPath "$PlanFile.controller.json"
}


