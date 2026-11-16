param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference = 'Stop'
if ($Plan -eq $Run) { throw 'Choose -Plan or -Run.' }
$admission = Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
if ($admission.Kind -ne 'issue83-audio-shard-feasibility-v1') { throw 'Wrong audio pilot.' }
if ($Plan) { return $admission | Select-Object Kind,Root,Bounds,Cases,RetainedBytes,Effects,Claim,ExpiresUtc }
if ((Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256) { throw 'Frozen reviewed plan required.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/issue83')) + '\'
if (-not $admission.Root.StartsWith($taskRoot,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $admission.Root)) { throw 'Fresh owned audio root required.' }
$wrapper = [IO.Path]::GetFullPath($PSCommandPath)
if ($admission.Files.PSObject.Properties.Name -notcontains $wrapper) { throw 'Unbound controller.' }
foreach ($binding in $admission.Files.PSObject.Properties) { if ((Get-FileHash -LiteralPath $binding.Name).Hash -ne $binding.Value) { throw "Changed binding: $($binding.Name)" } }
$sdk = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.tools/dotnet/dotnet.exe'))
$assembly = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../projects/F10Recorder.WorkerChecks/bin/Release/net10.0-windows/F10Recorder.WorkerChecks.dll'))
foreach ($role in @($sdk,$assembly)) { if ($admission.Files.PSObject.Properties.Name -notcontains $role) { throw 'Unbound actual runtime.' } }
if ([datetime]::UtcNow.AddSeconds(160) -ge ([datetime]$admission.ExpiresUtc).ToUniversalTime()) { throw 'Complete audio admission window expired.' }
foreach ($suffix in @('.stdout.txt','.stderr.txt','.controller.json')) { if (Test-Path -LiteralPath "$PlanFile$suffix") { throw 'Preserve prior attempt.' } }
$ownedArgs = @($assembly,'--replay-audio-shards-run',[IO.Path]::GetFullPath($PlanFile),$ExpectedPlanSha256)
$quoted = $ownedArgs | ForEach-Object { if ($_.Contains('"')) { throw 'Quotes unsupported.' }; '"'+$_+'"' }
$watch = [Diagnostics.Stopwatch]::StartNew(); $child = $null; $failure = $null; $exitCode = $null
try {
    $child = Start-Process -FilePath $sdk -ArgumentList $quoted -WorkingDirectory (Split-Path $PSScriptRoot -Parent) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$PlanFile.stdout.txt" -RedirectStandardError "$PlanFile.stderr.txt"
    while (-not $child.HasExited) {
        $child.Refresh()
        if ($watch.Elapsed.TotalSeconds -gt 160 -or $child.WorkingSet64 -gt 536870912 -or $child.HandleCount -gt 1536) { throw 'Audio outer process/time/resource bound.' }
        foreach ($log in @("$PlanFile.stdout.txt","$PlanFile.stderr.txt")) {
            $reader = [IO.File]::Open($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            try { if ([IO.RandomAccess]::GetLength($reader.SafeFileHandle) -gt 2097152) { throw 'Audio outer diagnostic bound.' } } finally { $reader.Dispose() }
        }
        Start-Sleep -Milliseconds 100
    }
    $child.WaitForExit(); $exitCode = $child.ExitCode
    if ($exitCode -ne 0) { throw "Audio pilot failed: $exitCode. Preserve attempt." }
} catch { $failure = $_.Exception.Message; throw }
finally {
    if ($child -and -not $child.HasExited) { $child.Kill($true); if (-not $child.WaitForExit(5000)) { $failure = 'Audio pilot cleanup unconfirmed.' } }
    [ordered]@{pid=if($child){$child.Id}else{$null};exit=$exitCode;failure=$failure;seconds=$watch.Elapsed.TotalSeconds;
        stopped=(-not $child -or $child.HasExited);planSha256=$ExpectedPlanSha256;utc=[datetime]::UtcNow} | ConvertTo-Json | Set-Content -LiteralPath "$PlanFile.controller.json"
}
