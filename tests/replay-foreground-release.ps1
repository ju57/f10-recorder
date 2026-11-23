param(
 [Parameter(Mandatory)][string]$PlanFile,
 [Parameter(Mandatory)][string]$ExpectedPlanSha256,
 [Parameter(Mandatory)][string]$Label
)
# Invoke only after every Computer Use call/permission prompt has returned.
# This writes the start signal; do not make further UI calls during capture.
$ErrorActionPreference='Stop'
function Read-Bounded([string]$Path,[int]$Maximum){
 $reader=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{if($reader.Length -le 0 -or $reader.Length -gt $Maximum){throw 'Release controller input exceeds its bound'};$buffer=[byte[]]::new([int]$reader.Length);$reader.ReadExactly($buffer);return ,$buffer}finally{$reader.Dispose()}
}
$raw=Read-Bounded (Resolve-Path -LiteralPath $PlanFile).Path 65536
if($raw.Length -gt 65536 -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw)) -cne $ExpectedPlanSha256){throw 'Exact reviewed recording plan required'}
$admission=[Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json
$p=$admission.plan
if(-not $p.CompositeQualification -or -not $p.CompositeManualForegroundHandoff -or -not $p.CompositeExplicitForegroundRelease -or $p.Order -cnotcontains $Label){throw 'Explicit composite release admission required'}
if($p.Files.PSObject.Properties.Name -notcontains $PSCommandPath -or (Get-FileHash -LiteralPath $PSCommandPath).Hash -cne $p.Files.($PSCommandPath) -or [DateTime]::UtcNow -ge ([DateTimeOffset]$admission.expiresUtc).UtcDateTime){throw 'Bound current release controller required'}
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))+[IO.Path]::DirectorySeparatorChar
$root=[IO.Path]::GetFullPath($p.Root)
if(-not $root.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase) -or $Label -cnotmatch '^[TH]0[123]$'){throw 'Isolated known-case root required'}
$case=Join-Path $root $Label
if((Test-Path (Join-Path $case capture-arguments.json)) -or (Test-Path (Join-Path $case foreground-handoff-complete.json))){throw 'Capture already admitted; no new release'}
$readyBytes=Read-Bounded (Join-Path $case foreground-handoff-ready.json) 4096
if($readyBytes.Length -gt 4096){throw 'Handoff challenge too large'}
$ready=[Text.Encoding]::UTF8.GetString($readyBytes)|ConvertFrom-Json
$r=$ready.release
if($ready.label -cne $Label -or $ready.captureStarted -ne $false -or $r.Label -cne $Label -or $r.Pid -ne $ready.pid -or $r.Hwnd -ne $ready.hwnd -or $r.Pid -le 0 -or $r.Hwnd -eq 0 -or $r.Nonce -cnotmatch '^[0-9a-f]{32}$'){throw 'Invalid explicit handoff challenge'}
$bytes=[Text.Encoding]::UTF8.GetBytes(($r|ConvertTo-Json -Compress))
if($bytes.Length -gt 4096){throw 'Release response too large'}
$temporary=Join-Path $case ('.foreground-release-'+[Guid]::NewGuid().ToString('N')+'.tmp')
$final=Join-Path $case foreground-handoff-release.json
$created=$false
try{
 $writer=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 $created=$true
 try{$writer.Write($bytes);$writer.Flush($true)}finally{$writer.Dispose()}
 [IO.File]::Move($temporary,$final) # Atomic publication; never overwrite/reuse a release.
}finally{if($created -and (Test-Path -LiteralPath $temporary)){[IO.File]::Delete($temporary)}}
[pscustomobject]@{label=$Label;pid=$r.Pid;hwnd=$r.Hwnd;releasePublished=$true;scope='Controller signal after UI calls; collector still verifies current native foreground before capture'}|ConvertTo-Json
