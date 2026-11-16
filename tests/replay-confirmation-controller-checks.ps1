param([switch]$Plan,[Parameter(Mandatory)][string]$OutputFile)
$ErrorActionPreference='Stop'
if($Plan){'Pure generated controller/control-receipt facts; no process launch, native inventory, media, UI, audio or devices. One PowerShell; <=30s; <=1MiB fresh ignored report.';return}
if(Test-Path -LiteralPath $OutputFile){throw 'Preserve prior check report'}
$watch=[Diagnostics.Stopwatch]::StartNew()
$runtime=@('D:\dummy\dotnet.exe');$engine=@('D:\dummy\ffmpeg.exe');$consoleImage='D:\dummy\conhost.exe'
. (Join-Path $PSScriptRoot 'confirmation-controller/ProcessController.Common.ps1')
. (Join-Path $PSScriptRoot 'confirmation-controller/ProcessController.CreatorProof.ps1')
. (Join-Path $PSScriptRoot 'confirmation-controller/ProcessController.Arguments.ps1')
$passed=[Collections.Generic.List[string]]::new()
function Need([bool]$Value,[string]$Name){if(-not$Value){throw $Name};$passed.Add($Name)}
function Rejected([scriptblock]$Action){try{&$Action|Out-Null;return $false}catch{return $true}}
$birth=[DateTime]::SpecifyKind([DateTime]'2026-11-14T12:00:00',[DateTimeKind]::Utc)
$fact=[pscustomobject]@{ProcessId=42;CreationDate=$birth.AddMilliseconds(1);ExecutablePath=$engine[0]}
Need (Test-LookupAbsenceSnapshot $fact $birth $true 'Process.GetProcessById(Int32)' 'System.ArgumentException') 'typed-local-lookup-absence-accepted'
Need (-not(Test-LookupAbsenceSnapshot $fact $birth $false 'Process.GetProcessById(Int32)' 'System.ArgumentException')) 'dead-parent-rejected'
Need (-not(Test-LookupAbsenceSnapshot $fact $birth $true 'other' 'System.ArgumentException')) 'wrong-call-rejected'
Need (-not(Test-LookupAbsenceSnapshot $fact $birth $true 'Process.GetProcessById(Int32)' 'System.InvalidOperationException')) 'wrong-exception-rejected'
$fact.CreationDate=$birth.AddSeconds(-1)
Need (-not(Test-LookupAbsenceSnapshot $fact $birth $true 'Process.GetProcessById(Int32)' 'System.ArgumentException')) 'old-birth-rejected'
$fact.CreationDate=$birth.AddMilliseconds(1);$fact.ExecutablePath='D:\unknown.exe'
Need (-not(Test-LookupAbsenceSnapshot $fact $birth $true 'Process.GetProcessById(Int32)' 'System.ArgumentException')) 'unknown-image-rejected'
$fact.ExecutablePath=''
Need (-not(Test-LookupAbsenceSnapshot $fact $birth $true 'Process.GetProcessById(Int32)' 'System.ArgumentException')) 'blank-image-not-pid-authority'
foreach($name in @('version','nonzero','timeout','output-limit')){
 $childExit=if($name-eq'nonzero'){1}else{0}
 $args=switch($name){'version'{@('-version')};'nonzero'{@('-this-option-is-not-real')};'timeout'{@('-hide_banner','-nostdin','-re','-f','lavfi','-i','anullsrc','-t','10','-f','null','-')};'output-limit'{@('-version')}}
 $receipt=[pscustomobject]@{ProcessId=42;ProcessStartedUtc=$birth;ExitCode=$childExit;ProducerAbsent=$true;ActualExitObserved=$true;ResourcesFinal=$true;ResourceError=$null;
  CapturedBytes=0;Success=($name-eq'version');Failure=$(switch($name){'version'{$null};'nonzero'{'exit-nonzero'};'timeout'{'timeout'};'output-limit'{'output-limit'}});
  CaptureComplete=$true;TimedOut=($name-eq'timeout');OutputLimitExceeded=($name-eq'output-limit')}
 $doc=[pscustomobject]@{receipt=$receipt;arguments=@($args)}
 Need (Test-ControlReceipt $doc $name 42 $birth $childExit) ($name+'-exact-creator-outcome')
 Need (-not(Test-ControlReceipt $doc $name 43 $birth $childExit)) ($name+'-wrong-pid-rejected')
 $receipt.ProducerAbsent=$false
 Need (Rejected {Test-ControlReceipt $doc $name 42 $birth $childExit}) ($name+'-incomplete-closure-rejected')
 $receipt.ProducerAbsent=$true;$doc.arguments=@('unreviewed')
 Need (Rejected {Test-ControlReceipt $doc $name 42 $birth $childExit}) ($name+'-changed-arguments-rejected')
}
$a=[pscustomobject]@{creatorProofPolicy='qualification-child-creator-v1';creatorProofMilliseconds=130000};$p=[pscustomobject]@{ReplayMinutes=1}
Need (Test-CreatorProofMode) 'retained-fixed-creator-window'
$a.creatorProofMilliseconds=1200000
Need (-not(Test-CreatorProofMode)) 'old30minute-creator-window-rejected'
$retained=$true;$p=[pscustomobject]@{Root='D:\nonexistent-dummy-root'}
Need ($null-eq(Get-CreatorCase)) 'retained-creator-needs-admitted-plan'
Need (Same-ControllerArguments (Split-ControllerArguments '"D:\with space\ffmpeg.exe" -i "D:\in path\input.ts" ""') @('D:\with space\ffmpeg.exe','-i','D:\in path\input.ts','')) 'windows-quoted-path-and-empty-argument'
Need (Same-ControllerArguments (Split-ControllerArguments 'tool one\ two') @('tool','one\','two')) 'windows-backslash-before-delimiter'
Need (Same-ControllerArguments (Split-ControllerArguments 'tool "a\\\"b"') @('tool','a\"b')) 'windows-backslashes-before-literal-quote'
Need (Rejected {Split-ControllerArguments 'tool "unclosed'}) 'unclosed-quote-rejected'
Need (-not(Same-ControllerArguments @('a','b') @('a','c'))) 'changed-child-argument-rejected'
Need (-not(Same-ControllerArguments @('a') @('a',''))) 'extra-empty-child-argument-rejected'
Need ($watch.Elapsed.TotalSeconds-lt30) 'pure-check-time-bound'
$result=[ordered]@{result='passed';count=$passed.Count;passed=$passed.ToArray();seconds=$watch.Elapsed.TotalSeconds;processesLaunched=0;mediaRead=$false}
$bytes=[Text.Encoding]::UTF8.GetBytes(($result|ConvertTo-Json -Depth 5));if($bytes.Length-gt1048576){throw 'Report1MiB bound'}
$stream=[IO.FileStream]::new([IO.Path]::GetFullPath($OutputFile),'CreateNew','Write','Read');try{$stream.Write($bytes)}finally{$stream.Dispose()}
"Passed $($passed.Count) pure controller checks"
