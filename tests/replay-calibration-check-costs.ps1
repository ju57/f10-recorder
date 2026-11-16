param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference='Stop'
if($Plan -eq $Run){throw 'Choose -Plan or -Run.'}
$path=(Resolve-Path -LiteralPath $PlanFile).Path
$raw=[IO.File]::ReadAllBytes($path)
if($raw.Length -gt 65536){throw 'Plan exceeds64KiB.'}
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))
$p=[Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json
if($p.kind -notin @('issue83-calibration-check-cost-v1','issue83-storage-check-cost-v1','issue83-storage-cadence-cost-v1') -or $p.seconds -ne 15 -or $p.groups -ne 4){throw 'Wrong metadata-only scope.'}
if($Plan){$p|ConvertTo-Json -Depth 8;return}
if($hash -cne $ExpectedPlanSha256 -or [DateTime]::UtcNow.AddSeconds(30) -ge ([DateTimeOffset]$p.expiresUtc).UtcDateTime){throw 'Exact reviewed plan and full30seconds required.'}
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))+[IO.Path]::DirectorySeparatorChar
if(-not ([IO.Path]::GetFullPath($p.output)).StartsWith($artifacts,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $p.output)){throw 'Fresh isolated output required.'}
$runtime=@($p.files.PSObject.Properties.Name|Where-Object {[IO.Path]::GetFileName($_) -eq 'dotnet.exe'})
$helper=@($p.files.PSObject.Properties.Name|Where-Object {[IO.Path]::GetFileName($_) -eq 'F10Recorder.WorkerChecks.dll'})
if($runtime.Count -ne 1 -or $helper.Count -ne 1 -or $p.files.PSObject.Properties.Name -notcontains $PSCommandPath -or ($p.kind -eq 'issue83-calibration-check-cost-v1' -and $p.files.PSObject.Properties.Name -notcontains $p.modelFile)){throw 'Bound controller/runtime/helper/model roles required.'}
foreach($file in $p.files.PSObject.Properties){if((Get-FileHash -LiteralPath $file.Name).Hash -cne $file.Value){throw "Binding changed: $($file.Name)"}}
$directory=Split-Path -Parent $path
$stdout=Join-Path $directory 'stdout.txt';$stderr=Join-Path $directory 'stderr.txt';$receipt=Join-Path $directory 'controller.json'
foreach($file in @($stdout,$stderr,$receipt)){if(Test-Path -LiteralPath $file){throw 'Preserve previous execution evidence.'}}
$route=if($p.kind -in @('issue83-storage-check-cost-v1','issue83-storage-cadence-cost-v1')){'--replay-storage-check-costs'}else{'--replay-calibration-check-costs'}
$argumentsOwned=@($helper[0],$route,$path,$hash)
$quoted=$argumentsOwned|ForEach-Object {if($_.Contains('"')){throw 'Quotes unsupported.'};'"'+$_+'"'}
$clock=[Diagnostics.Stopwatch]::StartNew();$owned=$null;$failure=$null;$exitCode=$null;$peakRam=0L;$peakHandles=0
try{
    $owned=Start-Process -FilePath $runtime[0] -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while(-not $owned.WaitForExit(100)){
        $owned.Refresh();$peakRam=[Math]::Max($peakRam,$owned.WorkingSet64);$peakHandles=[Math]::Max($peakHandles,$owned.HandleCount)
        if($clock.Elapsed.TotalSeconds -ge 25 -or $peakRam -gt 268435456 -or $peakHandles -gt 4096){throw 'Metadata parent resource/time bound; five-second cleanup reserve.'}
        foreach($log in @($stdout,$stderr)){
            $reader=[IO.FileStream]::new($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            try{if($reader.Length -gt 1048576){throw 'Metadata log bound.'}}finally{$reader.Dispose()}
        }
    }
    $exitCode=$owned.ExitCode;if($exitCode -ne 0){throw "Metadata check failed:$exitCode"}
}catch{$failure=$_.ToString()}
finally{
    try{if($owned -and -not $owned.HasExited){$owned.Kill($true);if(-not $owned.WaitForExit(5000)){$failure="$failure Owned cleanup unconfirmed."}}}catch{$failure="$failure Cleanup:$_"}
    [pscustomobject]@{planSha256=$hash;pid=$owned.Id;absent=(-not $owned -or $owned.HasExited);exitCode=$exitCode;seconds=$clock.Elapsed.TotalSeconds;peakRam=$peakRam;peakHandles=$peakHandles;error=$failure}|ConvertTo-Json|Set-Content -LiteralPath $receipt
    if($owned){$owned.Dispose()}
}
if($failure){throw $failure}
