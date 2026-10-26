param(
    [Parameter(Mandatory)][string]$ReportPath,
    [Parameter(Mandatory)][int]$RunExitCode,
    [Parameter(Mandatory)][ValidateSet('Normal', 'Sensitivity')][string]$Mode
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$control = $Mode -eq 'Sensitivity'
$expectedExit = if ($control) { 1 } else { 0 }
$expectedResult = if ($control) { 'failed' } else { 'passed' }
if ($RunExitCode -ne $expectedExit) { throw "Profile entry $Mode exited $RunExitCode; expected $expectedExit." }

$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json -NoEnumerate -ErrorAction Stop
if ($null -eq $report -or $report.GetType() -ne [System.Management.Automation.PSCustomObject]) { throw 'Profile entry report must be a JSON object.' }
if ($report.result -isnot [string] -or $report.result -cne $expectedResult -or $report.sensitivityControl -isnot [bool] -or $report.sensitivityControl -ne $control) {
    throw "Profile entry $Mode report result or boolean sensitivity flag is invalid."
}
if ($report.checks -isnot [array] -or $report.checks.Count -ne 27) {
    throw 'Profile entry report must contain exactly 27 checks.'
}
foreach ($check in $report.checks) {
    if ($null -eq $check -or $check.GetType() -ne [System.Management.Automation.PSCustomObject]) { throw 'Each profile entry check must be a JSON object.' }
}
$failures = @($report.checks | Where-Object { $_.result -isnot [string] -or $_.result -cne 'passed' })
if ($control) {
    if ($failures.Count -ne 1 -or $failures[0].result -isnot [string] -or $failures[0].result -cne 'failed' -or $failures[0].name -isnot [string] -or $failures[0].name -cne 'existing-context-fixture-sensitivity') {
        throw 'Profile entry sensitivity failed for an unintended reason.'
    }
} elseif ($failures.Count -ne 0) {
    throw 'Profile entry normal report contains unsuccessful checks.'
}
