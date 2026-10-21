param([switch]$RemoveSettings)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name F10Recorder,F10Recorder.Desktop -ErrorAction SilentlyContinue) { throw 'Quit F10 Recorder from its tray menu first.' }
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -LiteralPath $runKey -Name F10Recorder -ErrorAction SilentlyContinue
$appRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'F10Recorder'))
$expected = [IO.Path]::GetFullPath($env:LOCALAPPDATA) + [IO.Path]::DirectorySeparatorChar
if (-not $appRoot.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($appRoot) -ne 'F10Recorder') { throw 'Unexpected application directory.' }
$target = if ($RemoveSettings) { $appRoot } else { Join-Path $appRoot 'application' }
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
if (-not $RemoveSettings) {
    $runtime = [IO.Path]::GetFullPath((Join-Path $appRoot 'runtime'))
    if (-not $runtime.StartsWith($appRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected runtime directory.' }
    if (Test-Path -LiteralPath $runtime) { Remove-Item -LiteralPath $runtime -Recurse -Force }
    $engineMetadata = Join-Path $appRoot 'media-engine.json'
    if (Test-Path -LiteralPath $engineMetadata) { Remove-Item -LiteralPath $engineMetadata -Force }
}
$privateRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'F10Recorder.Security'))
if (-not $privateRoot.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($privateRoot) -ne 'F10Recorder.Security') { throw 'Unexpected private storage directory.' }
if (Test-Path -LiteralPath $privateRoot) {
    $privateAncestor = Get-Item -LiteralPath $privateRoot
    while ($null -ne $privateAncestor) {
        if ($privateAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Private storage path contains a reparse point; automatic cleanup refused.' }
        $privateAncestor = $privateAncestor.Parent
    }
    $privateDirectories = @(Get-Item -LiteralPath $privateRoot) + @(Get-ChildItem -LiteralPath $privateRoot -Directory -Recurse -Force)
    if ($privateDirectories | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Private storage contains a reparse point; automatic cleanup refused.' }
    Remove-Item -LiteralPath $privateRoot -Recurse -Force
}
Write-Host 'F10 Recorder uninstalled. Saved clips were preserved.'
