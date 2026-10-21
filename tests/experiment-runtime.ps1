# Runtime checks use existing status polling; no extra observer process per sample.
function Resolve-ExperimentInputPath {
    param([string]$Root,[string]$InputPath)
    $candidate=if ([IO.Path]::IsPathRooted($InputPath)) { $InputPath } else { Join-Path $Root $InputPath }
    [IO.Path]::GetFullPath($candidate)
}

function Assert-ExperimentInputs {
    param($Plan,[string]$Root,[string]$DataDirectory,[string]$ActualWorkerExecutable,[string]$ActualCaptureExecutable)
    $expected = [ordered]@{
        desktop_apphost='bin/avalonia/F10Recorder.Desktop.exe'
        desktop_assembly='bin/avalonia/F10Recorder.Desktop.dll'
        windows_assembly='bin/avalonia/F10Recorder.Windows.dll'
        core_assembly='bin/avalonia/F10Recorder.Core.dll'
        presentmon='.tools/presentmon/PresentMon-2.6.0-x64.exe'
        profile=[IO.Path]::GetFullPath((Join-Path $DataDirectory 'settings.json'))
    }
    foreach ($role in $expected.Keys) {
        $declared=$Plan.collection_inputs.$role
        if (-not $declared -or $declared -notin $Plan.identity_files.PSObject.Properties.Name) { throw "Required measured input missing: $role" }
        $declaredPath=Resolve-ExperimentInputPath $Root $declared
        $expectedPath=Resolve-ExperimentInputPath $Root $expected[$role]
        if ($declaredPath -ne $expectedPath) { throw "Collection input differs from the actual runner path: $role" }
    }
    $engine=$Plan.collection_inputs.capture_engine
    if (-not $engine -or $engine -notin $Plan.identity_files.PSObject.Properties.Name) { throw 'Required measured input missing: capture_engine' }
    if ($ActualWorkerExecutable -and [IO.Path]::GetFullPath($ActualWorkerExecutable) -ne (Resolve-ExperimentInputPath $Root $Plan.collection_inputs.desktop_apphost)) {
        throw 'Running worker executable differs from the declared apphost.'
    }
    if ($ActualCaptureExecutable -and [IO.Path]::GetFullPath($ActualCaptureExecutable) -ne (Resolve-ExperimentInputPath $Root $engine)) {
        throw 'Running capture executable differs from the declared engine.'
    }
}

function Stop-ExperimentCapture {
    param([scriptblock]$Pause,[scriptblock]$ReadStatus,[int]$TimeoutMilliseconds=5000)
    $null=& $Pause
    $stopDeadline=[Diagnostics.Stopwatch]::StartNew()
    do {
        $state=& $ReadStatus
        if ($state.Paused -and -not $state.Ready -and $state.Metrics.CaptureProcessId -eq 0) { return }
        if ($stopDeadline.ElapsedMilliseconds -ge $TimeoutMilliseconds) { throw 'Task-owned capture did not stop after pause.' }
        Start-Sleep -Milliseconds 100
    } while ($true)
}

function Assert-ExperimentStorage {
    param($Plan,[string]$Root)
    $items = @(Get-ChildItem -LiteralPath $Root -Force -Recurse -ErrorAction Stop)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Linked experiment storage cannot be accounted safely.'
    }
    [long]$used = ($items | Where-Object { -not $_.PSIsContainer } | Measure-Object -Property Length -Sum).Sum
    [long]$reserve = $Plan.storage.finalization_reserve_bytes + $Plan.storage.poll_growth_reserve_bytes
    if ($used + $reserve -gt $Plan.storage.budget_bytes) { throw 'Experiment storage budget would be exceeded.' }
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Root)))
    if ($drive.AvailableFreeSpace -lt $reserve + $Plan.storage.minimum_free_bytes) { throw 'Experiment free-space reserve would be violated.' }
}

function Assert-ExperimentSample {
    param($Previous,$Sample,$Plan,[string]$Mode)
    if ($null -ne $Previous) {
        $gap = $Sample.ElapsedSeconds - $Previous.ElapsedSeconds
        if ($gap -le 0 -or $gap -gt $Plan.maximum_sample_gap_seconds) { throw 'Invalid or excessive worker sample gap.' }
        if ($Previous.Status.WorkerProcessId -ne $Sample.Status.WorkerProcessId -or
            $Previous.Status.Metrics.CaptureProcessId -ne $Sample.Status.Metrics.CaptureProcessId -or
            $Previous.Status.Metrics.Recoveries -ne $Sample.Status.Metrics.Recoveries) { throw 'Recorder process changed or recovered during the trial.' }
    }
    $state=$Sample.Status
    if ($state.WorkerProcessId -le 0 -or $state.ApplicationName -ne 'cs2') { throw 'Worker or game source is unqualified.' }
    if ($Mode -eq 'On') {
        if (-not $state.Ready -or $state.Paused -or $state.Metrics.CaptureProcessId -le 0) { throw 'Recording is not ready.' }
    } elseif (-not $state.Paused -or $state.Metrics.CaptureProcessId -ne 0) { throw 'Recorder-off control is recording.' }
}
