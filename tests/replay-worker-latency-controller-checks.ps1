param([switch]$Plan,[string]$Controller=(Join-Path $PSScriptRoot 'replay-worker-latency-pilot.ps1'),[switch]$KernelIdentity)
$ErrorActionPreference='Stop'
# Resource APIs are independently exercised by the read-only sampler probe.
# Ownership cases retain only fake objects and must never call a native API.
function Get-ReplayFinalIo {param($Process) [pscustomobject]@{ReadBytes=0;WriteBytes=0;OtherBytes=0}}
function Get-ReplayHeldImage {param($Process) if($Process.PSObject.Properties.Name -contains 'KernelImage'){return $Process.KernelImage};return $Process.MainModule.FileName}
if($Plan){
 'Twenty-two owning-controller checks with injected parent/child objects and CIM snapshots. No process, native, capture, audio, UI, settings or filesystem mutation.'
 return
}
$source=$Controller
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Controller parse failed'}
$functions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Observe-Children'},$true))
if($functions.Count -ne 1){throw 'Exact owning observer required'}
. ([ScriptBlock]::Create($functions[0].Extent.Text))
$passed=0
function Get-CimInstance {
 param($ClassName,$Filter,$Property)
 $script:cimCalls++
 if($script:mode -ceq 'dies-during-query'){$script:owned.HasExited=$true}
 if($script:mode -ceq 'alive-empty'){return}
 [pscustomobject]@{ProcessId=[int]::MaxValue;CreationDate=$ownedStart.AddSeconds(1);ExecutablePath='unrelated-new-parent-child.exe';WorkingSetSize=1}
}
foreach($case in @('already-exited-finally','dies-during-query','alive-empty')){
 $mode=$case;$cimCalls=0;$queryMaximumMs=0.0
 $owned=[pscustomobject]@{Id=[int]::MaxValue;HasExited=($case -ceq 'already-exited-finally')}
 $ownedStart=[DateTime]::UtcNow.AddSeconds(-1)
 $tracked=@{};$children=@{};$rawChildren=[Collections.Generic.List[object]]::new()
 Observe-Children
 $expected=if($case -ceq 'already-exited-finally'){0}else{1}
 if($cimCalls -ne $expected -or $tracked.Count -ne 0 -or $children.Count -ne 0 -or $rawChildren.Count -ne 0){throw "Unsafe dead-parent admission: $case"}
 $passed++
}
$owned=$null;$cimCalls=0;Observe-Children
if($cimCalls -ne 0){throw 'Null parent queried'}
$passed++
$closures=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.ForEachStatementAst] -and $node.Extent.Text.StartsWith('foreach($entry in $tracked.Values)')},$true))
if($closures.Count -ne 1){throw 'Exact owning child closure required'}
function Remaining {5000}
foreach($force in @($false,$true)){
 $failure=$null;$childClosure=@();$killCount=0;$disposeCount=0
 $fake=[pscustomobject]@{HasExited=(-not $force);ExitCode=0}
 $fake|Add-Member -MemberType ScriptMethod -Name Kill -Value {$script:killCount++;$this.HasExited=$true}
 $fake|Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {param($milliseconds) return $this.HasExited}
 $fake|Add-Member -MemberType ScriptMethod -Name Dispose -Value {$script:disposeCount++}
 $tracked=@{one=[pscustomobject]@{process=$fake;pid=[int]::MaxValue;startedUtc=[DateTime]::UtcNow}}
 . ([ScriptBlock]::Create($closures[0].Extent.Text))
 if($killCount -ne [int]$force -or $disposeCount -ne 1 -or $childClosure.Count -ne 1 -or $childClosure[0].forced -ne $force -or (-not [string]::IsNullOrEmpty($failure)) -ne $force){throw 'Owning closure confused forced and normal completion'}
 $passed++
}
# Replay the image-disappears-at-exit race through the actual owning observer.
# The fake retained objects prevent any numeric PID from being opened.
function Get-CimInstance { param($ClassName,$Filter,$Property) $script:rows }
$cases=@('verified-media-exit','verified-console-exit','unverified-exit','live-unknown','different-birth','ram-cap','current-count-cap','image-exit-race','image-exit-no-proof','cim-only-exit','console-nonblank-exit','contradictory-exit','empty-image-exit','empty-image-no-proof','live-loader-null-once','live-loader-null-persistent')
if($KernelIdentity -or $functions[0].Extent.Text.Contains('Get-ReplayHeldImage')){$cases+=@('live-loader-empty-no-cim','live-loader-empty-with-cim','kernel-image-unavailable')}
foreach($case in $cases){
 $owned=[pscustomobject]@{Id=[int]::MaxValue;HasExited=$false}
 $ownedStart=[DateTime]::UtcNow.AddSeconds(-10);$queryMaximumMs=0.0
 $consoleImage='C:\Windows\System32\conhost.exe';$allowedImages=@('C:\pinned\dotnet.exe','C:\pinned\ffmpeg.exe')
 $tracked=@{};$children=@{};$rawChildren=[Collections.Generic.List[object]]::new();$rows=@()
 $count=if($case -ceq 'current-count-cap'){5}else{1}
 for($i=0;$i -lt $count;$i++){
  $created=$ownedStart.AddSeconds(1+$i);$id=[int]::MaxValue-$i-1;$key="$id/$($created.Ticks)"
  $image=if($case -in @('verified-console-exit','console-nonblank-exit')){$consoleImage}else{$allowedImages[1]}
  $exited=$case -in @('verified-media-exit','verified-console-exit','unverified-exit','different-birth','cim-only-exit','console-nonblank-exit','contradictory-exit')
  $heldImage=if($case -ceq 'live-unknown'){'C:\unapproved.exe'}else{$image}
  $tracked[$key]=[pscustomobject]@{process=[pscustomobject]@{HasExited=$exited;MainModule=[pscustomobject]@{FileName=$heldImage}}}
  if($case -like 'live-loader-empty-*'){$tracked[$key].process.MainModule.FileName=$null;$tracked[$key].process|Add-Member -NotePropertyName KernelImage -NotePropertyValue $image}
  if($case -ceq 'kernel-image-unavailable'){$tracked[$key].process|Add-Member -NotePropertyName KernelImage -NotePropertyValue $null}
  if($case -in @('image-exit-race','image-exit-no-proof')){
   $fake=[pscustomobject]@{HasExited=$false}
   $fake|Add-Member -MemberType ScriptProperty -Name MainModule -Value {$this.HasExited=$true;throw 'Injected exit during MainModule read'}
   $tracked[$key].process=$fake
  }
  if($case -in @('empty-image-exit','empty-image-no-proof')){
   $fake=[pscustomobject]@{HasExited=$false}
   $fake|Add-Member -MemberType ScriptProperty -Name MainModule -Value {$this.HasExited=$true;return $null}
   $tracked[$key].process=$fake
  }
  if($case -ceq 'live-loader-null-once'){
   $script:imageReads=0
   $fake=[pscustomobject]@{HasExited=$false;ExpectedImage=$image}
   $fake|Add-Member -MemberType ScriptProperty -Name MainModule -Value {$script:imageReads++;[pscustomobject]@{FileName=if($script:imageReads -eq 1){$null}else{$this.ExpectedImage}}}
   $tracked[$key].process=$fake
  }
  if($case -ceq 'live-loader-null-persistent'){
   $fake=[pscustomobject]@{HasExited=$false}
   $fake|Add-Member -MemberType ScriptProperty -Name MainModule -Value {[pscustomobject]@{FileName=$null}}
   $tracked[$key].process=$fake
  }
  if($case -notin @('unverified-exit','live-unknown','current-count-cap','image-exit-no-proof','empty-image-no-proof','live-loader-empty-no-cim','live-loader-empty-with-cim','kernel-image-unavailable','live-loader-null-once','live-loader-null-persistent')){
   $rememberedKey=if($case -ceq 'different-birth'){"$id/$($created.AddTicks(-10).Ticks)"}else{$key}
   $children[$rememberedKey]=[ordered]@{pid=$id;startedUtc=$created;path=$image;cimPath=$image;heldImageObserved=($case -cne 'cim-only-exit');consoleSupport=($image -ceq $consoleImage);peakWorkingSet=1L}
  }
  $ram=if($case -ceq 'ram-cap'){2147483649L}else{1L}
  $cimImage=if($case -in @('console-nonblank-exit','live-loader-empty-with-cim')){$image}elseif($case -ceq 'contradictory-exit'){$allowedImages[0]}else{$null}
  $rows+= [pscustomobject]@{ProcessId=$id;CreationDate=$created;ExecutablePath=$cimImage;WorkingSetSize=$ram}
 }
 $rejected=$false
 try{Observe-Children}catch{if($_.ToString() -notlike '*Current child image/resource/count cap*'){throw};$rejected=$true}
 $shouldReject=$case -notin @('verified-media-exit','verified-console-exit','image-exit-race','console-nonblank-exit','empty-image-exit','live-loader-empty-no-cim','live-loader-empty-with-cim','live-loader-null-once')
 if($rejected -ne $shouldReject){throw "Child image/exit boundary failed: $case, rejected=$rejected"}
 if($case -ceq 'live-loader-null-once' -and ($script:imageReads -lt 2 -or $children[$key].path -cne $allowedImages[1] -or -not $children[$key].heldImageObserved)){throw 'Live child image retry did not establish held-image proof'}
 $passed++
}
"Passed $passed owning controller boundary checks."
