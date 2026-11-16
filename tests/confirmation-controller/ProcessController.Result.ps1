function Assert-ControllerCompleted {
 Assert-ObservedArgumentEvidence
 if($pendingControlImages.Count-or$pendingCreatorImages.Count){throw 'Unresolved creator proof prevents completion'}
 if(-not$retained-and($released.Count-ne1-or-not$released.Contains('H03'))){throw 'C00 lacks its current controlled foreground release'}
 if($retained-and$released.Count-ne0){throw 'Retained operation must not acquire foreground'}
 $result=[Text.Encoding]::UTF8.GetString((Read-Bounded (Join-Path $p.Root result.json) 1048576))|ConvertFrom-Json
 $expected=if($retained){'passed-fixed-retained-1m-pair-pending-independent-acceptance'}else{'passed-C00-frozen-union-no-refit-pending-independent-acceptance'}
 if($result.result-cne$expected-or$result.ordinarySavedSamples-ne0-or$result.modelActivated-ne$false){throw 'Missing exact confirmation-helper completion receipt'}
}
function Assert-RecoveryWriterAndModel {
 $writer=[Text.Encoding]::UTF8.GetString((Read-Bounded (Join-Path $p.Root storage-writer.json) 65536))|ConvertFrom-Json
 if(-not$writer.final-or-not$writer.writerAbsent-or$writer.failure-or$writer.enqueued-ne$writer.written-or$writer.bytes-ne$writer.persistedBytes){throw 'Writer did not completely drain'}
}
function Write-GlobalAdmissionMap {
 # Preserve the new operation's actual local roles without relabelling any old
 # receipt. Historical counters remain in their original immutable reports.
 $path=Join-Path $p.Root child-admissions.jsonl
 if(Test-Path -LiteralPath $path){
  $bytes=Read-Bounded $path 65536
  $out=Join-Path $directory operation-child-admissions.jsonl
  $stream=[IO.FileStream]::new($out,'CreateNew','Write','Read')
  try{$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
 }
}
