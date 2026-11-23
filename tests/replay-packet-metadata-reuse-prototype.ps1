param([Parameter(Mandatory)][string]$PlanFile,[string]$ExpectedPlanSha256,[switch]$Plan,[switch]$Run)
$ErrorActionPreference='Stop'
if($Plan -eq $Run){throw 'Choose exactly -Plan or -Run.'}
$planPath=[IO.Path]::GetFullPath($PlanFile)
$admission=Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
if($admission.kind -notin @('packet-metadata-reuse-file-only-v1','packet-metadata-hardlink-setup-v1')){throw 'Wrong file-only route.'}
if($Plan){return $admission | Select-Object kind,effects,bounds,sources,sequence,acceptance,output,provenance}
if((Get-FileHash -LiteralPath $planPath).Hash -ne $ExpectedPlanSha256){throw 'Exact frozen reviewed plan required.'}
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/issue83-metadata-reuse-prototype-20261112'))+[IO.Path]::DirectorySeparatorChar
$output=[IO.Path]::GetFullPath($admission.output)
if(-not $output.StartsWith($workspace,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Fresh owned output required.'}
foreach($f in $admission.files.PSObject.Properties){if((Get-FileHash -LiteralPath $f.Name).Hash -ne $f.Value){throw "Changed bound input: $($f.Name)"}}
foreach($suffix in @('.stdout.txt','.stderr.txt','.controller.json')){if(Test-Path -LiteralPath "$planPath$suffix"){throw 'Preserve previous attempt.'}}
if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($output)).AvailableFreeSpace -lt $admission.bounds.minimum_free_bytes){throw 'Disk floor.'}
$clock=[Diagnostics.Stopwatch]::StartNew();$child=$null;$failure=$null;$exitCode=$null;$forced=$false
$peakWorking=0L;$peakPrivate=0L;$peakHandles=0;$samples=[Collections.Generic.List[object]]::new()
try{
    $ownedArguments=@($admission.helper,$planPath,$ExpectedPlanSha256)
    if($admission.kind -eq 'packet-metadata-hardlink-setup-v1'){$ownedArguments+='--setup-only'}
    $arguments=$ownedArguments|ForEach-Object {if($_.Contains('"')){throw 'Quoted paths unsupported.'};'"'+$_+'"'}
    $child=Start-Process -FilePath $admission.runtime -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput "$planPath.stdout.txt" -RedirectStandardError "$planPath.stderr.txt"
    while(-not $child.HasExited){
        $child.Refresh();$peakWorking=[Math]::Max($peakWorking,$child.WorkingSet64);$peakPrivate=[Math]::Max($peakPrivate,$child.PrivateMemorySize64);$peakHandles=[Math]::Max($peakHandles,$child.HandleCount)
        $samples.Add([ordered]@{qpc=[Diagnostics.Stopwatch]::GetTimestamp();working=$child.WorkingSet64;private=$child.PrivateMemorySize64;handles=$child.HandleCount})
        if($clock.Elapsed.TotalSeconds -gt $admission.bounds.outer_seconds -or $child.WorkingSet64 -gt $admission.bounds.child_memory_bytes -or $child.HandleCount -gt 4096){throw 'Owned helper resource bound.'}
        $bytes=0L
        if(Test-Path -LiteralPath $output){Get-ChildItem -LiteralPath $output -File -Recurse|ForEach-Object {$bytes+=$_.Length}}
        foreach($log in @("$planPath.stdout.txt","$planPath.stderr.txt")){if(Test-Path -LiteralPath $log){$bytes+=(Get-Item -LiteralPath $log).Length}}
        if($bytes -gt $admission.bounds.report_bytes -or [IO.DriveInfo]::new([IO.Path]::GetPathRoot($output)).AvailableFreeSpace -lt $admission.bounds.minimum_free_bytes){throw 'Diagnostic storage bound.'}
        Start-Sleep -Milliseconds 50
    }
    $child.WaitForExit();$exitCode=$child.ExitCode
    if($exitCode -ne 0){throw "Prototype failed: $exitCode. Preserve evidence; no automatic repeat."}
}catch{$failure=$_.Exception.Message;throw}
finally{
    if($child -and -not $child.HasExited){$forced=$true;$child.Kill($true);if(-not $child.WaitForExit(5000)){$failure='Owned helper closure unconfirmed.'}}
    [ordered]@{planSha256=$ExpectedPlanSha256;pid=if($child){$child.Id}else{$null};exit=$exitCode;failure=$failure;forced=$forced;stopped=(-not $child -or $child.HasExited);
        seconds=$clock.Elapsed.TotalSeconds;peakWorking=$peakWorking;peakPrivate=$peakPrivate;peakHandles=$peakHandles;samples=$samples;utc=[datetime]::UtcNow}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath "$planPath.controller.json"
}
