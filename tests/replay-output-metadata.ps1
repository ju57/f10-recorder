param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$PlanFile,
    [Parameter(Mandatory)][string]$DummySource,
    [string]$ExpectedPlanSha256,
    [switch]$Plan, [switch]$Prepare, [switch]$Run
)
$ErrorActionPreference = 'Stop'
if (@(@($Plan,$Prepare,$Run) | Where-Object { $_ }).Count -ne 1) { throw 'Choose exactly one mode.' }
$repo = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
$assembly = [IO.Path]::GetFullPath($AssemblyPath)
$root = [IO.Path]::GetFullPath($OutputRoot)
$planPath = [IO.Path]::GetFullPath($PlanFile)
$source = [IO.Path]::GetFullPath($DummySource)
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($root,$planPath,$source)) { if (-not $path.StartsWith($artifacts,[StringComparison]::OrdinalIgnoreCase)) { throw 'Probe stays under ignored artifacts.' } }
if ($planPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Plan must be outside fresh output.' }
$effects = 'One180s helper; two800MB nonmedia outputs with850MB preallocation; full310/301 namespace via read-only dummy hardlinks; real storage callbacks and native metadata;20GB retained-family cap,60GB caller floor. No engine, children, devices, GPU, capture, window or preferences/startup/model activation.'
if ($Plan) { return [pscustomobject]@{executable=$sdk;assembly=$assembly;root=$root;effects=$effects;maximumChildren=1;deadlineSeconds=180;budgetBytes=20000000000;floorBytes=60000000000} }
if ($Prepare) {
    if ((Test-Path -LiteralPath $planPath) -or (Test-Path -LiteralPath $root)) { throw 'Fresh plan and output required.' }
    $files = [ordered]@{}
    foreach ($file in @($sdk,$assembly,(Join-Path (Split-Path $assembly) 'F10Recorder.Windows.dll'),(Join-Path (Split-Path $assembly) 'F10Recorder.Core.dll'),(Join-Path (Split-Path $assembly) 'F10Recorder.WorkerChecks.deps.json'),(Join-Path (Split-Path $assembly) 'F10Recorder.WorkerChecks.runtimeconfig.json'),$PSCommandPath)) { $files[[IO.Path]::GetFullPath($file)] = (Get-FileHash -LiteralPath $file).Hash }
    $fingerprint = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $names = @(0..309 | ForEach-Object { 'chunk-{0:D9}.ts' -f $_ }) + @('segments.csv')
        foreach ($name in $names) { $fingerprint.AppendData([Text.Encoding]::UTF8.GetBytes($name + ':' + (Get-FileHash -LiteralPath (Join-Path $source $name)).Hash + "`n")) }
        $sourceHash = [Convert]::ToHexString($fingerprint.GetHashAndReset())
    } finally { $fingerprint.Dispose() }
    New-Item -ItemType Directory -Path (Split-Path $planPath) -Force | Out-Null
    [ordered]@{kind='issue14-output-metadata-nonmedia-v1';root=$root;dummySource=$source;sourceFingerprint=$sourceHash;files=$files;deadlineSeconds=180;budgetBytes=20000000000;floorBytes=60000000000;effects=$effects} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $planPath
    return [pscustomobject]@{plan=$planPath;sha256=(Get-FileHash -LiteralPath $planPath).Hash;effects=$effects}
}
$record = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
if (-not $ExpectedPlanSha256 -or (Get-FileHash -LiteralPath $planPath).Hash -ne $ExpectedPlanSha256) { throw 'Reviewed plan identity changed.' }
if ($record.root -ne $root -or $record.dummySource -ne $source) { throw 'Probe differs from frozen plan.' }
foreach ($file in $record.files.PSObject.Properties) { if ((Get-FileHash -LiteralPath $file.Name).Hash -ne $file.Value) { throw 'Probe input changed.' } }
$stdout = "$planPath.stdout.txt"; $stderr = "$planPath.stderr.txt"; $receipt = "$planPath.controller.json"
if (Test-Path -LiteralPath $receipt) { throw 'Attempt already started; retain it.' }
$arguments = @($assembly,'--replay-calibration-output-metadata',$planPath,(Get-FileHash -LiteralPath $planPath).Hash)
$quoted = $arguments | ForEach-Object { if ($_.Contains('"')) { throw 'Quotes unsupported in probe paths.' }; '"' + $_ + '"' }
$owned = $null; $clock = [Diagnostics.Stopwatch]::StartNew()
try {
    $owned = Start-Process -FilePath $sdk -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while (-not $owned.WaitForExit(100)) {
        if ($clock.Elapsed.TotalSeconds -gt 175) { throw 'Probe deadline;five-second owned cleanup reserve remains.' }
        if ((Get-Item -LiteralPath $stdout).Length + (Get-Item -LiteralPath $stderr).Length -gt 131072) { throw 'Probe diagnostic cap.' }
    }
    $code = $owned.ExitCode
    [pscustomobject]@{pid=$owned.Id;exit=$code;confirmedExited=$true;elapsedSeconds=$clock.Elapsed.TotalSeconds} | ConvertTo-Json | Set-Content -LiteralPath $receipt
    Get-Content -LiteralPath $stdout; Get-Content -LiteralPath $stderr
    if ($code -ne 0) { throw 'Nonmedia metadata probe failed; retain all evidence.' }
} finally {
    if ($owned -and -not $owned.HasExited) { $owned.Kill($true); if (-not $owned.WaitForExit(5000)) { throw 'Owned probe exit unconfirmed.' } }
    if ($owned) { $owned.Dispose() }
}
