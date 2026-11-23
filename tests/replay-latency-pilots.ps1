param([Parameter(Mandatory)][string]$AssemblyPath,[Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference = 'Stop'
if ($Plan -eq $Run) { throw 'Choose -Plan or -Run.' }
if ($Plan) {
    $preview = Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
    if ($preview.kind -ne 'issue83-finite-pilot-v1') { throw 'Wrong pilot plan.' }
    return [pscustomobject]@{Children=$preview.maximumChildren;Seconds=$preview.deadlineSeconds;
        Cap=$preview.cap;Stop=$preview.stop;Floor=$preview.floor;
        Trials=@($preview.trials | Select-Object Label,Kind);Effects=$preview.effects;Claim=$preview.claim}
}
if ((Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256) { throw 'Reviewed plan required.' }
$p = Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
if ($p.files.PSObject.Properties.Name -notcontains [IO.Path]::GetFullPath($AssemblyPath)) { throw 'Supplied helper is not bound by the reviewed plan.' }
foreach ($f in $p.files.PSObject.Properties) { if ((Get-FileHash -LiteralPath $f.Name).Hash -ne $f.Value) { throw "Bound input changed: $($f.Name)" } }
$receipt = "$PlanFile.controller.json"
if (Test-Path -LiteralPath $receipt) { throw 'Existing attempt; preserve it.' }
$sdk = Join-Path (Split-Path $PSScriptRoot) '.tools/dotnet/dotnet.exe'
$ownedArgs = @([IO.Path]::GetFullPath($AssemblyPath),'--replay-latency-pilot-run',[IO.Path]::GetFullPath($PlanFile),$ExpectedPlanSha256)
$quoted = $ownedArgs | ForEach-Object { if ($_.Contains('"')) { throw 'Quotes unsupported.' }; '"'+$_+'"' }
$clock = [Diagnostics.Stopwatch]::StartNew(); $owned=$null; $exit=$null; $failure=$null
@{state='admitted';utc=[datetime]::UtcNow;sha256=$ExpectedPlanSha256} | ConvertTo-Json | Set-Content -LiteralPath $receipt
try {
    $owned = Start-Process -FilePath $sdk -ArgumentList $quoted -WindowStyle Hidden -WorkingDirectory (Split-Path $PSScriptRoot) -PassThru -RedirectStandardOutput "$PlanFile.stdout.txt" -RedirectStandardError "$PlanFile.stderr.txt"
    while (-not $owned.WaitForExit(200)) {
        if ($clock.Elapsed.TotalSeconds -gt 595) { throw 'Whole pilot deadline;5s cleanup remains.' }
        if ((Get-Item -LiteralPath "$PlanFile.stdout.txt").Length+(Get-Item -LiteralPath "$PlanFile.stderr.txt").Length -gt 131072) { throw 'Controller diagnostic cap.' }
    }
    $exit=$owned.ExitCode; if ($exit -ne 0) { throw "Pilot failed $exit; preserve all trials." }
} catch { $failure=$_.Exception.Message; throw }
finally {
    if ($owned -and -not $owned.HasExited) { $owned.Kill($true); if(-not $owned.WaitForExit(5000)){$failure='Owned controller tree exit unconfirmed.'} }
    @{exit=$exit;error=$failure;elapsedSeconds=$clock.Elapsed.TotalSeconds;pid=if($owned){$owned.Id}else{$null};confirmedExited=if($owned){$owned.HasExited}else{$true}} | ConvertTo-Json | Set-Content -LiteralPath $receipt
    if ($owned) { $owned.Dispose() }
}
