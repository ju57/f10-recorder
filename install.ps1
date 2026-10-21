param([switch]$NoLaunch, [switch]$Avalonia, [switch]$Legacy)
$ErrorActionPreference = 'Stop'
if ($Avalonia -and $Legacy) { throw 'Choose either the Avalonia app or the legacy reference build.' }
$useAvalonia = -not $Legacy
$source = Join-Path $PSScriptRoot 'bin\F10Recorder.exe'
$publish = Join-Path $PSScriptRoot 'bin\avalonia-publish'
if ($useAvalonia) {
    $source = Join-Path $publish 'F10Recorder.Desktop.exe'
    if (-not (Test-Path -LiteralPath $source)) { & (Join-Path $PSScriptRoot 'build-avalonia.ps1') -Publish }
} elseif (-not (Test-Path -LiteralPath $source)) { & (Join-Path $PSScriptRoot 'build.ps1') }
$appRoot = Join-Path $env:LOCALAPPDATA 'F10Recorder'
$destination = Join-Path $appRoot 'application'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$executable = Join-Path $destination 'F10Recorder.exe'
if (Get-Process -Name F10Recorder,F10Recorder.Desktop -ErrorAction SilentlyContinue) { throw 'Quit F10 Recorder from its tray menu before reinstalling.' }
if ($useAvalonia) { Get-ChildItem -LiteralPath $publish | Where-Object Name -ne 'F10Recorder.Desktop.exe' | Copy-Item -Destination $destination -Recurse -Force }
Copy-Item -LiteralPath $source -Destination $executable -Force
foreach ($noticeName in @('LICENSE', 'THIRD_PARTY_NOTICES')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $noticeName) -Destination (Join-Path $destination $noticeName) -Force
}
$config = Join-Path $appRoot 'settings.json'
if (-not (Test-Path -LiteralPath $config)) {
    $initialSettings = [ordered]@{
        ReplayMinutes = 5; FramesPerSecond = 60; BitrateMbps = 20
        SystemAudio = $true; DisplayIndex = -1
        OutputDirectory = Join-Path ([Environment]::GetFolderPath('MyVideos')) 'F10 Recorder'
        SaveKey = 121; SaveModifiers = 6
        OverlayKey = 120; OverlayModifiers = 6
        ScreenshotKey = 119; ScreenshotModifiers = 6
        ScreenshotDirectory = Join-Path ([Environment]::GetFolderPath('MyPictures')) 'F10 Recorder'
    }
    $initialSettings | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding UTF8
}
Write-Host 'Preparing the approved recording engine. Setup downloads a verified private copy when needed.'
$engineSetup = Start-Process -FilePath $executable -ArgumentList @('--ensure-media','--data-dir',('"' + $appRoot + '"')) -WindowStyle Hidden -PassThru -Wait
if ($engineSetup.ExitCode -ne 0) {
    $errorFile = Join-Path $appRoot 'media-install-error.txt'
    $detail = if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile -Raw } else { 'Recording engine setup failed.' }
    throw $detail
}
Write-Host 'Approved recording engine ready. Verified offline reuse is available.'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey | Out-Null }
Set-ItemProperty -LiteralPath $runKey -Name F10Recorder -Value ('"' + $executable + '"')
if (-not $NoLaunch) { Start-Process -FilePath $executable -WindowStyle Hidden }
Write-Host "Installed $executable"
Write-Host 'Windows sign-in startup enabled. Ctrl+Shift+F10 saves the rolling replay.'
