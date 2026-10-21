$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'verification-launch.ps1')
$root=Split-Path -Parent $PSScriptRoot
function Require($condition,$message) { if (-not $condition) { throw $message } }
function Rejected([scriptblock]$action) {
    $rejected=$false
    try { & $action } catch { $rejected=$true }
    Require $rejected 'An invalid launch was accepted.'
}
# Preview the real scripts, before their build/directory/process side effects.
$headless = & (Join-Path $PSScriptRoot 'window-capture.ps1') -Plan
Require ($headless.Headless -and $headless.Arguments[0] -eq '--headless-status') 'Default window route is not headless.'
Require (-not $headless.Effects.UiWindows -and -not $headless.Effects.DesktopPixels -and -not $headless.Effects.SystemAudio -and -not $headless.Effects.StartupWrites) 'Headless effects changed.'
$window = & (Join-Path $PSScriptRoot 'window-capture.ps1') -Mode Window -Plan
Require ($window.Arguments[0] -eq '--window-checks' -and $window.Effects.UiWindows -and $window.Effects.DesktopPixels -and $window.Effects.SystemAudio) 'Window route effects missing.'
$worker = & (Join-Path $PSScriptRoot 'worker.ps1') -Plan
Require (-not $worker.Headless -and $worker.Effects.UiWindows -and $worker.Effects.NvidiaReplayMayStop -and -not $worker.Effects.DesktopPixels) 'Synthetic worker concealed its UI effects.'
$live = & (Join-Path $PSScriptRoot 'worker.ps1') -LiveDesktop -GpuFixture -Plan
Require ($live.Effects.DesktopPixels -and $live.Effects.SystemAudio -and $live.Arguments -contains '--gpu-fixture') 'Live worker dispatch changed.'
foreach ($plan in @($headless,$window,$worker,$live)) { Assert-VerificationLaunchPlan $plan }
Rejected { & (Join-Path $PSScriptRoot 'worker.ps1') -GpuFixture -Plan }
$headless.Arguments[0]='--window-checks'
Rejected { Start-VerificationLaunchPlan $headless }
$worker.Headless=$true
Rejected { Start-VerificationLaunchPlan $worker }
$live.Effects.SystemAudio=$false
Rejected { Start-VerificationLaunchPlan $live }
Write-Host 'Passed: actual script previews, explicit UI/capture effects, and contradictory launches rejected before Start-Process.'
