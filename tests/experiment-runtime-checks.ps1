$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'experiment-runtime.ps1')
function Require($value,$message) { if (-not $value) { throw $message } }
function Rejected([scriptblock]$action) {
    $failed=$false
    try { & $action } catch { $failed=$true }
    Require $failed 'An invalid runtime sample was accepted.'
}
function Sample([double]$time,[int]$captureId=20,[int]$recoveries=0) {
    [pscustomobject]@{ElapsedSeconds=$time;Status=[pscustomobject]@{
        Ready=$true;Paused=$false;WorkerProcessId=10;ApplicationName='cs2'
        Metrics=[pscustomobject]@{CaptureProcessId=$captureId;Recoveries=$recoveries}
    }}
}
$plan=[pscustomobject]@{maximum_sample_gap_seconds=1.5}
$first=Sample 0
$next=Sample 1
Assert-ExperimentSample $first $next $plan On
Rejected { Assert-ExperimentSample $first (Sample 2) $plan On }
Rejected { Assert-ExperimentSample $first (Sample 1 21) $plan On }
Rejected { Assert-ExperimentSample $first (Sample 1 20 1) $plan On }
$next.Status.Ready=$false
Rejected { Assert-ExperimentSample $first $next $plan On }
$off=Sample 0 0
$off.Status.Paused=$true
$off.Status.Ready=$false
Assert-ExperimentSample $null $off $plan Off
Rejected { Assert-ExperimentSample $null $first $plan Off }
$repository=Split-Path -Parent $PSScriptRoot
$data=Join-Path $repository 'artifacts/offline-input-fixture/worker'
$roles=[ordered]@{
    desktop_apphost='bin/avalonia/F10Recorder.Desktop.exe';desktop_assembly='bin/avalonia/F10Recorder.Desktop.dll'
    windows_assembly='bin/avalonia/F10Recorder.Windows.dll';core_assembly='bin/avalonia/F10Recorder.Core.dll'
    presentmon='.tools/presentmon/PresentMon-2.6.0-x64.exe';profile=(Join-Path $data 'settings.json')
    capture_engine=(Join-Path $repository 'artifacts/offline-input-fixture/ffmpeg.exe')
}
$identities=[ordered]@{}
foreach ($name in $roles.Values) { $identities[$name]='0'*64 }
$inputs=[pscustomobject]@{collection_inputs=[pscustomobject]$roles;identity_files=[pscustomobject]$identities}
$actualWorker=Join-Path $repository 'bin/avalonia/F10Recorder.Desktop.exe'
Assert-ExperimentInputs $inputs $repository $data $actualWorker $roles.capture_engine
Rejected { Assert-ExperimentInputs $inputs $repository $data (Join-Path $repository 'old-worker.exe') $roles.capture_engine }
Rejected { Assert-ExperimentInputs $inputs $repository $data $actualWorker (Join-Path $repository 'old-ffmpeg.exe') }
$inputs.collection_inputs.presentmon='old-presentmon.exe'
Rejected { Assert-ExperimentInputs $inputs $repository $data }
$inputs.collection_inputs.presentmon=$roles.presentmon
# The real pause helper confirms asynchronous shutdown before returning.
$script:pauseCalls=0
$script:statusCalls=0
Stop-ExperimentCapture -Pause { $script:pauseCalls++ } -ReadStatus {
    $script:statusCalls++
    [pscustomobject]@{Paused=$true;Ready=$false;Metrics=[pscustomobject]@{CaptureProcessId=if ($script:statusCalls -eq 1) { 20 } else { 0 }}}
}
Require ($script:pauseCalls -eq 1 -and $script:statusCalls -eq 2) 'Capture shutdown was not confirmed.'
Rejected { Stop-ExperimentCapture -TimeoutMilliseconds 1 -Pause {} -ReadStatus { $first.Status } }
# Retain these tiny, task-created fixture files under ignored artifacts.
$root=Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/runtime-contract-checks-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
[IO.File]::WriteAllBytes((Join-Path $root 'budget.dat'),[byte[]]::new(1024))
$budget=[pscustomobject]@{storage=[pscustomobject]@{
    budget_bytes=4096;finalization_reserve_bytes=512;poll_growth_reserve_bytes=512;minimum_free_bytes=0
}}
Assert-ExperimentStorage $budget $root
$budget.storage.budget_bytes=1500
Rejected { Assert-ExperimentStorage $budget $root }
$budget.storage.budget_bytes=4096
$budget.storage.minimum_free_bytes=[long]::MaxValue-2048
Rejected { Assert-ExperimentStorage $budget $root }
Write-Host 'Passed: runtime readiness, input binding, confirmed pause, control state, PID, recovery, sample gaps, storage budget and disk reserve.'
