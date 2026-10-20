param([string]$Executable, [string]$DataDirectory, [switch]$KeepOpen)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $root 'bin\avalonia-publish\F10Recorder.Desktop.exe' }
$customData = -not [string]::IsNullOrWhiteSpace($DataDirectory)
if (-not $DataDirectory) { $DataDirectory = Join-Path $root ('artifacts\published-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss')); $customData = $true }
$DataDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($DataDirectory))
New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $DataDirectory 'settings.json'))) {
    [ordered]@{ ReplayMinutes = 5; FramesPerSecond = 60; BitrateMbps = 20; SaveKey = 112; SaveModifiers = 7; OverlayKey = 113; OverlayModifiers = 7; ScreenshotKey = 123; ScreenshotModifiers = 7; OutputDirectory = (Join-Path $DataDirectory 'clips'); ScreenshotDirectory = (Join-Path $DataDirectory 'screenshots') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $DataDirectory 'settings.json') -Encoding UTF8
}
if (-not ('F10PublishedProbe' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class F10PublishedProbe {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    public static IntPtr FindByTitle(string title) { return FindWindow(null, title); }
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
}
'@
}
$configBefore = (Get-FileHash -LiteralPath (Join-Path $DataDirectory 'settings.json')).Hash
$arguments = '--paused --settings --data-dir "' + $DataDirectory + '"'
$start = [Diagnostics.ProcessStartInfo]::new($Executable, $arguments)
$start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
$start.Environment['DOTNET_ROOT'] = Join-Path $DataDirectory 'no-installed-runtime'
$start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
$process = [Diagnostics.Process]::Start($start)
$statusFile = Join-Path $DataDirectory 'status.json'
function Wait-State([scriptblock]$Condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        if ($process.HasExited) { throw "Published application exited: $($process.ExitCode)" }
        try { $script:probeState = Get-Content -LiteralPath $statusFile -Raw | ConvertFrom-Json } catch { continue }
        if ($probeState.UiProcessId -ne $process.Id) { continue }
        if (& $Condition) { return }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Published application did not reach the requested state.'
}
$suffix = '-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($DataDirectory.ToUpperInvariant()))).Substring(0,16)
$controlTitle = 'F10Recorder.ControlWindow' + $suffix
try {
    Wait-State { $probeState.Ui -eq 'Avalonia' -and $probeState.Paused -and $probeState.SettingsVisible }
    $controlTitle = $probeState.ControlTitle
    $window = [F10PublishedProbe]::FindByTitle($controlTitle)
    if ($window -eq [IntPtr]::Zero) { throw 'Native control window was not created.' }
    # Save while paused produces a passive notice without recording any footage.
    if (-not [F10PublishedProbe]::PostMessage($window,0x0312,[IntPtr]1,[IntPtr]::Zero)) { throw 'Could not dispatch the paused-save probe.' }
    Wait-State { $probeState.NotificationVisible -and $probeState.NotificationTitle -eq 'Replay not saved' }
    $notice = [F10PublishedProbe]::FindByTitle('F10 Recorder Notification')
    $before = [F10PublishedProbe+Rect]::new(); [void][F10PublishedProbe]::GetWindowRect($notice,[ref]$before)
    # Send through a second copy of the published executable: proves single-instance routing.
    $toggle = [Diagnostics.Process]::Start([Diagnostics.ProcessStartInfo]::new($Executable, '--overlay --data-dir "' + $DataDirectory + '"'))
    $toggle.WaitForExit(); if ($toggle.ExitCode -ne 0) { throw 'Published overlay command failed.' }
    Wait-State { $probeState.OverlayVisible }
    $beside = [F10PublishedProbe+Rect]::new(); [void][F10PublishedProbe]::GetWindowRect($notice,[ref]$beside)
    if ($beside.Left -ge $before.Left) { throw 'Active notification did not move beside the overlay.' }
    $toggle = [Diagnostics.Process]::Start([Diagnostics.ProcessStartInfo]::new($Executable, '--overlay --data-dir "' + $DataDirectory + '"')); $toggle.WaitForExit()
    Wait-State { -not $probeState.OverlayVisible -and $probeState.SettingsVisible }
    if ((Get-FileHash -LiteralPath (Join-Path $DataDirectory 'settings.json')).Hash -ne $configBefore) { throw 'Cold launch changed recording preferences.' }
    'Passed: self-contained cold launch, visible Settings, paused state, single-instance commands, notification repositioning and preserved preferences.'
} finally {
    if (-not $KeepOpen -and -not $process.HasExited) {
        $quitStart = [Diagnostics.ProcessStartInfo]::new($Executable, '--quit --data-dir "' + $DataDirectory + '"'); $quitStart.UseShellExecute = $false; $quitStart.CreateNoWindow = $true; $quitStart.WindowStyle = 'Hidden'
        $quit = [Diagnostics.Process]::Start($quitStart); $quit.WaitForExit()
        if (-not $process.WaitForExit(20000)) { Write-Warning 'The isolated published probe is still running.' }
    }
}
