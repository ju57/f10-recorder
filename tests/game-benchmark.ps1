param(
    [ValidateSet('Start','Status','Pause','Resume','Off','On','Save','Quit')][string]$Action = 'Status',
    [ValidateRange(5,3600)][int]$Seconds = 60,
    [string]$Label = 'trial',
    [switch]$WaitForResume,
    [string]$DataDirectory,
    [string]$ExperimentPlan,
    [switch]$Qualification,
    [switch]$ValidatePlan
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'experiment-runtime.ps1')
function Invoke-ExperimentContract([string[]]$Options) {
    $python = (Get-Command python -ErrorAction Stop).Source
    $result = & $python (Join-Path $PSScriptRoot 'experiment_contract.py') @Options
    if ($LASTEXITCODE) { throw ('Experiment contract rejected: ' + ($Options -join ' ')) }
    return (($result -join "`n") | ConvertFrom-Json)
}
if ($ValidatePlan) {
    if (-not $ExperimentPlan) { throw '-ExperimentPlan is required.' }
    Invoke-ExperimentContract @('validate-plan',$ExperimentPlan) | ConvertTo-Json -Depth 10
    return
}
if (-not $DataDirectory) { throw 'Supply a fresh isolated -DataDirectory explicitly.' }
$data = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$relativeData = [IO.Path]::GetRelativePath($artifactsRoot,$data)
if ([IO.Path]::IsPathRooted($relativeData) -or $relativeData -eq '..' -or $relativeData.StartsWith('..'+[IO.Path]::DirectorySeparatorChar) -or $relativeData -eq '.') {
    throw 'Benchmark profiles must be isolated beneath this repository artifacts directory.'
}
$evidence = Split-Path -Parent $data
$env:DOTNET_ROOT = Join-Path $root '.tools\dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$session = [Diagnostics.Process]::GetCurrentProcess().SessionId
$scopeText = '{0}:{1}:{2}' -f $identity.User.Value,$session,$data.ToUpperInvariant()
$sha = [Security.Cryptography.SHA256]::Create()
try { $scope = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($scopeText)))).Replace('-','').Substring(0,24) } finally { $sha.Dispose() }
function Read-Bytes($stream, [int]$count) {
    $bytes = New-Object byte[] $count
    $offset = 0
    $deadline = New-Object Threading.CancellationTokenSource(5000)
    try {
        while ($offset -lt $count) {
            $read = $stream.ReadAsync($bytes,$offset,$count-$offset,$deadline.Token).GetAwaiter().GetResult()
            if ($read -eq 0) { throw 'Worker closed its reply early.' }
            $offset += $read
        }
    } finally { $deadline.Dispose() }
    return ,$bytes
}
function Call-Worker([string]$command = 'status', [bool]$value = $false, [long]$after = -1) {
    $pipe = New-Object IO.Pipes.NamedPipeClientStream('.',('F10Recorder.v1.'+$scope),[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(5000)
        $request = @{Version=3;Command=$command;Value=$value;AfterEvent=$after} | ConvertTo-Json -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($request)
        $header = [BitConverter]::GetBytes([int]$bytes.Length)
        $pipe.Write($header,0,4); $pipe.Write($bytes,0,$bytes.Length); $pipe.Flush()
        $replyHeader = Read-Bytes $pipe 4
        $length = [BitConverter]::ToInt32($replyHeader,0)
        if ($length -le 0 -or $length -gt 1048576) { throw 'Invalid worker reply length.' }
        $reply = [Text.Encoding]::UTF8.GetString((Read-Bytes $pipe $length)) | ConvertFrom-Json
        if ($reply.Version -ne 3) { throw 'Recorder protocol version mismatch.' }
        if ($reply.Error) { throw $reply.Error }
        return $reply.Status
    } finally { $pipe.Dispose() }
}
function Save-Replay {
    param([scriptblock]$Observe)
    $sequence = (Call-Worker).Sequence
    $null = Call-Worker 'save'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt 40) {
        $state = Call-Worker 'status' $false $sequence
        if ($Observe) { & $Observe $state }
        $saved = $state.Events | Where-Object { $_.Kind -eq 'saved' } | Select-Object -First 1
        if ($saved) {
            if (-not $saved.Success -or -not (Test-Path -LiteralPath $saved.Message)) { throw ('Replay save failed: '+$saved.Message) }
            [pscustomobject]@{Label=$Label;Utc=[DateTime]::UtcNow.ToString('O');Clip=$saved.Message;CommandToCompletionMs=$clock.Elapsed.TotalMilliseconds;Metrics=$state.Metrics} | ConvertTo-Json -Depth 6 -Compress | Add-Content -LiteralPath (Join-Path $evidence 'saves.jsonl')
            return $saved.Message
        }
        Start-Sleep -Milliseconds 200
    }
    throw 'Replay did not complete within 40 seconds.'
}
if ($Action -eq 'Start') {
    if (Test-Path -LiteralPath $data) { throw 'Use a fresh isolated data directory for Start.' }
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    @{ReplayMinutes=1;FramesPerSecond=120;BitrateMbps=20;VideoCodec='h264_nvenc';SystemAudio=$true;DisplayIndex=-1;OutputDirectory=(Join-Path $data 'clips');ScreenshotDirectory=(Join-Path $data 'screenshots');SaveKey=117;SaveModifiers=7;OverlayKey=118;OverlayModifiers=7;ScreenshotKey=119;ScreenshotModifiers=7} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding UTF8
    $worker = Start-Process -FilePath (Join-Path $root 'bin\avalonia\F10Recorder.Desktop.exe') -ArgumentList @('--recorder-worker','--paused','--data-dir',('"'+$data+'"')) -WindowStyle Hidden -PassThru
    $worker.Id | Set-Content -LiteralPath (Join-Path $evidence 'worker-pid.txt')
    Start-Sleep -Seconds 2
    Call-Worker | ConvertTo-Json -Depth 8
    exit
}
if ($Action -eq 'Status') { Call-Worker | ConvertTo-Json -Depth 8; exit }
if ($Action -eq 'Quit') { Call-Worker 'quit' | ConvertTo-Json -Depth 8; exit }
if ($Action -eq 'Pause') { Call-Worker 'pause' $true | ConvertTo-Json -Depth 8; exit }
if ($Action -eq 'Resume') { Call-Worker 'pause' $false | ConvertTo-Json -Depth 8; exit }
if ($Action -eq 'Save') { Save-Replay; exit }
if ($Label -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Label must contain only letters, digits, hyphens or underscores.' }
$csv = Join-Path $evidence ($Label+'-presentmon.csv')
$sampleFile = Join-Path $evidence ($Label+'-samples.jsonl')
foreach ($previous in @($csv,$sampleFile,(Join-Path $evidence ($Label+'-worker.json')),(Join-Path $evidence ($Label+'-failure.json')),(Join-Path $evidence ($Label+'-resume.json')),(Join-Path $evidence ($Label+'-armed.json')),(Join-Path $evidence ($Label+'-ready.json')))) {
    if (Test-Path -LiteralPath $previous) { throw 'Use a new label; previous trial evidence must not be overwritten.' }
}
if (-not $ExperimentPlan) { throw 'Measurement collection requires -ExperimentPlan and a successful qualification before the batch.' }
$plan = Invoke-ExperimentContract @('validate-plan',$ExperimentPlan)
$settingsPath = Join-Path $data 'settings.json'
$profile = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$profileIdentity = [IO.Path]::GetRelativePath($root,[IO.Path]::GetFullPath($settingsPath)).Replace('\','/')
if ($profileIdentity -notin $plan.identity_files.PSObject.Properties.Name) { throw 'Include the isolated settings file in plan identity_files.' }
foreach ($outputPath in @($profile.OutputDirectory,$profile.ScreenshotDirectory)) {
    if (-not $outputPath -or -not [IO.Path]::IsPathRooted($outputPath)) { throw 'Use absolute task-isolated output paths.' }
    $relativeOutput = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($evidence),[IO.Path]::GetFullPath($outputPath))
    if ([IO.Path]::IsPathRooted($relativeOutput) -or $relativeOutput -eq '..' -or $relativeOutput.StartsWith('..'+[IO.Path]::DirectorySeparatorChar)) {
        throw 'Benchmark media outputs must stay inside the budgeted experiment directory.'
    }
}
$admission = @('admit',$ExperimentPlan,'--root',$evidence,'--label',$Label,'--mode',$Action,'--seconds',[string]$Seconds)
if ($Qualification) { $admission += '--qualification' }
$admitted = Invoke-ExperimentContract $admission
$trace = $null
$samples = [Collections.Generic.List[object]]::new()
try {
    $capture = $Action -eq 'On'
    $games = @(Get-Process | Where-Object { $_.ProcessName -eq 'cs2' })
    if ($games.Count -ne 1) { throw 'Expected exactly one running CS2 process.' }
    $null = Call-Worker 'pause' (-not $capture)
    $readyClock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $state = Call-Worker
        Assert-ExperimentStorage -Plan $plan -Root $evidence
        if (($capture -and $state.Ready -and $state.Metrics.CaptureProgressSeconds -ge 5) -or (-not $capture -and $state.Paused -and $state.Metrics.CaptureProcessId -eq 0)) { break }
        if ($readyClock.Elapsed.TotalSeconds -gt 30) { throw ('Worker did not reach requested mode: '+$state.State) }
        Start-Sleep -Milliseconds 250
    } while ($true)
    if ($WaitForResume) {
        $resumeFile = Join-Path $evidence ($Label+'-resume.json')
        if (Test-Path -LiteralPath $resumeFile) { throw 'A resume marker already exists for this label.' }
        [pscustomobject]@{Label=$Label;Stage='armed';Mode=$Action;WorkerPid=$state.WorkerProcessId} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence ($Label+'-armed.json')) -Encoding UTF8
        $armClock = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $resumeFile)) {
            Assert-ExperimentStorage -Plan $plan -Root $evidence
            if ($armClock.Elapsed.TotalSeconds -gt 180) {
                if ($capture) { $null = Call-Worker 'pause' $true }
                throw 'Timed out waiting for the game replay to resume.'
            }
            Start-Sleep -Milliseconds 100
        }
    }
    $tool = Join-Path $root '.tools\presentmon\PresentMon-2.6.0-x64.exe'
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$tool
    $info.Arguments='--process_id '+$games[0].Id+' --timed '+$Seconds+' --terminate_after_timed --no_console_stats --track_gpu_video --session_name F10Game'+[Guid]::NewGuid().ToString('N')+' --output_file "'+$csv+'"'
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $trace = [Diagnostics.Process]::Start($info)
    $traceStartUtc = [DateTime]::UtcNow.ToString('O')
    [pscustomobject]@{Label=$Label;TracePid=$trace.Id;TraceStartUtc=$traceStartUtc;Mode=$Action} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence ($Label+'-ready.json')) -Encoding UTF8
    $stdoutTask=$trace.StandardOutput.ReadToEndAsync(); $stderrTask=$trace.StandardError.ReadToEndAsync()
    $measurementClock = [Diagnostics.Stopwatch]::StartNew()
    $nextSave = if ($WaitForResume -and $Seconds -lt 120) { 65 } else { 45 }
    $lastProgress = -1.0
    $progressChangedAt = 0.0
    $observe = {
        param($observedState)
        $sample = [pscustomobject]@{ElapsedSeconds=$measurementClock.Elapsed.TotalSeconds;Status=$observedState}
        $previousSample = if ($samples.Count) { $samples[$samples.Count-1] } else { $null }
        $samples.Add($sample)
        $sample | ConvertTo-Json -Depth 10 -Compress | Add-Content -LiteralPath $sampleFile
        Assert-ExperimentSample -Previous $previousSample -Sample $sample -Plan $plan -Mode $Action
        Assert-ExperimentStorage -Plan $plan -Root $evidence
    }
    while (-not $trace.HasExited -and $measurementClock.Elapsed.TotalSeconds -lt $Seconds+15) {
        $state = Call-Worker
        & $observe $state
        if ($capture -and (-not $state.Ready -or $state.Paused)) { throw ('Recording interrupted: '+$state.State) }
        if ($capture) {
            if ($state.Metrics.CaptureProgressSeconds -gt $lastProgress) {
                $lastProgress = $state.Metrics.CaptureProgressSeconds
                $progressChangedAt = $measurementClock.Elapsed.TotalSeconds
            } elseif ($measurementClock.Elapsed.TotalSeconds - $progressChangedAt -gt 10) { throw 'Capture progress stalled for more than 10 seconds.' }
            if ($state.ApplicationName -ne 'cs2') { throw ('Foreground changed during game recording: '+$state.ApplicationName) }
        }
        if ($capture -and $measurementClock.Elapsed.TotalSeconds -ge $nextSave) { $null = Save-Replay -Observe $observe; $nextSave += 60 }
        Start-Sleep -Milliseconds ([int]($plan.sampling_interval_seconds * 1000))
    }
    if (-not $trace.HasExited) { throw 'PresentMon exceeded its time limit.' }
    if ($trace.ExitCode -ne 0) { throw ('PresentMon failed: '+$trace.ExitCode+' '+$stderrTask.Result) }
    if (-not (Test-Path -LiteralPath $csv) -or (Get-Item -LiteralPath $csv).Length -lt 1000) { throw 'PresentMon produced no useful frame trace.' }
    if ($capture) { $null = Save-Replay -Observe $observe }
    [pscustomobject]@{Mode=$Action;Label=$Label;Seconds=$Seconds;GamePid=$games[0].Id;TraceStartUtc=$traceStartUtc;PresentMonCSV=$csv;TraceExitCode=$trace.ExitCode;Stdout=$stdoutTask.Result;Stderr=$stderrTask.Result;Samples=$samples;ExperimentPlan=$plan;ExperimentPlanSha256=$admitted.plan_sha256} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence ($Label+'-worker.json')) -Encoding UTF8
    $null = Invoke-ExperimentContract @('finish',$ExperimentPlan,'--root',$evidence,'--label',$Label,'--trace',(Join-Path $evidence ($Label+'-worker.json')))
    Write-Output ('Completed '+$Label+'; sampled health valid, source/performance acceptance separate.')
} catch {
    $trialError = $_.Exception.ToString()
    [pscustomobject]@{Mode=$Action;Label=$Label;Result='failed';Error=$_.Exception.ToString();Seconds=$Seconds;PresentMonCSV=$csv;SamplesFile=$sampleFile;Samples=$samples;ExperimentPlan=$plan;ExperimentPlanSha256=$admitted.plan_sha256} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence ($Label+'-failure.json')) -Encoding UTF8
    if (Test-Path -LiteralPath (Join-Path $evidence ('experiment-state/'+$Label+'.pending.json'))) {
        try { $null = Invoke-ExperimentContract @('finish',$ExperimentPlan,'--root',$evidence,'--label',$Label,'--error',$trialError) } catch { Write-Warning $_ }
    }
    if ($capture) { try { $null = Call-Worker 'pause' $true } catch { Write-Warning ('Could not pause test worker after failure: '+$_.Exception.Message) } }
    throw
} finally { if ($null -ne $trace) { if (-not $trace.HasExited) { $trace.Kill(); $trace.WaitForExit() }; $trace.Dispose() } }
