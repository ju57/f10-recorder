param(
    [Parameter(Mandatory)][string]$RetainedPackage,
    [string]$ControllerDirectory=(Join-Path $PSScriptRoot 'confirmation-controller'),
    [switch]$Plan
)
$ErrorActionPreference='Stop'
if($Plan){
    'Read retained metadata. One hidden local cmd.exe exits on stdin to reproduce the Windows handle race. No recorder, media, audio, desktop access or evidence writes.'
    return
}
foreach($module in 'Operations','Arguments','UnheldControls','CreatorProof','Common'){
    . (Join-Path $ControllerDirectory "ProcessController.$module.ps1")
}
function Require($Condition,[string]$Message){if(!$Condition){throw $Message}}
$directory=Join-Path $RetainedPackage 'admissions/SIX'
$binding=Get-Content (Join-Path $directory collector-identity.json) -Raw|ConvertFrom-Json -DateKind String
$planPath=$binding.planPath
$hash=(Get-FileHash $planPath).Hash
$a=Get-Content $planPath -Raw|ConvertFrom-Json -DateKind String
$p=$a.plan
$helper=@($binding.helperPath)
$runtime=@($binding.runtimePath)
$engine=@($p.Files.PSObject.Properties.Name|Where-Object {[IO.Path]::GetFileName($_) -ceq 'ffmpeg.exe'})
$consoleImage=Join-Path ([Environment]::SystemDirectory) conhost.exe
$receiptPath=Join-Path $p.Root 'controls/version.json'
$receipt=Get-Content $receiptPath -Raw|ConvertFrom-Json -DateKind String
$observations=@(Get-Content (Join-Path $directory raw-child-observations.jsonl)|ForEach-Object {$_|ConvertFrom-Json -DateKind String}|
    Where-Object pid -eq $receipt.receipt.ProcessId)
Require ($observations.Count -eq 1 -and $engine.Count -eq 1 -and $hash -ceq $binding.planSha256) 'Unexpected retained observation or binding'
$raw=$observations[0]
$failureText=(Get-Content (Join-Path $directory controller.json) -Raw|ConvertFrom-Json).error

# Exercise the real managed exception without querying an old PID.
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path ([Environment]::SystemDirectory) cmd.exe))
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardInput=$true
$start.RedirectStandardOutput=$true
$start.ArgumentList.Add('/D')
$start.ArgumentList.Add('/Q')
$owner=[Diagnostics.Process]::Start($start)
$observer=$null
try{
    $probePid=$owner.Id
    $observer=[Diagnostics.Process]::GetProcessById($probePid)
    $owner.StandardInput.WriteLine('exit')
    Require ($owner.WaitForExit(5000)) 'Fixture exit deadline'
    $owner.Dispose()
    try{Read-ControllerHandle $observer|Out-Null}catch{$probeFailure=$_.Exception}
}finally{
    if($observer){$observer.Dispose()}
    $owner.Dispose()
}
Require ($probeFailure -and (Test-ExitAtHandleException $probeFailure $probePid)) 'Expected a typed exit while acquiring SafeHandle'
Require (!(Test-ExitAtHandleException $probeFailure ($probePid+1))) 'Wrong exception PID accepted'
$expectedError=$probeFailure.GetBaseException().Message.Replace("($probePid)","($($raw.pid))")
Require ($failureText.Contains($expectedError) -and $failureText.Contains('Read-ControllerHandle')) 'Retained failure differs from reproduced exit'

