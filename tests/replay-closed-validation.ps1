param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$PlanFile,
    [Parameter(Mandatory)][string]$DummySource,
    [Parameter(Mandatory)][string]$MediaFile,
    [Parameter(Mandatory)][string]$EngineRoot,
    [string]$ExpectedPlanSha256,
    [switch]$Plan, [switch]$Prepare, [switch]$Run
)
$ErrorActionPreference = 'Stop'
if (@(@($Plan,$Prepare,$Run) | Where-Object { $_ }).Count -ne 1) { throw 'Choose one mode.' }
$repo = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
$assembly = [IO.Path]::GetFullPath($AssemblyPath); $root = [IO.Path]::GetFullPath($OutputRoot)
$planPath = [IO.Path]::GetFullPath($PlanFile); $source = [IO.Path]::GetFullPath($DummySource)
$media = [IO.Path]::GetFullPath($MediaFile); $engineDirectory = [IO.Path]::GetFullPath($EngineRoot)
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($root,$planPath,$source,$media)) { if (-not $path.StartsWith($artifacts,[StringComparison]::OrdinalIgnoreCase)) { throw 'Diagnostic uses ignored artifacts only.' } }
if ($planPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Plan must be outside fresh output.' }
$effects = 'Onehelper/twoCPU full decoders;210s whole-run/5s cleanup;26GB retained-family cap60GB floor. One750MB owned copy then hardlink of existing closed MP4, real replay storage callbacks,30s warmup per mode. No acquisition, encoder, GPU, UI, devices, startup/preferences/model activation; publication intentionally suppressed and failed live cases stay failed.'
if ($Plan) { return [pscustomobject]@{root=$root;maximumChildren=3;deadlineSeconds=210;budgetBytes=26000000000;floorBytes=60000000000;effects=$effects} }
if ($Prepare) {
    if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $planPath)) { throw 'Fresh plan/output required.' }
    $files = [ordered]@{}
    foreach ($file in @($sdk,$assembly,(Join-Path (Split-Path $assembly) 'F10Recorder.Windows.dll'),(Join-Path (Split-Path $assembly) 'F10Recorder.Core.dll'),(Join-Path (Split-Path $assembly) 'F10Recorder.WorkerChecks.deps.json'),(Join-Path (Split-Path $assembly) 'F10Recorder.WorkerChecks.runtimeconfig.json'),(Join-Path $engineDirectory 'ffmpeg-9.0.2/bin/ffmpeg.exe'),$media,(Join-Path $source 'segments.csv'),$PSCommandPath)) { $files[[IO.Path]::GetFullPath($file)] = (Get-FileHash -LiteralPath $file).Hash }
    New-Item -ItemType Directory -Path (Split-Path $planPath) -Force | Out-Null
    @{kind='issue14-closed-validation-v1';root=$root;dummySource=$source;media=$media;engineRoot=$engineDirectory;files=$files;maximumChildren=3;deadlineSeconds=210;budgetBytes=26000000000;effects=$effects} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $planPath
    return [pscustomobject]@{plan=$planPath;sha256=(Get-FileHash -LiteralPath $planPath).Hash;effects=$effects}
}
if (-not $ExpectedPlanSha256 -or (Get-FileHash -LiteralPath $planPath).Hash -ne $ExpectedPlanSha256) { throw 'Reviewed diagnostic plan changed.' }
$record = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
if ($record.root -ne $root -or $record.media -ne $media -or $record.dummySource -ne $source -or $record.engineRoot -ne $engineDirectory) { throw 'Launch differs from plan.' }
foreach ($file in $record.files.PSObject.Properties) { if ((Get-FileHash -LiteralPath $file.Name).Hash -ne $file.Value) { throw 'Bound diagnostic input changed.' } }
$stdout = "$planPath.stdout.txt"; $stderr = "$planPath.stderr.txt"; $receipt = "$planPath.controller.json"
if (Test-Path -LiteralPath $receipt) { throw 'Attempt already started; retain it.' }
$arguments = @($assembly,'--replay-calibration-closed-validation',$planPath,$ExpectedPlanSha256)
$quoted = $arguments | ForEach-Object { if ($_.Contains('"')) { throw 'Quoted paths unsupported.' }; '"'+$_+'"' }
$owned = $null; $clock = [Diagnostics.Stopwatch]::StartNew()
try {
    $owned = Start-Process -FilePath $sdk -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while (-not $owned.WaitForExit(100)) {
        if ($clock.Elapsed.TotalSeconds -gt 205) { throw 'Diagnostic deadline;five-second cleanup remains.' }
        if ((Get-Item -LiteralPath $stdout).Length + (Get-Item -LiteralPath $stderr).Length -gt 131072) { throw 'Diagnostic log cap.' }
    }
    $code = $owned.ExitCode
    @{pid=$owned.Id;exit=$code;confirmedExited=$true;elapsedSeconds=$clock.Elapsed.TotalSeconds} | ConvertTo-Json | Set-Content -LiteralPath $receipt
    Get-Content -LiteralPath $stdout; Get-Content -LiteralPath $stderr
    if ($code -ne 0) { throw 'Closed-file diagnostic failed; preserve evidence.' }
} finally {
    if ($owned -and -not $owned.HasExited) { $owned.Kill($true); if (-not $owned.WaitForExit(5000)) { throw 'Owned diagnostic cleanup unconfirmed.' } }
    if ($owned) { $owned.Dispose() }
}
