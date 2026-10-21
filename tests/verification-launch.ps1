# Pure launch descriptions: dot-sourcing this file builds and launches nothing.
function Get-VerificationLaunchPlan {
    param(
        [ValidateSet('WindowCapture','Worker')][string]$Suite,
        [ValidateSet('Synthetic','Window','LiveDesktop')][string]$Mode,
        [string]$Root, [string]$DataDirectory, [string]$Desktop,
        [ValidateRange(10,86400)][int]$SoakSeconds=120,
        [ValidateSet(24,30,60,120)][int]$FramesPerSecond=60,
        [switch]$GpuFixture
    )
    if (($Suite -eq 'WindowCapture' -and $Mode -eq 'LiveDesktop') -or
        ($Suite -eq 'Worker' -and $Mode -eq 'Window')) { throw 'Unsupported verification route.' }
    if ($GpuFixture -and ($Suite -ne 'Worker' -or $Mode -ne 'LiveDesktop')) { throw 'GPU fixture requires the live worker route.' }
    $headless = $Suite -eq 'WindowCapture' -and $Mode -eq 'Synthetic'
    $realCapture = $Mode -ne 'Synthetic'
    if ($Suite -eq 'WindowCapture') {
        $entry = if ($headless) { '--headless-status' } else { '--window-checks' }
        $arguments = @($entry,$DataDirectory,$Desktop)
        $executable = Join-Path $Root 'bin/window-checks/checks/F10Recorder.WorkerChecks.exe'
    } else {
        $arguments = @($DataDirectory,$Desktop,[string]$SoakSeconds)
        if ($realCapture) { $arguments += @('--live','--fps',[string]$FramesPerSecond) }
        if ($GpuFixture) { $arguments += '--gpu-fixture' }
        $executable = Join-Path $Root 'bin/worker-checks/F10Recorder.WorkerChecks.exe'
    }
    [pscustomobject]@{
        Suite=$Suite; Mode=$Mode; Root=$Root; DataDirectory=$DataDirectory; Desktop=$Desktop
        SoakSeconds=$SoakSeconds; FramesPerSecond=$FramesPerSecond; GpuFixture=[bool]$GpuFixture
        Executable=$executable; Arguments=$arguments; Headless=$headless
        Effects=[ordered]@{
            UiWindows=(-not $headless); Focus=(-not $headless)
            DesktopPixels=$realCapture; SystemAudio=$realCapture; GpuEncoding=$true
            StartupWrites=($Suite -eq 'Worker'); NvidiaReplayMayStop=(-not $headless)
        }
    }
}

function Assert-VerificationLaunchPlan {
    param($Plan)
    $expected = Get-VerificationLaunchPlan -Suite $Plan.Suite -Mode $Plan.Mode -Root $Plan.Root `
        -DataDirectory $Plan.DataDirectory -Desktop $Plan.Desktop -SoakSeconds $Plan.SoakSeconds `
        -FramesPerSecond $Plan.FramesPerSecond -GpuFixture:$Plan.GpuFixture
    foreach ($field in @('Executable','Arguments','Headless','Effects')) {
        if (($Plan.$field | ConvertTo-Json -Compress -Depth 5) -cne ($expected.$field | ConvertTo-Json -Compress -Depth 5)) {
            throw "Verification launch contradicts the declared route: $field"
        }
    }
}

function Start-VerificationLaunchPlan {
    param($Plan)
    Assert-VerificationLaunchPlan $Plan
    Write-Host ('Verification effects: ' + ($Plan.Effects | ConvertTo-Json -Compress))
    $arguments = @($Plan.Arguments | ForEach-Object {
        if ($_ -match '["\r\n]') { throw 'Invalid character in a verification argument.' }
        '"' + $_ + '"'
    })
    Start-Process -FilePath $Plan.Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
}
