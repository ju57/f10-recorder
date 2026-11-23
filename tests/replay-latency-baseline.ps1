param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$PlanFile,
    [string]$ExpectedPlanSha256,
    [switch]$Plan, [switch]$Run
)
$ErrorActionPreference = 'Stop'
if ($Plan -eq $Run) { throw 'Choose -Plan or -Run.' }
$sdk = Join-Path (Split-Path $PSScriptRoot) '.tools/dotnet/dotnet.exe'
if ($Plan) {
    return [pscustomobject]@{Assembly=$AssemblyPath;Plan=$PlanFile;Children=7;Acquisitions=1;CaptureSeconds=580;WholeSeconds=900;BudgetBytes=12000000000;StopBytes=11000000000;FreeFloorBytes=60000000000;Effects='Owned fullscreen moving fixture/quiet chirp; primary pixels/default system audio; real isolated worker; two full five-minute export/validation saves. No installed/startup/vendor/catalog changes.'}
}
if (-not $ExpectedPlanSha256 -or (Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256) { throw 'Reviewed plan hash required.' }
$p = Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
foreach ($f in $p.files.PSObject.Properties) { if ((Get-FileHash -LiteralPath $f.Name).Hash -ne $f.Value) { throw "Bound input changed: $($f.Name)" } }
if ($p.kind -ne 'issue83-baseline-v1' -or $p.files.PSObject.Properties.Name -notcontains [IO.Path]::GetFullPath($AssemblyPath)) { throw 'Wrong helper/plan.' }
$receipt = "$PlanFile.controller.json"
if (Test-Path -LiteralPath $receipt) { throw 'Attempt already started; preserve receipt.' }
$argsOwned = @([IO.Path]::GetFullPath($AssemblyPath),'--replay-latency-run',[IO.Path]::GetFullPath($PlanFile),$ExpectedPlanSha256)
$quoted = $argsOwned | ForEach-Object { if ($_.Contains('"')) { throw 'Quotes unsupported.' }; '"'+$_+'"' }
$owned = $null; $clock = [Diagnostics.Stopwatch]::StartNew(); $exit = $null; $failure = $null
@{state='admitted';startedUtc=[datetime]::UtcNow;planSha256=$ExpectedPlanSha256} | ConvertTo-Json | Set-Content -LiteralPath $receipt
try {
    $owned = Start-Process -FilePath $sdk -ArgumentList $quoted -WorkingDirectory (Split-Path $PSScriptRoot) -WindowStyle Hidden -PassThru -RedirectStandardOutput "$PlanFile.stdout.txt" -RedirectStandardError "$PlanFile.stderr.txt"
    while (-not $owned.WaitForExit(250)) {
        if ($clock.Elapsed.TotalSeconds -gt 895) { throw 'Outer deadline; five-second cleanup reserve.' }
        if ((Get-Item -LiteralPath "$PlanFile.stdout.txt").Length + (Get-Item -LiteralPath "$PlanFile.stderr.txt").Length -gt 131072) { throw 'Bounded controller logs exceeded.' }
    }
    $exit = $owned.ExitCode
    if ($exit -ne 0) { throw "Baseline failed with exit $exit; retain evidence." }
} catch { $failure = $_.Exception.Message; throw }
finally {
    if ($owned -and -not $owned.HasExited) { $owned.Kill($true); if (-not $owned.WaitForExit(5000)) { $failure = 'Owned process-tree exit unconfirmed.' } }
    @{exit=$exit;error=$failure;elapsedSeconds=$clock.Elapsed.TotalSeconds;pid=if($owned){$owned.Id}else{$null};confirmedExited=if($owned){$owned.HasExited}else{$true}} | ConvertTo-Json | Set-Content -LiteralPath $receipt
    if ($owned) { $owned.Dispose() }
}
