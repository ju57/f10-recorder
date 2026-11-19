# Retrospective closed-control attribution only. Typed local lookup absence is
# retained separately. This file grants no handle/PID adoption or stop authority.
function Write-CollectorIdentityBinding {
 $file=Join-Path $directory 'collector-identity.json'
 $value=[ordered]@{kind='held-collector-launch-v1';pid=$owned.Id;birth=$birth.ToString('o');root=$p.Root;planPath=$planPath;planSha256=$hash;
  helperPath=$helper[0];helperSha256=$p.Files.($helper[0]);runtimePath=$runtime[0];arguments=$controllerArguments;recordedUtc=[DateTime]::UtcNow.ToString('o')}
 $bytes=[Text.Encoding]::UTF8.GetBytes(($value|ConvertTo-Json -Depth 4))
 if($bytes.Length-gt8192){throw 'Collector binding8KiB bound'}
 $stream=[IO.FileStream]::new($file,'CreateNew','Write','Read')
 try{$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
}
function Test-UnheldControlContext {
 if(-not(Test-ControllerParentAlive)-or$released.Count-ne0){return $false}
 # A fixture may already be disarmed while its last short control exits. No
 # release/capture is allowed, and the complete control-admission prefix below
 # must still bind this exact process; a fixture name alone is never evidence.
 foreach($label in $p.Order){foreach($name in @('capture-arguments.json','foreground-handoff-release.json','foreground-handoff-complete.json')){
  if(Test-Path -LiteralPath (Join-Path $p.Root "$label/$name")){return $false}
 }}
 $file=Join-Path $directory 'collector-identity.json'
 if(-not(Test-Path -LiteralPath $file)){return $false}
 $b=[Text.Encoding]::UTF8.GetString((Read-Bounded $file 8192))|ConvertFrom-Json
 if($b.kind-cne'held-collector-launch-v1'-or$b.pid-ne$owned.Id-or([DateTimeOffset]$b.birth).UtcDateTime-ne$birth-or$b.root-cne$p.Root-or
  $b.planPath-cne$planPath-or$b.planSha256-cne$hash-or$b.helperPath-cne$helper[0]-or$b.helperSha256-cne$p.Files.($helper[0])-or$b.runtimePath-cne$runtime[0]){return $false}
 $boundArguments=@($b.arguments)
 if($boundArguments.Count-ne4-or$boundArguments[0]-cne$helper[0]-or$boundArguments[1]-cne'--replay-calibration-run'-or$boundArguments[2]-cne$planPath-or$boundArguments[3]-cne$hash){return $false}
 $admitted=Join-Path $p.Root admitted-plan.json
 if(-not(Test-Path -LiteralPath $admitted)){return $false}
 $doc=[Text.Encoding]::UTF8.GetString((Read-Bounded $admitted 65536))|ConvertFrom-Json
 if($doc.planHash-cne$hash-or$doc.plan.Root-cne$p.Root-or$doc.plan.Files.($helper[0])-cne$p.Files.($helper[0])){return $false}
 return Test-ControllerParentAlive
}
function Add-PendingUnheldControlImage([string]$Key,$Fact) {
 if($Fact.Name-cne'ffmpeg.exe'-or-not[string]::IsNullOrEmpty($Fact.ExecutablePath)-or$Fact.ProcessId-le0-or$Fact.ProcessId-gt[int]::MaxValue-or
  ([DateTimeOffset]$Fact.CreationDate).UtcDateTime-lt$birth-or-not(Test-UnheldControlContext)){throw 'Unheld control lacks current pre-release collector context'}
 if(-not$pendingControlImages.ContainsKey($Key)){$pendingControlImages[$Key]=[ordered]@{unheld=$true;fact=$Fact;started=(Get-ControllerPolicyMilliseconds)}}
}
function Test-ExitAtHandleException($Failure,[int]$ChildPid) {
 # GetProcessById succeeded. Only the runtime's specific exit-at-handle error
 # may wait for creator proof. Access, birth and generic handle errors stay fatal.
 $cause=$Failure.GetBaseException()
 $site=$cause.TargetSite
 $assembly=[Diagnostics.Process].Assembly
 if($cause.GetType() -ne [InvalidOperationException] -or !$site -or $site.DeclaringType.Assembly -ne $assembly){return $false}
 $knownSite=($site.DeclaringType -eq [Diagnostics.Process] -and $site.Name -ceq 'GetProcessHandle') -or
    ($site.DeclaringType.FullName -ceq 'System.Diagnostics.ProcessManager' -and $site.Name -ceq 'OpenProcess')
 if(!$knownSite){return $false}
 $messages=$assembly.GetType('System.SR').GetProperty('ProcessHasExited',[Reflection.BindingFlags]'NonPublic,Static')
 return ($null -ne $messages -and $cause.Message -ceq ($messages.GetValue($null) -f $ChildPid))
}

function Test-ControlExitAtHandle($Failure,$Fact) {
 if(!(Test-ExitAtHandleException $Failure $Fact.ProcessId)){return $false}
 if($Fact.ParentProcessId -ne $owned.Id -or $Fact.Name -cne 'ffmpeg.exe' -or $Fact.ExecutablePath -ine $engine[0] -or
    !$Fact.CommandLine -or !(Test-CreatorProofMode) -or !(Test-UnheldControlContext)){return $false}
 $argv=Split-ControllerArguments $Fact.CommandLine
 return ($argv.Count -ge 2 -and $argv[0] -ieq $engine[0])
}

function Read-UnheldControlImageProof($Pending) {
 if(-not(Test-UnheldControlContext)){throw 'Unheld control context changed before proof'}
 $path=Join-Path $p.Root child-admissions.jsonl
 if(-not(Test-Path -LiteralPath $path)){return $null}
 $bytes=Read-ControllerProofBytes $path
 if($null-eq$bytes-or$bytes.Length-eq0){return $null}
 $text=[Text.Encoding]::UTF8.GetString($bytes)
 if(-not$text.EndsWith("`n")){return $null}
 $rows=@($text.Split("`n")|Where-Object {$_}|ForEach-Object {$_|ConvertFrom-Json})
 $names=@('version','nonzero','timeout','output-limit')
 # Exact trusted helper prefix: collector then the four fixed engine controls.
 # One following, still-disarmed first fixture is permitted; no capture role.
 if($rows.Count-lt2-or$rows.Count-gt6){throw 'Unheld control admission prefix bound'}
 for($i=0;$i-lt$rows.Count;$i++){
  $role=if($i-eq0){'collector'}elseif($i-le4){'engine-control:'+$names[$i-1]}else{'fixture:'+$p.Order[0]}
  if($rows[$i].ordinal-ne([int]$p.RetainedChildren+1+$i)-or$rows[$i].role-cne$role){throw 'Unheld control admission prefix changed'}
 }
 $fact=$Pending.fact;$matches=@()
 for($i=0;$i-lt4;$i++){
  $name=$names[$i];$file=Join-Path $p.Root "controls/$name.json"
  if(-not(Test-Path -LiteralPath $file)){continue}
  $raw=Read-ControllerProofBytes $file;if($null-eq$raw-or$raw.Length-eq0){continue}
  try{$doc=[Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json}catch [ArgumentException]{continue}
  $r=$doc.receipt
  if($r.ProcessId-ne$fact.ProcessId){continue}
  $actualBirth=([DateTimeOffset]$r.ProcessStartedUtc).UtcDateTime
  if([Math]::Abs(($actualBirth-([DateTimeOffset]$fact.CreationDate).UtcDateTime).Ticks)-gt10-or$actualBirth-lt$birth){throw 'Unheld control creator birth mismatch'}
  if($rows.Count-le($i+1)-or([DateTimeOffset]$rows[0].utc).UtcDateTime-lt$birth-or
   ([DateTimeOffset]$rows[$i+1].utc).UtcDateTime-lt([DateTimeOffset]$rows[0].utc).UtcDateTime-or
   ([DateTimeOffset]$rows[$i+1].utc).UtcDateTime-gt$actualBirth){throw 'Unheld control was not admitted before its birth'}
  if(Test-ControlReceipt $doc $name ([int]$fact.ProcessId) $actualBirth $r.ExitCode){
   if($Pending.handleExit -and !(Same-ControllerArguments (Split-ControllerArguments $fact.CommandLine) (@($engine[0])+@($doc.arguments)))){
    throw 'Exited control command differs from creator receipt'
   }
   $matches+=@{path=$file;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw));kind=$name;unheld=$true;
    admissionOrdinal=$rows[$i+1].ordinal;creatorExitCode=$r.ExitCode;collectorBindingSha256=(Get-FileHash -LiteralPath (Join-Path $directory 'collector-identity.json')).Hash}
  }
 }
 if($matches.Count-gt1){throw 'Duplicate unheld control creator proof'}
 if($matches.Count-eq1){return $matches[0]};return $null
}
