param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference = 'Stop'
if ($Plan -eq $Run) { throw 'Choose -Plan or -Run.' }
$admission = Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
if ($Plan) { return $admission | Select-Object kind,effects,bounds,sources,expected,provenance }
if ($admission.kind -ne 'transport-index-file-only-v1' -or (Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256) { throw 'Frozen reviewed file-only plan required.' }
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/issue83')) + [IO.Path]::DirectorySeparatorChar
$root = [IO.Path]::GetFullPath($admission.output)
if (-not $root.StartsWith($workspace,[StringComparison]::OrdinalIgnoreCase)) { throw 'Owned issue83 output required.' }
if (Test-Path -LiteralPath $root) { throw 'Preserve previous attempt.' }
$wrapper = [IO.Path]::GetFullPath($PSCommandPath)
if ($admission.files.PSObject.Properties.Name -notcontains $wrapper) { throw 'Unbound wrapper.' }
foreach ($binding in $admission.files.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath $binding.Name).Hash -ne $binding.Value) { throw "Changed bound input: $($binding.Name)" }
}
$dotnet = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.tools/dotnet/dotnet.exe'))
$assembly = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../projects/F10Recorder.CoreChecks/bin/Release/net10.0/F10Recorder.CoreChecks.dll'))
foreach ($binary in @($dotnet,$assembly)) {
    if ($admission.files.PSObject.Properties.Name -notcontains $binary) { throw 'Unbound file-only runtime.' }
}
foreach ($suffix in @('.stdout.txt','.stderr.txt','.controller.json')) {
    if (Test-Path -LiteralPath "$PlanFile$suffix") { throw 'Preserve prior controller output.' }
}
$argumentsOwned = @($assembly,'--transport-fixtures',[IO.Path]::GetFullPath($PlanFile),$ExpectedPlanSha256)
$quoted = $argumentsOwned | ForEach-Object { if ($_.Contains('"')) { throw 'Quotes unsupported.' }; '"'+$_+'"' }
$watch = [Diagnostics.Stopwatch]::StartNew(); $child = $null; $failure = $null; $exitCode = $null
try {
    $child = Start-Process -FilePath $dotnet -ArgumentList $quoted -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$PlanFile.stdout.txt" -RedirectStandardError "$PlanFile.stderr.txt"
    while (-not $child.HasExited) {
        $child.Refresh()
        if ($watch.Elapsed.TotalSeconds -gt $admission.bounds.outer_seconds -or $child.WorkingSet64 -gt $admission.bounds.child_memory_bytes) { throw 'File-only outer time/memory bound.' }
        foreach ($log in @("$PlanFile.stdout.txt","$PlanFile.stderr.txt")) {
            $reader = [IO.File]::Open($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            try { if ([IO.RandomAccess]::GetLength($reader.SafeFileHandle) -gt $admission.bounds.report_bytes) { throw 'File-only diagnostic bound.' } }
            finally { $reader.Dispose() }
        }
        Start-Sleep -Milliseconds 100
    }
    $child.WaitForExit(); $exitCode = $child.ExitCode
    if ($exitCode -ne 0) { throw "File-only source check failed: $exitCode. Preserve receipt." }
} catch { $failure = $_.Exception.Message; throw }
finally {
    if ($child -and -not $child.HasExited) {
        $child.Kill($true)
        if (-not $child.WaitForExit(5000)) { $failure = 'Owned file-only process cleanup unconfirmed.' }
    }
    [ordered]@{pid=if($child){$child.Id}else{$null};exit=$exitCode;failure=$failure;seconds=$watch.Elapsed.TotalSeconds;
        stopped=(-not $child -or $child.HasExited);planSha256=$ExpectedPlanSha256;utc=[datetime]::UtcNow} |
        ConvertTo-Json | Set-Content -LiteralPath "$PlanFile.controller.json"
}
