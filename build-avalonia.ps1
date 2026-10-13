param([switch]$Publish, [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$sdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\home'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$project = Join-Path $PSScriptRoot 'projects\F10Recorder.Desktop\F10Recorder.Desktop.csproj'
$buildArgs = @('build', $project, '-c', 'Release', '-o', (Join-Path $PSScriptRoot 'bin\avalonia'), '-m:1', '--disable-build-servers', '-p:UseSharedCompilation=false')
if ($Publish) { $buildArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', (Join-Path $PSScriptRoot 'bin\avalonia-publish'), '-m:1', '--disable-build-servers', '-p:UseSharedCompilation=false') }
if ($NoRestore) { $buildArgs += '--no-restore' }
& $sdk @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Avalonia build failed.' }
