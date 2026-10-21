param(
    [Parameter(Mandatory)][string]$ExperimentPlan,
    [Parameter(Mandatory)][string]$DataDirectory,
    [switch]$Qualification,
    [switch]$ValidatePlan
)
$ErrorActionPreference='Stop'
$runner=Join-Path $PSScriptRoot 'game-benchmark.ps1'
$plan=(& $runner -ExperimentPlan $ExperimentPlan -ValidatePlan | Out-String) | ConvertFrom-Json
if ($ValidatePlan) { $plan | ConvertTo-Json -Depth 10; return }
$trials=if ($Qualification) { @($plan.qualification) } else { @($plan.trials) }
foreach ($trial in $trials) {
    # Admission enforces successful qualification, frozen order and prior validity.
    # A thrown failure ends this loop; it never retries or adds observations.
    & $runner -Action $trial.mode -Label $trial.label -Seconds $plan.trial_seconds `
        -ExperimentPlan $ExperimentPlan -DataDirectory $DataDirectory `
        -Qualification:$Qualification -WaitForResume
}
if ($Qualification) {
    Write-Host 'Qualification retained. Review the prospective window before starting the fixed batch.'
} else {
    Write-Host 'Fixed batch collected. Statistical, media and source-picture acceptance remain separate.'
}
