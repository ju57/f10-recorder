param(
    [Parameter(Mandatory)][string]$PlanFile,
    [Parameter(Mandatory)][string]$ExpectedPlanSha256,
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$ExpectedAssemblySha256,
    [switch]$Run
)
$ErrorActionPreference = 'Stop'
if ((Get-Item -LiteralPath $PlanFile).Length -gt 65536) { throw 'Software plan exceeds64KiB.' }
$plan = Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
$hosts = @($plan.files.PSObject.Properties | Where-Object {[IO.Path]::GetFileName($_.Name) -eq 'dotnet.exe'})
if ($hosts.Count -ne 1) { throw 'Exactly one SDK host must be bound.' }
$arguments = @($AssemblyPath,'--replay-manifest-software',$PlanFile,$ExpectedPlanSha256)
if (-not $Run) {
    return [pscustomobject]@{executable=$hosts[0].Name;arguments=$arguments;root=$plan.root;
        effects='Two CPU-only synthetic video/tone engine children; no audio playback, desktop/device capture, GPU, UI or profile/startup/vendor changes.60seconds/32MiB; no retry.'}
}
if ((Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256 -or
    (Get-FileHash -LiteralPath $AssemblyPath).Hash -ne $ExpectedAssemblySha256) { throw 'Reviewed software identities changed.' }
foreach($f in $plan.files.PSObject.Properties) { if((Get-FileHash -LiteralPath $f.Name).Hash -ne $f.Value) { throw "Software input changed: $($f.Name)" } }
$nonce = [guid]::NewGuid().ToString('N'); $dir = Split-Path -Parent $PlanFile
$stdout = Join-Path $dir "software-$nonce.stdout.txt"; $stderr = Join-Path $dir "software-$nonce.stderr.txt"
$quoted = $arguments | ForEach-Object { if($_.Contains('"')){throw 'Quoted argument unsupported'}; '"'+$_+'"' }
$clock = [Diagnostics.Stopwatch]::StartNew(); $owned = $null
try {
    $owned = Start-Process -FilePath $hosts[0].Name -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while(-not $owned.WaitForExit(250)) {
        if($clock.Elapsed.TotalSeconds -gt 65 -or (Get-Item $stdout).Length+(Get-Item $stderr).Length -gt 131072){throw 'Software controller deadline/diagnostic cap exceeded.'}
    }
    [ordered]@{pid=$owned.Id;exit=$owned.ExitCode;confirmedExited=$true;elapsedSeconds=$clock.Elapsed.TotalSeconds;stdout=$stdout;stderr=$stderr} |
        ConvertTo-Json | Set-Content (Join-Path $dir "software-$nonce.receipt.json")
    Get-Content $stdout; Get-Content $stderr
    if($owned.ExitCode -ne 0){throw 'Software manifest verification failed; retain evidence.'}
}
finally {
    if($owned -and -not $owned.HasExited){$owned.Kill($true); if(-not $owned.WaitForExit(5000)){Write-Error 'Software controller cleanup unconfirmed'}}
    if($owned){$owned.Dispose()}
}
