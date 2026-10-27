param(
    [switch]$Plan, [switch]$Observe, [switch]$Checks, [switch]$NoBuild,
    [string]$Sdk, [string]$AssemblyPath, [string]$DataPath
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (-not $Sdk) { $Sdk = Join-Path $repository '.tools/dotnet/dotnet.exe' }
if (-not $AssemblyPath) { $AssemblyPath = Join-Path $repository 'projects/F10Recorder.WorkerChecks/bin/Release/net10.0-windows/F10Recorder.WorkerChecks.dll' }
if (-not $DataPath) { $DataPath = Join-Path $repository ('artifacts/pilot-storage-' + [Guid]::NewGuid().ToString('N')) }
if ($Observe -and $Checks) { throw 'Choose one storage entry point.' }
$entry = if ($Observe) { '--pilot-storage-observe' } else { '--pilot-storage-checks' }
$launch = [ordered]@{
    Executable=$Sdk; Arguments=@($AssemblyPath,$entry,$DataPath)
    WorkingDirectory=$repository; TimeoutSeconds=60; MaximumCombinedOutputBytes=131072
    EffectsOnExecution=[ordered]@{
        FreshRootAndMarkerWrites=$true; TinyNonmediaFixtureWrites=$true; StorageMetadataReads=$true
        ReportWrites=$true; Recorder=$false; Ui=$false; Engine=$false; Gpu=$false; Capture=$false
        Startup=$false; VendorChanges=$false; NestedChild=$false
    }
}
if ($Plan -or (-not $Observe -and -not $Checks)) { return [pscustomobject]$launch }
if (-not $NoBuild) { throw 'Build explicitly with the pinned SDK, then use -NoBuild. This route never provisions or builds.' }
if (-not [IO.Path]::IsPathFullyQualified($Sdk) -or [IO.Path]::GetFileName($Sdk) -notin @('dotnet.exe','dotnet')) { throw 'Supply the explicit pinned dotnet host path.' }
if (-not [IO.Path]::IsPathFullyQualified($AssemblyPath)) { throw 'Supply the absolute task-built assembly path.' }
if ([IO.Path]::GetFileName($AssemblyPath) -ne 'F10Recorder.WorkerChecks.dll') { throw 'Only the task-built storage verification assembly is supported.' }
$assemblyRelative = [IO.Path]::GetRelativePath($repository, [IO.Path]::GetFullPath($AssemblyPath))
if ([IO.Path]::IsPathRooted($assemblyRelative) -or $assemblyRelative -eq '..' -or $assemblyRelative.StartsWith('..'+[IO.Path]::DirectorySeparatorChar)) { throw 'Verification assembly must be inside this checkout.' }
$receiptBase = Join-Path $repository ('artifacts/pilot-storage-launch-' + [Guid]::NewGuid().ToString('N'))
$stdoutPath = $receiptBase+'.stdout.json'
$stderrPath = $receiptBase+'.stderr.txt'
$receiptPath = $receiptBase+'.receipt.json'
$outputFiles = @()
$process = [Diagnostics.Process]::new()
$exitCode, $reason, $ownedPid, $startedUtc = 2, $null, $null, $null
$cleanupConfirmed = $false
try {
    # CreateNew prevents overwriting a prior launch record, even on collision.
    foreach ($path in @($stdoutPath,$stderrPath,$receiptPath)) { $outputFiles += [IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read) }
    $info = [Diagnostics.ProcessStartInfo]::new($Sdk)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true; $info.WorkingDirectory=$repository
    $info.Environment['DOTNET_ROOT'] = Split-Path -Parent $Sdk
    $info.Environment['DOTNET_ROOT_X64'] = Split-Path -Parent $Sdk
    foreach ($argument in $launch.Arguments) { $info.ArgumentList.Add($argument) }
    $process.StartInfo = $info
    if (-not $process.Start()) { throw 'Storage helper did not start.' }
    $ownedPid=$process.Id; $startedUtc=$process.StartTime.ToUniversalTime().ToString('o')
    $ownedHandle=$process.SafeHandle # Pin this exact Windows process object, not a reusable PID.
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $streams=@(
        @{Stream=$process.StandardOutput.BaseStream;Buffer=[byte[]]::new(4096);File=$outputFiles[0];Read=$null;Done=$false},
        @{Stream=$process.StandardError.BaseStream;Buffer=[byte[]]::new(4096);File=$outputFiles[1];Read=$null;Done=$false}
    )
    $total=0
    while (-not $process.HasExited -or -not $streams[0].Done -or -not $streams[1].Done) {
        if ($clock.ElapsedMilliseconds -ge 55000) { throw 'Storage helper deadline exceeded.' }
        foreach ($stream in $streams) {
            if ($stream.Done) { continue }
            if ($null -eq $stream.Read) { $stream.Read=$stream.Stream.ReadAsync($stream.Buffer,0,$stream.Buffer.Length) }
            if ($stream.Read.IsCompleted) {
                $count=$stream.Read.GetAwaiter().GetResult(); $stream.Read=$null
                if ($count -eq 0) { $stream.Done=$true; continue }
                $keep=[Math]::Min($count,131072-$total)
                if ($keep -gt 0) { $stream.File.Write($stream.Buffer,0,$keep); $total+=$keep }
                if ($keep -ne $count) { throw 'Storage helper output exceeds128KiB.' }
            }
        }
        Start-Sleep -Milliseconds 10
    }
    $process.WaitForExit(); $exitCode=$process.ExitCode; $cleanupConfirmed=$true
} catch { $reason=$_.Exception.Message; $exitCode=2 }
finally {
    if ($null -ne $ownedPid -and -not $process.HasExited) {
        try { $process.Kill(); $cleanupConfirmed=$process.WaitForExit(5000) } catch { $cleanupConfirmed=$false }
    }
    if ($outputFiles.Count -eq 3) {
        $receipt=[ordered]@{Arguments=$launch.Arguments;Executable=$Sdk;OwnedPid=$ownedPid;StartedUtc=$startedUtc;ExitCode=$exitCode;CleanupConfirmed=$cleanupConfirmed;Error=$reason;Stdout=$stdoutPath;Stderr=$stderrPath;Effects=$launch.EffectsOnExecution}
        $bytes=[Text.Encoding]::UTF8.GetBytes(($receipt|ConvertTo-Json -Depth 5))
        $outputFiles[2].Write($bytes,0,$bytes.Length)
    }
    foreach ($file in $outputFiles) { $file.Dispose() }
    $process.Dispose()
}
if ($reason -or -not $cleanupConfirmed) {
    [pscustomobject]@{kind='storage-launch-incomplete';error=$reason;cleanup_confirmed=$cleanupConfirmed;receipt=$receiptPath}|ConvertTo-Json
    exit 2
}
# Emit the complete child JSON only after its normal bounded exit; retain raw bytes separately.
[IO.File]::ReadAllText($stdoutPath)
exit $exitCode
