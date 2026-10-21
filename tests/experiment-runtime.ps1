# Runtime checks use existing status polling; no extra observer process per sample.
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
