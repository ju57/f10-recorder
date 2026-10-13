$ErrorActionPreference = 'Stop'
$sdkVersion = '10.0.401'
$sdkUrl = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip'
$sdkSha512 = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'
$directory = Join-Path $PSScriptRoot '.tools'
$executable = Join-Path $directory 'dotnet\dotnet.exe'
if (Test-Path -LiteralPath $executable) { if ((& $executable --version) -eq $sdkVersion) { Write-Host "Workspace SDK $sdkVersion already available."; return } }
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$archive = Join-Path $directory 'dotnet-sdk.zip'
Invoke-WebRequest -Uri $sdkUrl -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $sdkSha512) { throw 'SDK SHA-512 verification failed.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $directory 'dotnet') -Force
Write-Host "Workspace SDK ready. Run .\build-avalonia.ps1 to restore and build Avalonia."
