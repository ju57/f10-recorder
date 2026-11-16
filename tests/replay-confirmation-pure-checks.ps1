param(
    [switch]$Plan,
    [Parameter(Mandatory)][string]$Root,
    [ValidateSet("Standalone", "Integrated")][string]$Scope = "Standalone",
    [string]$Sdk,
    [string]$AssemblyPath
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Root = [IO.Path]::GetFullPath($Root)
$artifactPrefix = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $Root.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fresh output below this checkout artifacts directory required.' }
if ($Scope -eq 'Integrated') {
    if (-not $Sdk -or -not $AssemblyPath) { throw 'Integrated scope requires a pinned SDK and prebuilt matching WorkerChecks assembly.' }
    $Sdk = [IO.Path]::GetFullPath($Sdk)
    $AssemblyPath = [IO.Path]::GetFullPath($AssemblyPath)
}
$routes = @(
    @{ name='controller'; script='replay-confirmation-controller-checks.ps1'; parameters=@('-OutputFile', (Join-Path $Root 'controller.json')); ram=268435456 },
    @{ name='release-v9'; script='replay-confirmation-release-checks.ps1'; parameters=@('-Root', (Join-Path $Root 'release-v9')); ram=268435456 },
    @{ name='release-v10'; script='replay-confirmation-v10-release-checks.ps1'; parameters=@('-Root', (Join-Path $Root 'release-v10')); ram=268435456 }
)
if ($Scope -eq 'Integrated') { $routes += @(
    @{ name='presentation'; selector='--replay-presentation-offline-checks'; output=(Join-Path $Root 'presentation.json'); ram=268435456 },
    @{ name='protection'; selector='--replay-presentation-protection-checks'; output=(Join-Path $Root 'protection.json'); ram=268435456 },
    @{ name='metadata'; selector='--replay-presentation-metadata-checks'; output=(Join-Path $Root 'metadata'); ram=536870912 },
    @{ name='same-C00'; selector='--replay-same-C00-offline-checks'; output=(Join-Path $Root 'same-C00.json'); ram=268435456 }
) }
$description = [ordered]@{
    kind='generated-confirmation-regressions-v1'; scope=$Scope; deferredManagedIntegration=($Scope -eq 'Standalone'); routes=@($routes | ForEach-Object { $_.name })
    processes='One wrapper and at most one owned PowerShell or dotnet child at a time; no child of a check helper.'
    effects='Generated JSON/CSV, memory PCM and raster fixtures only. No media payload, capture, GPU/device, audio playback, native file-owner fixtures, profiles, account catalogs or execution release.'
    perRouteSeconds=40; wholeSeconds=330; outputBytes=8388608; outputFiles=256; logBytesPerStream=1048576
    memory='256MiB per child; generated metadata child512MiB. Sampled limits, not OS quotas.'
    root=$Root; sdk=$Sdk; assembly=$AssemblyPath
}
if ($Plan) { $description | ConvertTo-Json -Depth 5; return }
if (Test-Path -LiteralPath $Root) { throw 'Preserve prior suite output.' }
if ($Scope -eq 'Integrated') { foreach ($path in @($Sdk, $AssemblyPath)) { if (-not [IO.File]::Exists($path)) { throw "Required prebuilt runtime input absent: $path" } } }
# Reject an existing junction in the output path before any fixture writes.
$parent = [IO.DirectoryInfo]::new((Split-Path -Parent $Root))
while ($parent -and $parent.FullName.StartsWith($repository, [StringComparison]::OrdinalIgnoreCase)) {
    if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse-point output parent rejected.' }
    if ($parent.FullName -eq $repository) { break }
    $parent = $parent.Parent
}
[void][IO.Directory]::CreateDirectory($Root)
$assemblyHash = if ($Scope -eq 'Integrated') { (Get-FileHash -LiteralPath $AssemblyPath -Algorithm SHA256).Hash } else { $null }
$powerShell = (Get-Process -Id $PID).Path
$whole = [Diagnostics.Stopwatch]::StartNew()
$results = [Collections.Generic.List[object]]::new()
function Assert-OutputBound {
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse)
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    if ($files.Count -gt 256 -or $bytes -gt 8388608) { throw 'Generated suite output exceeds256files/8MiB.' }
}
function Invoke-PureRoute($route) {
    if ($whole.Elapsed.TotalSeconds -ge 325) { throw 'Pure suite deadline leaves no cleanup reserve.' }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.WorkingDirectory=$repository
    if ($route.script) {
        $start.FileName=$powerShell
        foreach ($value in @('-NoProfile','-NonInteractive','-File',(Join-Path $PSScriptRoot $route.script)) + $route.parameters) { $start.ArgumentList.Add($value) }
    } else {
        $start.FileName=$Sdk
        foreach ($value in @($AssemblyPath,$route.selector,$route.output)) { $start.ArgumentList.Add($value) }
    }
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
    $start.Environment['DOTNET_NOLOGO']='1'
    $stdoutPath=Join-Path $Root ($route.name + '.stdout.log')
    $stderrPath=Join-Path $Root ($route.name + '.stderr.log')
    $stdout=$null; $stderr=$null; $process=$null; $outCopy=$null; $errCopy=$null
    $watch=[Diagnostics.Stopwatch]::StartNew(); $peak=0L; $failure=$null; $exitCode=$null; $absent=$false
    try {
        $stdout=[IO.FileStream]::new($stdoutPath,'CreateNew','Write',[IO.FileShare]::ReadWrite)
        $stderr=[IO.FileStream]::new($stderrPath,'CreateNew','Write',[IO.FileShare]::ReadWrite)
        $process=[Diagnostics.Process]::Start($start)
        $outCopy=$process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errCopy=$process.StandardError.BaseStream.CopyToAsync($stderr)
        while (-not $process.WaitForExit(100)) {
            $process.Refresh(); $peak=[Math]::Max($peak,$process.PeakWorkingSet64)
            if ($watch.Elapsed.TotalSeconds -ge 35 -or $whole.Elapsed.TotalSeconds -ge 325) { throw 'Pure route deadline exceeded.' }
            if ($peak -gt $route.ram) { throw 'Pure route sampled memory budget exceeded.' }
            if ($stdout.Length -gt 1048576 -or $stderr.Length -gt 1048576) { throw 'Pure route log bound exceeded.' }
            Assert-OutputBound
        }
        $exitCode=$process.ExitCode; $absent=$true
        if ($exitCode -ne 0) { throw "Pure route exited $exitCode; see $stderrPath" }
    } catch { $failure=$_.Exception.Message }
    finally {
        if ($process) {
            try {
                if (-not $process.HasExited) { $process.Kill() }
                $remaining = [Math]::Max(0, [Math]::Min(3000, [Math]::Min(40000 - [int]$watch.ElapsedMilliseconds, 330000 - [int]$whole.ElapsedMilliseconds)))
                if (-not $process.WaitForExit($remaining)) { throw 'Owned pure-check process absence unconfirmed.' }
                $absent=$true
                if ($null -eq $exitCode) { $exitCode=$process.ExitCode }
            } catch { $failure="$failure Cleanup: $($_.Exception.Message)" }
        }
        foreach ($copy in @($outCopy,$errCopy)) {
            if ($copy) {
                try {
                    $remaining = [Math]::Max(0, [Math]::Min(1000, [Math]::Min(40000 - [int]$watch.ElapsedMilliseconds, 330000 - [int]$whole.ElapsedMilliseconds)))
                    if (-not $copy.Wait($remaining)) { throw 'Pure route log drain timed out.' }
                }
                catch { $failure="$failure Log drain: $($_.Exception.Message)" }
            }
        }
        if ($stdout) { $stdout.Dispose() }; if ($stderr) { $stderr.Dispose() }
        if ($process) { $process.Dispose() }
    }
    if ((Get-Item -LiteralPath $stdoutPath).Length -gt 1048576 -or (Get-Item -LiteralPath $stderrPath).Length -gt 1048576) { $failure="$failure Pure route final log bound exceeded." }
    if ($watch.Elapsed.TotalSeconds -ge 40 -or $whole.Elapsed.TotalSeconds -ge 330) { $failure="$failure Cleanup-inclusive pure route/suite deadline exceeded." }
    Assert-OutputBound
    $row=[ordered]@{name=$route.name;exitCode=$exitCode;absent=$absent;seconds=$watch.Elapsed.TotalSeconds;peakWorkingSet=$peak;failure=$failure}
    $results.Add($row)
    [IO.File]::WriteAllText((Join-Path $Root 'suite.json'), ([ordered]@{kind=$description.kind;scope=$Scope;deferredManagedIntegration=$description.deferredManagedIntegration;assemblySha256=$assemblyHash;empiricalAcceptance=$false;result=$(if($failure){'failed'}else{'running'});routes=$results.ToArray()} | ConvertTo-Json -Depth 8))
    if ($failure) { throw "$($route.name): $failure" }
    Write-Output "Passed pure route: $($route.name)"
}
foreach ($route in $routes) { Invoke-PureRoute $route }
if ($whole.Elapsed.TotalSeconds -ge 330) { throw 'Cleanup-inclusive pure suite deadline exceeded.' }
if ($Scope -eq 'Integrated' -and (Get-FileHash -LiteralPath $AssemblyPath -Algorithm SHA256).Hash -cne $assemblyHash) { throw 'Prebuilt helper changed during suite.' }
Assert-OutputBound
[IO.File]::WriteAllText((Join-Path $Root 'suite.json'), ([ordered]@{kind=$description.kind;scope=$Scope;deferredManagedIntegration=$description.deferredManagedIntegration;result='passed';assemblySha256=$assemblyHash;empiricalAcceptance=$false;seconds=$whole.Elapsed.TotalSeconds;routes=$results.ToArray()} | ConvertTo-Json -Depth 8))
Write-Output "Passed $($routes.Count) generated confirmation regression routes in $Scope scope; no empirical media acceptance."