# Replay the original IDs and files. Only the separate runtime exception has
# the disposable probe's PID. Keep its actual type, throw site and message.
$classify=(Get-Command Test-ExitAtHandleException).ScriptBlock
$readProof=(Get-Command Read-ControllerProofBytes).ScriptBlock
function Test-ExitAtHandleException($Failure,[int]$ChildPid){
    return ($ChildPid -eq $raw.pid -and (& $classify $Failure $probePid))
}
function Get-ControllerFacts($Parent){return $fact}
function Open-ControllerProcess($ChildPid){$counts.opens++; return [pscustomobject]@{Id=$ChildPid}}
function Read-ControllerHandle($Process){
    if($case -eq 'access-denied'){throw [UnauthorizedAccessException]::new('Access denied')}
    if($case -eq 'generic'){throw [InvalidOperationException]::new($expectedError)}
    if($case -eq 'birth-read'){return [pscustomobject]@{IsClosed=$false;IsInvalid=$false}}
    throw $probeFailure
}
function Read-ControllerBirth($Process){throw 'Injected birth read failure'}
function Dispose-ControllerProcess($Process){$counts.disposals++}
function Stop-ControllerProcess($Process){throw 'Unexpected stop'}
function Get-ControllerPolicyMilliseconds {return $counts.clock}
function Read-ControllerProofBytes([string]$Path){
    if($Path -eq $receiptPath){
        if($case -eq 'missing' -or ($case -eq 'delayed-within-bound' -and $counts.clock -eq 0)){return $null}
        if($case -eq 'incomplete'){
            $copy=Get-Content $Path -Raw|ConvertFrom-Json -DateKind String
            $copy.receipt.ProducerAbsent=$false
            return ,([Text.Encoding]::UTF8.GetBytes(($copy|ConvertTo-Json -Depth 8)))
        }
    }
    return ,(& $readProof $Path)
}
$passed=@()
foreach($case in 'retained','delayed-within-bound','wrong-pid','wrong-birth','wrong-argv','wrong-image','wrong-parent','released','access-denied','generic','birth-read','missing','late','incomplete'){
    . (Join-Path $ControllerDirectory 'ProcessController.State.ps1')
    $owned=[pscustomobject]@{Id=$binding.pid;HasExited=$false}
    $birth=([DateTimeOffset]$binding.birth).UtcDateTime
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $rawChildRowLimit=32
    $rawChildRowBytes=4096
    $counts=@{opens=0;disposals=0;clock=0}
    $fact=[pscustomobject]@{ProcessId=$raw.pid;ParentProcessId=$raw.parentPid;CreationDate=$raw.birth;Name=$raw.name;
        CommandLine=$raw.arguments;ExecutablePath=$raw.path;WorkingSetSize=$raw.ram}
    switch($case){
        'wrong-pid'{$fact.ProcessId++}
        'wrong-birth'{$fact.CreationDate=([DateTimeOffset]$raw.birth).AddSeconds(1)}
        'wrong-argv'{$fact.CommandLine+=' -extra'}
        'wrong-image'{$fact.ExecutablePath=$runtime[0]}
        'wrong-parent'{$fact.ParentProcessId++}
        'released'{[void]$released.Add('T01')}
    }
    $reason=$null
    try{
        Observe-Children
        if($case -in 'missing','delayed-within-bound'){Resolve-PendingControlImages; Require ($pendingControlImages.Count -eq 1) 'Missing proof lost'}
        if($case -in 'missing','late'){$counts.clock=1000}
        if($case -eq 'delayed-within-bound'){$counts.clock=999}
        Observe-Children
    }catch{$reason=$_.Exception.Message}
    if($case -in 'retained','delayed-within-bound'){
        Require (!$reason) "Valid $case replay failed: $reason"
        $row=@($children.Values)[0]
        Require ($row.closureEvidence -ceq 'creator-confirmed-exit-before-handle' -and $row.creatorReceiptVerified -and
            $row.creatorProof.sha256 -ceq (Get-FileHash $receiptPath).Hash -and $row.creatorProof.creatorExitCode -eq 0 -and
            $row.heldHandleAcquired -eq $false -and !$row.Contains('exitCode') -and $pendingControlImages.Count -eq 0) 'Incorrect closure provenance'
    }else{
        $expected=switch($case){
            'wrong-birth'{'birth mismatch'} 'wrong-argv'{'command differs'} 'birth-read'{'birth read failure'}
            'missing'{'fixed1second'} 'late'{'fixed1second'} 'incomplete'{'invalid closure'} 'access-denied'{'Access denied'}
            default{'has exited'}
        }
        Require ($reason -and $reason.Contains($expected)) "Unexpected disposition for $case ($reason)"
    }
    Require ($counts.opens -eq 1 -and $counts.disposals -eq 1 -and !$tracked.Count -and !$lookupAbsentChildren.Count) "Unexpected adoption, reopen or disposal count for $case"
    $passed+=$case
}
[ordered]@{result='passed';cases=$passed;receipt=(Get-FileHash $receiptPath).Hash;
    controller=(Get-FileHash (Join-Path $ControllerDirectory 'ProcessController.Common.ps1')).Hash;
    proofVerifier=(Get-FileHash (Join-Path $ControllerDirectory 'ProcessController.UnheldControls.ps1')).Hash;
    probeType=$probeFailure.GetBaseException().GetType().FullName;probeMethod=$probeFailure.GetBaseException().TargetSite.Name;
    recordings=0;exports=0}|ConvertTo-Json
