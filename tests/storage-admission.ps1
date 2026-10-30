param(
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$WorkerExecutable,
    [Parameter(Mandatory)][string]$EngineRoot,
    [switch]$Plan
)
$ErrorActionPreference = 'Stop'
$worker = [IO.Path]::GetFullPath($WorkerExecutable)
$data = [IO.Path]::GetFullPath($DataDirectory)
$engine = [IO.Path]::GetFullPath($EngineRoot)
$launch = [pscustomobject]@{
    route = 'Replay output status through an actual paused worker and ordinary client'
    executable = $worker
    arguments = @('--replay-storage-worker', $data, $worker, $engine)
    effects = [pscustomobject]@{
        isolatedPausedWorker = $true
        currentUserPipe = $true
        hiddenFixtureShortcuts = 'Ctrl+Alt+Shift F4-F6'
        existingEngineReadOnly = $true
        capture = $false
        engineExecution = $false
        gpu = $false
        audioPlayback = $false
        visibleUi = $false
        startupWrite = $false
        installation = $false
    }
}
if ($Plan) { return $launch }
if (-not (Test-Path -LiteralPath $worker -PathType Leaf)) { throw 'Build the reviewed WorkerChecks executable first.' }
if (Test-Path -LiteralPath (Join-Path $data 'profile')) { throw 'Use a fresh isolated evidence directory.' }
& $worker @($launch.arguments)
if ($LASTEXITCODE -ne 0) { throw 'Paused storage status tracer failed; preserve its evidence.' }

