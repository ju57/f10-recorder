param([Parameter(Mandatory)][string]$FreshRoot,
 [ValidateSet('https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-full_build-shared.7z','https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-full_build-shared.7z')]
 [string]$SourceUrl='https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-full_build-shared.7z')
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $FreshRoot){throw 'Fresh staging root required; retain prior failures.'}
if(([IO.DriveInfo]::new([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($FreshRoot)))).AvailableFreeSpace -lt 61500000000){throw 'Staging free-space floor.'}
New-Item -ItemType Directory -Path $FreshRoot | Out-Null
$archive=Join-Path $FreshRoot 'ffmpeg-9.0.2-full_build-shared.7z'
$client=[Net.Http.HttpClient]::new();$client.Timeout=[timespan]::FromSeconds(120)
$cancel=[Threading.CancellationTokenSource]::new(120000);$response=$null;$failure=$null;$bytes=[long]0
try{
 $response=$client.GetAsync($SourceUrl,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cancel.Token).GetAwaiter().GetResult()
 $response.EnsureSuccessStatusCode()|Out-Null
 if($response.Content.Headers.ContentLength -gt 250000000){throw 'Download length cap.'}
 $input=$response.Content.ReadAsStreamAsync($cancel.Token).GetAwaiter().GetResult()
 $output=[IO.File]::Open($archive,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try{
  $buffer=[byte[]]::new(1048576)
  while(($count=$input.ReadAsync($buffer,0,$buffer.Length,$cancel.Token).GetAwaiter().GetResult()) -gt 0){
   $bytes+=$count;if($bytes -gt 250000000){throw 'Streaming download cap.'};$output.Write($buffer,0,$count)
  }
 }finally{$output.Dispose();$input.Dispose()}
 $hash=(Get-FileHash -LiteralPath $archive).Hash
 if($hash -ne '4D2060A8B34A940AA47D785142055BB92A63053781E55F2ACE4546EDD519A8F5'){throw 'Archive SHA mismatch; retain failed bytes, do not extract.'}
 @{result='downloaded-and-hash-verified';url=$SourceUrl;bytes=$bytes;sha256=$hash;utc=[datetime]::UtcNow}|ConvertTo-Json|Set-Content (Join-Path $FreshRoot 'download.json')
}catch{$failure=$_.Exception.Message;@{result='failed-download';error=$failure;bytes=$bytes;utc=[datetime]::UtcNow}|ConvertTo-Json|Set-Content (Join-Path $FreshRoot 'failure.json');throw}
finally{if($response){$response.Dispose()};$cancel.Dispose();$client.Dispose()}
