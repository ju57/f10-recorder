param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$DataDirectory,
    [string]$ExpectedAssemblySha256,
    [switch]$Run
)
$ErrorActionPreference = 'Stop'
$sdk = Join-Path (Split-Path -Parent $PSScriptRoot) '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$assembly = [IO.Path]::GetFullPath($AssemblyPath)
$data = [IO.Path]::GetFullPath($DataDirectory)
$launch = [pscustomobject]@{
    executable=$sdk; arguments=@($assembly,'--replay-manifest-stream',$data)
    effects='Tiny native CSV fixtures and concurrent metadata tasks; under2MiB retained/about128MB cumulative writes. No engine, child, capture, audio, GPU, UI, shortcut, profile or startup action.'
}
if (-not $Run) { return $launch }
if (-not $ExpectedAssemblySha256 -or (Get-FileHash -LiteralPath $assembly).Hash -ne $ExpectedAssemblySha256) { throw 'Reviewed helper identity changed.' }
if (Test-Path -LiteralPath $data) { throw 'Manifest evidence root must be fresh.' }
$env:DOTNET_ROOT = Split-Path -Parent $sdk
& $sdk @($launch.arguments)
if ($LASTEXITCODE -ne 0) { throw 'Manifest checks failed; preserve evidence.' }
