param([string]$OutputRoot)
$ErrorActionPreference='Stop'
$repository=Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot=Join-Path $repository ('artifacts/pilot-storage-guard-'+[Guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$wrapper=Join-Path $PSScriptRoot 'pilot-storage.ps1'
$sentinel=Join-Path $OutputRoot 'F10Recorder.WorkerChecks.dll'
[IO.File]::WriteAllText($sentinel,"not-an-executable`n")
# A held ordinary file is an impossible SDK parent. Even the pre-fix wrapper
# cannot start a host or load either a stale DLL or this sentinel.
$blockerPath=Join-Path $OutputRoot 'not-a-directory.txt'
$blocker=[IO.File]::Open($blockerPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
$blockedSdk=Join-Path $blockerPath 'dotnet.exe'
$checks=@();$failed=0
function Require([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function LaunchFiles { @(Get-ChildItem -LiteralPath (Join-Path $repository 'artifacts') -File -Filter 'pilot-storage-launch-*' | Sort-Object FullName | ForEach-Object FullName) }
function Check([string]$Name,[scriptblock]$Body) {
    try { & $Body; $script:checks+=@{name=$Name;result='passed'} }
    catch { $script:failed++;$script:checks+=@{name=$Name;result='failed';error=$_.Exception.Message} }
}
try {
    Check 'preview-with-absent-build-remains-inert' {
        $before=LaunchFiles
        $absent=Join-Path $OutputRoot 'absent/F10Recorder.WorkerChecks.dll'
        $preview=& $wrapper -Plan -Checks -Sdk $blockedSdk -AssemblyPath $absent -ExpectedAssemblySha256 ('0'*64)
        Require ($preview.Arguments[0] -eq $absent) 'Preview substituted a default assembly.'
        $missing=& $wrapper -Plan -Sdk $blockedSdk
        Require ($missing.MissingExecutionRequirements -contains 'AssemblyPath' -and $missing.MissingExecutionRequirements -contains 'ExpectedAssemblySha256') 'Preview hid missing explicit build identity.'
        Require (-not (Test-Path -LiteralPath (Split-Path -Parent $absent))) 'Preview created the absent target.'
        Require (($before -join "`n") -ceq ((LaunchFiles) -join "`n")) 'Preview created launch artifacts.'
        foreach ($mode in 'ProcessRun','ProcessChecks') {
            $arguments=@{Plan=$true;Sdk=$blockedSdk;AssemblyPath=$absent;ExpectedAssemblySha256=('0'*64)}
            $arguments[$mode]=$true
            $processPreview=& $wrapper @arguments
            Require ($processPreview.Arguments[1] -eq $(if($mode -eq 'ProcessRun'){'--pilot-stage-run'}else{'--pilot-stage-checks'})) 'Preview substituted a different entry.'
            Require ($processPreview.EffectsOnExecution.StageChildCleanupRequiresItsOwnReceipt -and -not $processPreview.EffectsOnExecution.FixtureDescendants) 'Preview hid stage ownership limits.'
        }
        $selected=& $wrapper -Plan -ProcessChecks -CheckCase S01 -Sdk $blockedSdk
        Require ($selected.Arguments[3] -eq '--only' -and $selected.Arguments[4] -eq 'S01') 'Preview lost the closed selected check.'
        Require (-not (Test-Path -LiteralPath (Split-Path -Parent $absent))) 'Process preview created its target.'
        Require (($before -join "`n") -ceq ((LaunchFiles) -join "`n")) 'Process preview created launch artifacts.'
    }
    foreach ($case in 'missing-path','missing-hash','mismatched-hash') {
        Check $case {
            $before=LaunchFiles
            foreach ($mode in 'Checks','ProcessRun','ProcessChecks') {
            $arguments=@{NoBuild=$true;Sdk=$blockedSdk;DataPath=(Join-Path $OutputRoot 'never-created')}
            $arguments[$mode]=$true
            if ($case -ne 'missing-path') { $arguments.AssemblyPath=$sentinel }
            if ($case -ne 'missing-hash') { $arguments.ExpectedAssemblySha256='0'*64 }
            $text=(& $wrapper @arguments | Out-String)
            $code=$LASTEXITCODE
            [IO.File]::WriteAllText((Join-Path $OutputRoot ($case+'-'+$mode+'.json')),$text)
            $result=$text|ConvertFrom-Json
            Require ($code -eq 2 -and $result.kind -eq 'storage-launch-rejected') 'Missing/mismatched identity did not reject before launch.'
            Require ($result.helper_started -eq $false -and $null -eq $result.owned_pid) 'Identity rejection claimed a helper/PID.'
            Require (($before -join "`n") -ceq ((LaunchFiles) -join "`n")) 'Identity rejection created helper launch artifacts.'
            Require ([IO.File]::ReadAllText($sentinel) -ceq "not-an-executable`n") 'Sentinel bytes changed.'
            if ($case -eq 'mismatched-hash') {
                Require ($result.expected_assembly_sha256 -eq ('0'*64) -and $result.actual_assembly_sha256 -match '^[0-9A-Fa-f]{64}$') 'Mismatch evidence did not retain both identities.'
            }
            }
        }
    }
} finally { $blocker.Dispose() }
$report=@{result=$(if($failed){'failed'}else{'passed'});checks=$checks;helper_started=$false;sentinel_loaded=$false;fixture_root=$OutputRoot}
$json=$report|ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $OutputRoot 'verification.json'),$json)
$json
exit $(if($failed){1}else{0})
