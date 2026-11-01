param(
    [ValidateSet('short','windows','rates','clock','integration','corrupt','drift')][string]$Mode = 'short',
    [Parameter(Mandatory)][string]$EngineRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$PlanFile,
    [string]$AssemblyPath,
    [switch]$Prepare, [switch]$Run, [switch]$Plan
)
$ErrorActionPreference = 'Stop'
if (@(@($Prepare,$Run,$Plan) | Where-Object { $_ }).Count -ne 1) { throw 'Choose exactly one of -Plan, -Prepare or -Run.' }
$repo = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
$assembly = if($AssemblyPath){[IO.Path]::GetFullPath($AssemblyPath)}else{Join-Path $repo 'bin/issue14/F10Recorder.WorkerChecks.dll'}
$root = [IO.Path]::GetFullPath($OutputRoot)
$planPath = [IO.Path]::GetFullPath($PlanFile)
$engineRootPath = [IO.Path]::GetFullPath($EngineRoot)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $root.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase) -or -not $planPath.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence and plan must stay under repository artifacts.' }
if ($planPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Plan must be outside the fresh evidence root.' }
$launch = [ordered]@{
    executable=$sdk; assembly=$assembly; mode=$Mode; root=$root; engineRoot=$engineRootPath
    effects='Generated picture IDs and synthetic tone only; NVENC generation/export and CPU full decoding. No desktop/device/audio capture or playback, visible UI, shortcuts, worker/profile/startup/vendor changes, installation, download or model activation.'
    maximumChildren=$(if($Mode -eq 'drift'){2}elseif($Mode -in @('integration','corrupt')){3}elseif($Mode -eq 'windows'){19}elseif($Mode -eq 'rates'){117}elseif($Mode -eq 'clock'){26}else{45}); deadlineSeconds=$(if($Mode -in @('integration','corrupt','drift')){45}elseif($Mode -eq 'windows'){1800}else{600})
    budgetBytes=8000000000; accountingRoot=[IO.Path]::GetDirectoryName($root); minimumFreeBytes=20000000000; retention='Charge all retained sibling attempts. Keep failed attempts, source chunks, published clips, pixel oracle and owned child receipts; no automatic retry or deletion.'
}
if ($Plan) { return [pscustomobject]$launch }
if ($Prepare) {
    if (Test-Path -LiteralPath $planPath) { throw 'Plan already exists; retain it and choose a fresh plan.' }
    $files = [ordered]@{}
    foreach($path in @($sdk,$assembly,(Join-Path (Split-Path $assembly) 'F10Recorder.Windows.dll'),(Join-Path (Split-Path $assembly) 'F10Recorder.Core.dll'),(Join-Path (Split-Path $assembly) 'F10Recorder.WorkerChecks.deps.json'),(Join-Path (Split-Path $assembly) 'F10Recorder.WorkerChecks.runtimeconfig.json'),(Join-Path $engineRootPath 'ffmpeg-9.0.2/bin/ffmpeg.exe'),$PSCommandPath,(Join-Path $repo 'projects/F10Recorder.WorkerChecks/ReplayPrecisionChecks.cs'),(Join-Path $repo 'projects/F10Recorder.WorkerChecks/ReplayIntegrationChecks.cs'))) {
        $absolute = [IO.Path]::GetFullPath($path); $files[$absolute]=(Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash
    }
    $record = [ordered]@{kind='issue14-generated-precision-v1';mode=$Mode;root=$root;accountingRoot=$launch.accountingRoot;engineRoot=$engineRootPath;files=$files;
        maximumChildren=$launch.maximumChildren;deadlineSeconds=$launch.deadlineSeconds;budgetBytes=$launch.budgetBytes;minimumFreeBytes=$launch.minimumFreeBytes}
    New-Item -ItemType Directory -Path (Split-Path $planPath) -Force | Out-Null
    $archive = $planPath + '.inputs'; New-Item -ItemType Directory -Path $archive | Out-Null
    foreach($path in $files.Keys){if($path -ne $sdk -and $path -notlike '*ffmpeg.exe'){Copy-Item -LiteralPath $path -Destination $archive}}
    $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $planPath -Encoding utf8
    return [pscustomobject]@{plan=$planPath;sha256=(Get-FileHash -LiteralPath $planPath).Hash;effects=$launch.effects}
}
$record = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
if($record.root -ne $root -or $record.mode -ne $Mode -or $record.engineRoot -ne $engineRootPath){throw 'Launch differs from the frozen plan.'}
foreach($f in $record.files.PSObject.Properties){if((Get-FileHash -LiteralPath $f.Name).Hash -ne $f.Value){throw "Precision identity changed: $($f.Name)"}}
$env:DOTNET_CLI_HOME=Join-Path $repo '.tools/home'
$arguments=@($assembly,'--replay-precision',$planPath,(Get-FileHash -LiteralPath $planPath).Hash)
$quoted=$arguments|ForEach-Object{if($_.Contains('"')){throw 'Quoted paths are unsupported'}; '"'+$_+'"'}
$receiptPath=$planPath+'.controller.json'; if(Test-Path -LiteralPath $receiptPath){throw 'This admitted attempt was already started; use a fresh plan/root.'}
$stdout=$planPath+'.stdout.txt'; $stderr=$planPath+'.stderr.txt'; $owned=$null; $clock=[Diagnostics.Stopwatch]::StartNew(); $stopped=$false
try {
    $owned=Start-Process -FilePath $sdk -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while(-not $owned.WaitForExit(100)){
        if($clock.Elapsed.TotalSeconds -gt ($record.deadlineSeconds-5)){throw 'Precision controller active deadline reached; five-second owned cleanup reserve remains.'}
        if((Get-Item -LiteralPath $stdout).Length+(Get-Item -LiteralPath $stderr).Length -gt 1048576){throw 'Precision controller diagnostic cap reached.'}
    }
    if($owned.ExitCode -ne 0){Get-Content -LiteralPath $stderr; throw 'Precision verification failed; retain this attempt.'}
}
finally {
    if($owned){
        if(-not $owned.HasExited){$stopped=$true; $owned.Kill($true); $null=$owned.WaitForExit(5000)}
        [ordered]@{pid=$owned.Id;confirmedExited=$owned.HasExited;terminatedByController=$stopped;elapsedSeconds=$clock.Elapsed.TotalSeconds;stdout=$stdout;stderr=$stderr}|
            ConvertTo-Json|Set-Content -LiteralPath $receiptPath
        $owned.Dispose()
    }
}
$reportPath=Join-Path $root 'verification.json'
$report=Get-Content -LiteralPath $reportPath -Raw|ConvertFrom-Json
[pscustomobject]@{result=$report.result;mode=$Mode;cases=@($report.results).Count;children=$report.children;elapsedSeconds=$report.elapsedSeconds;report=$reportPath}
