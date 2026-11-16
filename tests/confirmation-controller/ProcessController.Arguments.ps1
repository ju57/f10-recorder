# Windows CRT quoting used by ProcessStartInfo.ArgumentList. Pure parsing; no
# remote module load, native process creation or PID-based ownership adoption.
function Split-ControllerArguments([string]$Line){
 if([string]::IsNullOrWhiteSpace($Line)-or$Line.Length-gt32768){throw 'Missing/bounded child command line required'}
 $items=[Collections.Generic.List[string]]::new();$at=0
 while($at-lt$Line.Length){
  while($at-lt$Line.Length-and[char]::IsWhiteSpace($Line[$at])){$at++}
  if($at-ge$Line.Length){break}
  $word=[Text.StringBuilder]::new();$quoted=$false
  while($at-lt$Line.Length){
   if(-not$quoted-and[char]::IsWhiteSpace($Line[$at])){break}
   $slashes=0;while($at-lt$Line.Length-and$Line[$at]-eq'\'){$slashes++;$at++}
   if($at-lt$Line.Length-and$Line[$at]-eq'"'){
    [void]$word.Append('\',[int]($slashes/2 - ($slashes%2)/2))
    if($slashes%2){[void]$word.Append('"');$at++}
    elseif($quoted-and$at+1-lt$Line.Length-and$Line[$at+1]-eq'"'){[void]$word.Append('"');$at+=2}
    else{$quoted=-not$quoted;$at++}
   }else{
    if($slashes){[void]$word.Append('\',$slashes)}
    if($at-lt$Line.Length){if(-not$quoted-and[char]::IsWhiteSpace($Line[$at])){break};[void]$word.Append($Line[$at]);$at++}
   }
  }
  if($quoted){throw 'Unclosed child argument quote'}
  $items.Add($word.ToString());if($items.Count-gt257){throw 'Child argument count bound'}
 }
 return ,$items.ToArray()
}
function Same-ControllerArguments($Left,$Right){
 if($Left.Count-ne$Right.Count){return $false}
 for($index=0;$index-lt$Left.Count;$index++){if($Left[$index]-cne$Right[$index]){return $false}}
 return $true
}
function Assert-ObservedArgumentEvidence {
 foreach($row in $rawChildren){
  if(-not$row.bornAfterHeldParent-or$row.path-ieq$consoleImage){continue}
  $key="$($row.pid)/$($row.birth.Ticks)"
  $image=if($children.ContainsKey($key)){$children[$key].path}else{$row.path}
  if([string]::IsNullOrEmpty($row.arguments)){
   if(-not$children.ContainsKey($key)-or$children[$key].creatorReceiptVerified-ne$true){throw 'Blank child command line needs exact closed creator proof'}
   continue
  }
  $argv=Split-ControllerArguments $row.arguments
  if($argv[0]-ine$image){throw 'Command-line image differs from observed executable'}
  if($image-ieq$runtime[0]){
   $fixtureSelector=if($same){'--replay-same-C00-fixture'}else{'--replay-calibration-duration-fixture'}
   $expectedFixture=@($runtime[0],$helper[0],$fixtureSelector,(Join-Path $p.Root H03),'H03','5')
   $presentationPath=$null
   if($p.PSObject.Properties['EvidenceRoles']-and$p.EvidenceRoles-and$p.EvidenceRoles.PSObject.Properties['source-presentation-method']){$presentationPath=$p.EvidenceRoles.'source-presentation-method'}
   if($presentationPath){
    $presentationHash=$p.Files.$presentationPath
    if(-not$same-or$presentationHash-cnotmatch'^[A-F0-9]{64}$'){throw 'Pinned native presentation method required'}
    $expectedFixture+=@('--presentation-reference',$presentationPath,$presentationHash)
   }
   if($retained-or-not(Same-ControllerArguments $argv $expectedFixture)){throw 'Exact C00 fixture argv required'}
   continue
  }
  if($image-ine$engine[0]){throw 'Unknown child executable arguments'}
  $matched=$false
  foreach($name in @('version','nonzero','timeout','output-limit')){
   $path=Join-Path $p.Root "controls/$name.json";if(-not(Test-Path -LiteralPath $path)){continue}
   $doc=[Text.Encoding]::UTF8.GetString((Read-Bounded $path 65536))|ConvertFrom-Json
   $actualBirth=([DateTimeOffset]$doc.receipt.ProcessStartedUtc).UtcDateTime
   if($doc.receipt.ProcessId-eq$row.pid-and[Math]::Abs(($actualBirth-$row.birth).Ticks)-le10-and
    (Test-ControlReceipt $doc $name $row.pid $actualBirth $doc.receipt.ExitCode)-and(Same-ControllerArguments $argv (@($engine[0])+@($doc.arguments)))){$matched=$true}
  }
  $cell=Join-Path $p.Root H03
  if(Test-Path -LiteralPath $cell){
   $count=0
   foreach($file in Get-ChildItem -LiteralPath $cell -File -Filter 'creator-exit-*.json'){
    if(++$count-gt$(if($same-and-not$retained){9}else{8})){throw 'Creator argument evidence count'}
    $doc=[Text.Encoding]::UTF8.GetString((Read-Bounded $file.FullName 65536))|ConvertFrom-Json
    if($doc.receipt.ProcessId-eq$row.pid-and[Math]::Abs((([DateTimeOffset]$doc.receipt.ProcessStartedUtc).UtcDateTime-$row.birth).Ticks)-le10-and
     (Test-CreatorImageProof $doc @{fact=[pscustomobject]@{ProcessId=$row.pid;CreationDate=$row.birth};label='H03'})-and
     (Same-ControllerArguments $argv (@($engine[0])+@($doc.arguments)))){$matched=$true}
   }
   $path=Join-Path $cell capture-exit.json
   if(Test-Path -LiteralPath $path){
    $doc=[Text.Encoding]::UTF8.GetString((Read-Bounded $path 65536))|ConvertFrom-Json
    if($doc.pid-eq$row.pid-and[Math]::Abs((([DateTimeOffset]$doc.startedUtc).UtcDateTime-$row.birth).Ticks)-le10-and$doc.exited-eq$true-and$doc.producersAbsent-eq$true-and$doc.exit-eq0){
     $args=[Text.Encoding]::UTF8.GetString((Read-Bounded (Join-Path $cell capture-arguments.json) 65536))|ConvertFrom-Json
     if(Same-ControllerArguments $argv (@($engine[0])+@($args))){$matched=$true}
    }
   }
  }
  if(-not$matched){throw 'Observed engine argv lacks its exact owned control/capture/creator receipt'}
 }
}
