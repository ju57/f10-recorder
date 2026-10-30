param(
    [switch]$Run,
    [Parameter(Mandatory)][string]$PlanFile,
    [Parameter(Mandatory)][string]$ExpectedPlanSha256,
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$ExpectedAssemblySha256,
    [string]$Sdk
)
$ErrorActionPreference = 'Stop'
if ((Get-Item -LiteralPath $PlanFile).Length -gt 65536) { throw 'Rehearsal plan exceeds64KiB.' }
$plan = Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
if (-not $Sdk) {
    $hosts = @($plan.Files.PSObject.Properties | Where-Object { [IO.Path]::GetFileName($_.Name) -eq 'dotnet.exe' })
    if ($hosts.Count -ne 1) { throw 'Plan must bind exactly one dotnet host.' }
    $Sdk = $hosts[0].Name
}
$arguments = @($AssemblyPath, '--replay-calibration-rehearsal', $PlanFile, $ExpectedPlanSha256)
$launch = [pscustomobject]@{
    executable = $Sdk; arguments = $arguments; root = $plan.Root
    effects = @('One bounded nonmedia controller; no nested children', 'Six 930MB dummy sources; eight real-copy snapshots and growing dummy outputs',
        'Native allocation/capacity and active500ms/offline phase checks', 'Snapshot TAR full readback and new-dummy-only verified retirement',
        'Up to24GB final dummy archive coexisting with originals before retirement', '64GB aggregate cap; 62.5GB stop; 60GB caller-free floor',
        '1800second rehearsal and9000second cumulative experiment deadline', 'No engine/GPU/capture/audio/UI/profile/startup/vendor/installation effects')
}
if (-not $Run) { return $launch }
if ((Get-FileHash -LiteralPath $AssemblyPath).Hash -ne $ExpectedAssemblySha256 -or
    (Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256) { throw 'Reviewed rehearsal identity changed.' }
if (-not $plan.Files.PSObject.Properties[[IO.Path]::GetFullPath($Sdk)]) { throw 'Actual SDK is not bound by the plan.' }
foreach ($file in $plan.Files.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath $file.Name).Hash -ne $file.Value) { throw "Reviewed input identity changed before launch: $($file.Name)" }
}
if (Test-Path -LiteralPath $plan.Root) { throw 'Use a fresh rehearsal root; previous evidence stays retained.' }
$directory = Split-Path -Parent (Resolve-Path -LiteralPath $PlanFile)
$nonce = [guid]::NewGuid().ToString('N')
$stdout = Join-Path $directory "rehearsal-$nonce.stdout.txt"
$stderr = Join-Path $directory "rehearsal-$nonce.stderr.txt"
$timer = [Diagnostics.Stopwatch]::StartNew()
$owned = $null
$code = $null
try {
    $quoted = $arguments | ForEach-Object { if ($_ -match '"') { throw 'Quotes unsupported in rehearsal arguments.' }; '"' + $_ + '"' }
    $owned = Start-Process -FilePath $Sdk -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while (-not $owned.WaitForExit(1000)) {
        if ($timer.Elapsed.TotalSeconds -gt (1805 - $plan.RehearsalElapsedSeconds)) { throw 'Outer nonmedia rehearsal deadline exceeded.' }
        if ((Get-Item -LiteralPath $stdout).Length + (Get-Item -LiteralPath $stderr).Length -gt 131072) { throw 'Rehearsal diagnostics exceeded128KiB.' }
    }
    $code = $owned.ExitCode
    Get-Content -LiteralPath $stdout
    Get-Content -LiteralPath $stderr
    if ($code -ne 0) { throw "Nonmedia rehearsal failed with exit $code; retain evidence." }
}
finally {
    if ($owned -and -not $owned.HasExited) { $owned.Kill(); if (-not $owned.WaitForExit(5000)) { throw 'Owned rehearsal controller exit is unconfirmed.' } }
    [pscustomobject]@{launch=$launch;exit=$code;confirmedExited=($owned -and $owned.HasExited);elapsedSeconds=$timer.Elapsed.TotalSeconds;stdout=$stdout;stderr=$stderr} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $directory "rehearsal-$nonce.receipt.json")
    if ($owned) { $owned.Dispose() }
}
