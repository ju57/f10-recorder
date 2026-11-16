# Exactly one reviewed conditional binding for this pair. No command here
# creates authorization, moves a deadline, changes source policy or retries.
function Assert-ConfirmationRelease {
 $authorization=[Text.Encoding]::UTF8.GetString((Read-Bounded (Join-Path $directory current-authorization.json) 8192))|ConvertFrom-Json
 if($authorization.approvedPlanSha256-cne$hash-or$authorization.approvedCaptures-ne$captures-or-not$authorization.questionItemId-or
  ([DateTimeOffset]$authorization.approvedUtc).UtcDateTime-gt$entryUtc){throw 'Explicit current-task exact-plan release required'}
 $releasePath=Join-Path $directory approval-release.json
 $release=[Text.Encoding]::UTF8.GetString((Read-Bounded $releasePath 65536))|ConvertFrom-Json
 if($release.kind-cne'issue83-two-confirmation-release-v1'-or$release.questionItemId-cne$authorization.questionItemId-or
  $release.approvedUtc-cne$authorization.approvedUtc-or$release.planSha256-cne$hash){throw 'One actual approval release must bind the final raw plan'}
 $t0=([DateTimeOffset]$release.approvedUtc).UtcDateTime
 if($t0-gt$entryUtc-or([DateTimeOffset]$a.expiresUtc).UtcDateTime-ne$t0.AddSeconds(3600)-or
  (-not$retained-and([DateTimeOffset]$a.originalPurposeDeadlineUtc).UtcDateTime-ne$t0.AddSeconds(1800))){throw 'Fixed approval-time windows required; never reset'}
 $manifestPath=[string]$release.invariantManifest
 if($p.Files.PSObject.Properties.Name-cnotcontains$manifestPath-or$p.Files.$manifestPath-cne$release.invariantManifestSha256-or
  $authorization.invariantManifestSha256-cne$release.invariantManifestSha256){throw 'Human-approved immutable manifest required'}
 $manifest=[Text.Encoding]::UTF8.GetString((Read-Bounded $manifestPath 524288))|ConvertFrom-Json
 if($manifest.kind-cne'issue83-two-confirmation-invariants-v1'){throw 'Wrong invariant manifest'}
 foreach($f in $manifest.files.PSObject.Properties){if((Get-FileHash -LiteralPath $f.Name).Hash-cne$f.Value){throw 'Approved invariant changed'}}
 $slot=$manifest.operations.($a.operation)
 if(-not$slot-or$slot.path-cne$planPath-or$manifest.files.PSObject.Properties.Name-cnotcontains$slot.template){throw 'Exact frozen operation template required'}
 $template=[Text.Encoding]::UTF8.GetString((Read-Bounded $slot.template 65536))|ConvertFrom-Json
 # Compare the entire admission after replacing ONLY these reviewed time/hash
 # leaves. Each dynamic digest is checked against its named genuine file.
 $actual=[Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json
 foreach($field in @('createdUtc','expiresUtc','originalPurposeDeadlineUtc')){
  if($template.PSObject.Properties.Name-contains$field){
   $expected=switch($field){'createdUtc'{$t0};'expiresUtc'{$t0.AddSeconds(3600)};'originalPurposeDeadlineUtc'{$t0.AddSeconds(1800)}}
   if(([DateTimeOffset]$actual.$field).UtcDateTime-ne$expected){throw 'Unreviewed conditional time value'}
   $actual.$field=$template.$field
  }
 }
 $actualResources=if($retained){$actual.Resources}else{$actual.plan}
 $templateResources=if($retained){$template.Resources}else{$template.plan}
 # The manifest pins its template; the template contains a sentinel for this
 # digest to avoid a self-referential hash cycle. The exact digest was checked
 # against both the raw final plan and actual human release above.
 if($templateResources.Files.$manifestPath-cne'conditional-approved-manifest'){throw 'Frozen template manifest sentinel required'}
 $actualResources.Files.$manifestPath=$templateResources.Files.$manifestPath
 $allowed=@('coverage-freeze','source-manifest','host-facts','preservation-before','retained-attempt','resources','rehearsal','outside-storage-baseline','outside-inventory','outside-inventory-plan','outside-forward-envelope','outside-outstanding-writes','fresh-candidate-qualification')
 if($retained){$allowed+=@('C00-controller')}
 if($retained-and$same){$allowed+=@('C00-confirmation','C00-manifest','C00-acceptance','C00-selection','C00-plan','C00-oracle-data','C00-source-evidence','C00-reference-binding','C00-source-clock')}
 if(@($slot.lateBindings).Count-gt24){throw 'Conditional binding count'}
 foreach($binding in $slot.lateBindings){
  if($binding.role-cnotin$allowed-or$actualResources.Files.PSObject.Properties.Name-cnotcontains$binding.path-or
   $templateResources.Files.PSObject.Properties.Name-cnotcontains$binding.path-or
   (Get-FileHash -LiteralPath $binding.path).Hash-cne$actualResources.Files.($binding.path)){throw 'Exact reviewed derived receipt binding required'}
  $actualResources.Files.($binding.path)=$templateResources.Files.($binding.path)
  if($retained-and$binding.role-ceq'coverage-freeze'){
   if($actual.FreezePath-cne$binding.path-or$actual.FreezeSha256-cne$p.Files.($binding.path)){throw 'Retained freeze digest differs'}
   $actual.FreezeSha256=$template.FreezeSha256
  }
 }
 if($retained-and$same){
  $manifestFile=[string]$actual.SameCapture.ManifestPath
  $c00Manifest=[Text.Encoding]::UTF8.GetString((Read-Bounded $manifestFile 524288))|ConvertFrom-Json
  if($c00Manifest.Revision-cne'reference-same-C00-coverage-v10'-or$c00Manifest.End-$c00Manifest.Start-lt299.999999){throw 'Genuine complete C00 manifest required for conditional selection'}
  $selected=@($c00Manifest.Inputs|Where-Object {$_.End-gt$c00Manifest.End-60-.08-and$_.Start-lt$c00Manifest.End}|Sort-Object Start)
  if($selected.Count-lt1-or$selected.Count-gt65-or($selected|Measure-Object -Property {$_.Extent.Length} -Sum).Sum-gt200000000){throw 'Prospective same-C00 final minute tier exceeded'}
  $original=@([Text.Encoding]::UTF8.GetString((Read-Bounded $actual.OriginalSelectionPath 524288)).Split("`n")|Where-Object {-not[string]::IsNullOrWhiteSpace($_)}|ForEach-Object {$_|ConvertFrom-Json})
  $derived=@(foreach($mappedInput in $selected){
   $match=@($original|Where-Object {$_.Extent.Volume-ceq$mappedInput.Extent.Volume-and$_.Extent.Identity-ceq$mappedInput.Extent.Identity-and$_.Extent.Length-eq$mappedInput.Extent.Length-and$_.Start-eq$mappedInput.Start-and$_.End-eq$mappedInput.End})
   if($match.Count-ne1-or$mappedInput.Sha256-cnotmatch'^[0-9A-F]{64}$'){throw 'C00 ordered original/native alias/digest mapping is ambiguous'}
   [pscustomobject][ordered]@{OriginalPath=$match[0].Path;CurrentPath=$mappedInput.Path;ContentSha256=$mappedInput.Sha256}
  })
  if(($actual.Inputs|ConvertTo-Json -Depth 12 -Compress)-cne($derived|ConvertTo-Json -Depth 12 -Compress)-or
   (($actualResources.SelectedRetainedFiles-join"`n")-cne(($derived.CurrentPath)-join"`n"))-or
   $actual.OriginalSelectionSha256-cne$p.Files.($actual.OriginalSelectionPath)){throw 'Retained fields differ from the immutable prospective C00 selection rule'}
  $actual.Inputs=$template.Inputs;$actualResources.SelectedRetainedFiles=$templateResources.SelectedRetainedFiles
  $actual.OriginalSelectionSha256=$template.OriginalSelectionSha256
 }
 if(($actual|ConvertTo-Json -Depth 60 -Compress)-cne($template|ConvertTo-Json -Depth 60 -Compress)){throw 'Plan differs outside reviewed conditional leaves'}
 # This is execution authorization, never media acceptance. There is no
 # invented A/V schema. Each helper result stays pending independent acceptance.
 # The final pair cannot be approved until its actual oracle/source proof exist.
 if($retained){
  if($p.Files.PSObject.Properties.Name-cnotcontains$release.C00ControllerPath-or
   @($slot.lateBindings|Where-Object {$_.role-ceq'C00-controller'-and$_.path-ceq$release.C00ControllerPath}).Count-ne1){throw 'Previous C00 closure needs the exact reviewed derived receipt path/hash binding'}
  $previous=[Text.Encoding]::UTF8.GetString((Read-Bounded $release.C00ControllerPath 262144))|ConvertFrom-Json
  if((Get-FileHash -LiteralPath $manifest.operations.C00.path).Hash-cne$release.C00PlanSha256-or
   $previous.operation-cne'C00'-or$previous.planHash-cne$release.C00PlanSha256-or$previous.absent-ne$true-or$previous.exitCode-ne0-or$previous.error-or
   $previous.postDisposal-ne$true-or$previous.processHandlesDisposed-ne$true){throw 'Fixed order requires actual closed C00 before retained pair'}
  return
 }
 $preflight=[Text.Encoding]::UTF8.GetString((Read-Bounded (Join-Path $directory PREFLIGHT.json) 8192))|ConvertFrom-Json
 if($preflight.kind-cne'final-calibration-admission-v1'-or$preflight.result-cne'passed'-or$preflight.planSha256-cne$hash-or
  $preflight.helperSha256-cne$p.Files.($helper[0])){throw 'Actual final helper admission required'}
}
