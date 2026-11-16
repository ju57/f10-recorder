# Retrospective identity proof only. Pending/closed rows never gain PID authority.
function Test-CreatorProofMode {
 $a.PSObject.Properties.Name -contains 'creatorProofPolicy' -and $a.creatorProofPolicy-ceq'qualification-child-creator-v1' -and $a.creatorProofMilliseconds-eq$(if($p.ReplayMinutes-eq30){1200000}else{130000})
}
function Get-CreatorCase {
 if($retained -and (Test-Path -LiteralPath (Join-Path $p.Root 'admitted-plan.json'))){return 'H03'}
 if(-not(Test-CreatorProofMode)-or-not(Test-Path -LiteralPath $a.watchStatusFile)){return $null}
 $s=Read-Handoff $a.watchStatusFile
 if($null-eq$s-or$p.Order-cnotcontains$s.label-or-not$released.Contains($s.label)){return $null}
 $cell=Join-Path $p.Root $s.label
 if(-not(Test-Path -LiteralPath (Join-Path $cell 'capture-arguments.json'))){return $null}
 return $s.label
}
function Add-PendingCreatorImage([string]$Key,$Fact,[bool]$Unheld){
 $label=Get-CreatorCase
 if(-not$label-or$Fact.Name-cne'ffmpeg.exe'-or$Fact.ProcessId-le0-or$Fact.ProcessId-gt[int]::MaxValue-or
  ([DateTimeOffset]$Fact.CreationDate).UtcDateTime-lt$birth-or-not(Test-ControllerParentAlive)){throw 'Closed blank image lacks admitted creator-proof context'}
 if(-not$pendingCreatorImages.ContainsKey($Key)){$pendingCreatorImages[$Key]=[ordered]@{fact=$Fact;label=$label;unheld=$Unheld;started=(Get-ControllerPolicyMilliseconds)}}
}
function Test-StagedCreatorFeed($Document,[string]$Cell,$Arguments){
 $s=$Document.stagedInput
 if($p.ReplayExporterRecipe-cne'replay-avc-aac-owned-completed-history-direct-avcc-with-reserved-staging-exact-fallback-v5'-or
  $Document.role-cne'export'-or$Document.variant-cne'packet'-or$Document.stage-ne0-or$Document.label-cnotin@('H01','H02','H03')-or
  $Document.ownership-cne'sealed-capture-job'-or$s.kind-cne'joined-protected-staged-feed-v1'){return $false}
 if($s.sourceBytes-le0-or$s.sourceBytes-gt8388608-or$s.sourceSha256-cnotmatch'^[0-9A-F]{64}$'-or
  $s.selectionIdentity-cnotmatch'^[0-9A-F]{64}$'-or$s.first-lt1-or$s.count-lt1-or$s.first+$s.count-gt120){return $false}
 $witnessPath=Join-Path $Cell 'native-export-witness.json'
 if($s.witnessFile-cne$witnessPath-or-not(Test-Path -LiteralPath $witnessPath)-or
  ((Get-Item -LiteralPath $witnessPath).Attributes-band[IO.FileAttributes]::ReparsePoint)){return $false}
 $raw=Read-Bounded $witnessPath 65536
 if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))-cne$s.witnessSha256){return $false}
 $w=[Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json;$t=$s.timing
 if($w.policy-cne$p.ReplayExporterRecipe-or$w.ownedRequest-ne$true-or$w.eligibleAtExport-ne$true-or$w.pipelineSelected-ne$true-or
  $w.scopeClosed-ne$true-or$w.preparationSelected-ne$true-or$w.preparationJoined-ne$true-or$w.selectionIdentity-cne$s.selectionIdentity-or
  $t.FeedJoined-ne$true-or$t.Aborted-ne$false-or$t.Error-or$t.FeedSha256-cne$s.sourceSha256-or$t.FedBytes-ne$s.sourceBytes-or
  $w.preparation.FeedSha256-cne$s.sourceSha256-or$w.preparation.FedBytes-ne$s.sourceBytes-or$w.preparation.Joined-ne$t.Joined-or
  $w.preparation.FeedJoined-ne$true-or$w.preparation.Aborted-ne$false-or$w.preparation.Error){return $false}
 $previous=0L
 foreach($name in @('Started','Ready','FeedAuthorized','FeedStarted','FeedCompleted')){
  $tick=[long]$t.$name;if($tick-le0-or$tick-lt$previous-or$tick-ne[long]$w.preparation.$name){return $false};$previous=$tick
 }
 if($t.RunCompleted-le0-or$t.Joined-lt$previous-or$t.Joined-lt$t.RunCompleted){return $false}
 $source=[string]$s.sourcePath;$partial=[string]$s.partial
 if(-not[IO.Path]::IsPathFullyQualified($source)-or[IO.Path]::GetFullPath($source)-cne$source-or
  -not$source.StartsWith($Cell+'\',[StringComparison]::OrdinalIgnoreCase)-or-not$source.EndsWith('.ts',[StringComparison]::OrdinalIgnoreCase)-or
  -not(Test-Path -LiteralPath $source)-or((Get-Item -LiteralPath $source).Attributes-band[IO.FileAttributes]::ReparsePoint)-or
  (Get-Item -LiteralPath $source).Length-ne$s.sourceBytes-or(Get-FileHash -LiteralPath $source).Hash-cne$s.sourceSha256-or
  -not[IO.Path]::IsPathFullyQualified($partial)-or[IO.Path]::GetFullPath($partial)-cne$partial-or
  [IO.Path]::GetDirectoryName($partial)-cne$Cell-or$w.Partial-cne$partial){return $false}
 $boundary=Join-Path ($partial+'.work') 'boundary-000000.ts'
 if($s.boundary-cne$boundary){return $false}
 $trim='trim=start_frame={0}:end_frame={1},setpts=PTS-STARTPTS'-f[int]$s.first,([int]$s.first+[int]$s.count)
 $expected=@('-hide_banner','-loglevel','warning','-xerror','-err_detect','explode','-nostdin','-y','-f','mpegts','-i','pipe:0',
  '-map','0:v:0','-an','-vf',$trim,'-fps_mode','passthrough','-enc_time_base:v','1:48000','-pix_fmt','yuv420p','-c:v','h264_nvenc',
  '-preset','p3','-tune','ll','-rc','cbr','-b:v','20M','-maxrate','20M','-bufsize','40M','-g','60','-bf','0','-forced-idr','1','-flags','+cgop','-f','mpegts',$boundary)
 if($Arguments.Count-ne$expected.Count){return $false}
 for($i=0;$i-lt$expected.Count;$i++){if($Arguments[$i]-cne$expected[$i]){return $false}}
 return $true
}
function Test-CreatorImageProof($Document,$Pending){
 $f=$Pending.fact;$r=$Document.receipt;$label=$Pending.label;$cell=Join-Path $p.Root $label
 if($Document.kind-cne'qualification-child-creator-v1'-or$Document.planSha256-cne$hash-or
  $Document.helperSha256-cne$p.Files.($helper[0])-or$Document.parentPid-ne$owned.Id-or
  ([DateTimeOffset]$Document.parentStartedUtc).UtcDateTime-ne$birth-or$Document.label-cne$label-or
  $Document.variant-cnotin@('packet','legacy')-or$Document.role-cnotin@('export','runtime-validation','offline-inspection','reference-oracle')-or
  $Document.stage-lt0-or$Document.stage-gt$(if($same-and$Document.role-ceq'reference-oracle'){2}else{1})-or$Document.enginePath-cne$engine[0]-or$Document.engineSha256-cne$p.Files.($engine[0])){return $false}
 if($Document.role-ceq'reference-oracle'-and-not$same){return $false}
 if($Document.admittedPlanSha256-cne(Get-FileHash -LiteralPath (Join-Path $p.Root 'admitted-plan.json')).Hash){return $false}
 if($Document.role-cnotin@('export','reference-oracle')-and$Document.stage-ne0){return $false}
 if($Document.role-in@('offline-inspection','reference-oracle')){
  $ownership=if($Document.role-ceq'reference-oracle'){'direct-held-joined-independent-reference'}else{'direct-held-joined-independent-inspection'}
  if($Document.ownership-cne$ownership-or$null-ne$Document.job){return $false}
 }else{
  $j=$Document.job
  if($Document.ownership-cne'sealed-capture-job'-or$j.WorkerId-ne$owned.Id-or$j.WorkerStartedUtcTicks-ne$birth.Ticks-or$j.Sealed-ne$true-or
   $j.JobName-cnotmatch'^Global\\F10Recorder-Producer-[0-9a-f]{32}$'){return $false}
 }
 if($r.ProcessId-ne$f.ProcessId-or-not$r.ProcessStartedUtc-or
  [Math]::Abs((([DateTimeOffset]$r.ProcessStartedUtc).UtcDateTime-([DateTimeOffset]$f.CreationDate).UtcDateTime).Ticks)-gt10-or
  $r.ExitCode-ne0-or$r.ProducerAbsent-ne$true-or$r.ActualExitObserved-ne$true-or$r.ResourcesFinal-ne$true-or$r.ResourceError-or$r.Failure-or$r.Error-or
  $r.CaptureComplete-ne$true-or$r.TimedOut-ne$false-or$r.OutputLimitExceeded-ne$false-or$r.CapturedBytes-lt0-or$r.CapturedBytes-gt2097152-or
  $r.SampledPeakWorkingSetBytes-gt2147483648){return $false}
 $admissionPath=Join-Path $p.Root 'child-admissions.jsonl'
 if(-not(Test-Path -LiteralPath $admissionPath)-or$Document.admissionOrdinal-le$p.RetainedChildren-or$Document.admissionOrdinal-gt$p.MaximumChildren){return $false}
 $admissionBytes=Read-Bounded $admissionPath 65536;$admissions=@()
 foreach($line in [Text.Encoding]::UTF8.GetString($admissionBytes).Split("`n")){
  if([string]::IsNullOrWhiteSpace($line)){continue}
  try{$row=$line|ConvertFrom-Json}catch [ArgumentException]{return $false}
  if($row.ordinal-eq$Document.admissionOrdinal){$admissions+=@($row)}
 }
 if($admissions.Count-ne1-or$admissions[0].role-cne($Document.role+':'+$label)-or
  ([DateTimeOffset]$admissions[0].utc).UtcDateTime-gt([DateTimeOffset]$r.ProcessStartedUtc).UtcDateTime){return $false}
 $path=[string]$Document.argumentsFile
 if(-not[IO.Path]::IsPathFullyQualified($path)-or[IO.Path]::GetFullPath($path)-cne$path-or
  -not$path.StartsWith($cell+'\',[StringComparison]::OrdinalIgnoreCase)-or-not(Test-Path -LiteralPath $path)-or
  ((Get-Item -LiteralPath $path).Attributes-band[IO.FileAttributes]::ReparsePoint)){return $false}
 $raw=Read-Bounded $path 65536
 if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))-cne$Document.argumentsSha256){return $false}
 $actualArguments=@([Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json);$declared=@($Document.arguments)
 if($actualArguments.Count-lt10-or$actualArguments.Count-gt256-or$actualArguments.Count-ne$declared.Count-or$actualArguments[0]-cne'-hide_banner'){return $false}
 for($i=0;$i-lt$actualArguments.Count;$i++){if($actualArguments[$i]-isnot[string]-or$declared[$i]-isnot[string]-or$actualArguments[$i]-cne$declared[$i]){return $false}}
 $at=[Array]::IndexOf($actualArguments,'-i')
 if($at-lt0-or$at+1-ge$actualArguments.Count){return $false}
 if($actualArguments[$at+1]-ceq'pipe:0'){return Test-StagedCreatorFeed $Document $cell $actualArguments}
 if($null-ne$Document.stagedInput-or-not([string]$actualArguments[$at+1]).StartsWith($cell+'\',[StringComparison]::OrdinalIgnoreCase)){return $false}
 return $true
}
function Resolve-PendingCreatorImages {
 foreach($key in @($pendingCreatorImages.Keys)){
  $pending=$pendingCreatorImages[$key]
  if((Get-ControllerPolicyMilliseconds)-$pending.started-ge$a.creatorProofMilliseconds){throw 'Retrospective creator image proof exceeded fixed130second deadline'}
  $cell=Join-Path $p.Root $pending.label;$matches=@();$count=0
  foreach($file in Get-ChildItem -LiteralPath $cell -File -Filter 'creator-exit-*.json' -ErrorAction SilentlyContinue){
   if(++$count-gt$(if($same-and-not$retained){9}else{8})-or($file.Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Creator proof file bound or reparse'}
   $bytes=Read-ControllerProofBytes $file.FullName;if($null-eq$bytes-or$bytes.Length-eq0){continue}
   try{$document=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json}catch [ArgumentException]{continue}
   if(Test-CreatorImageProof $document $pending){$matches+=@{path=$file.FullName;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));kind=$document.kind}}
  }
  if((Get-ControllerPolicyMilliseconds)-$pending.started-ge$a.creatorProofMilliseconds){throw 'Retrospective creator image proof exceeded work-inclusive130second deadline'}
  if($matches.Count-gt1){throw 'Ambiguous retrospective creator image proof'}
  if($matches.Count-eq1){
   $children[$key].path=$engine[0];$children[$key].creatorReceiptVerified=$true;$children[$key].imageEvidence='verified-retrospective-creator-receipt';$children[$key].creatorProof=$matches[0]
   if($pending.unheld){$lookupAbsentChildren[$key].verifiedImage=$engine[0];$lookupAbsentChildren[$key].creatorProof=$matches[0];$lookupAbsentChildren[$key].closureEvidence='typed-local-lookup-absence-plus-creator-proof'}
   $pendingCreatorImages.Remove($key)
  }
 }
}
