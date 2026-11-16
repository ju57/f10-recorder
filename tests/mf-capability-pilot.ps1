param([Parameter(Mandatory)][string]$AssemblyPath,[Parameter(Mandatory)][string]$PlanFile,[string]$OutputRoot,[string]$ExpectedPlanSha256,[switch]$Prepare,[switch]$Plan,[switch]$Run)
$ErrorActionPreference='Stop'
if(@($Prepare,$Plan,$Run).Where({$_}).Count -ne 1){throw 'Choose -Prepare, -Plan or -Run.'}
if($Plan){return [pscustomobject]@{NativeHelpers=1;Seconds=20;MaximumRam=512000000;MaximumThreads=128;MaximumOutputs=1000000;MinimumFree=60000000000;Effects='One capability-only process activates pinned System32 H264 class factory, minimal H264 media type and local CodecAPI properties. No media samples/decoder ProcessInput or ProcessOutput/D3D manager/quality advice/playback/capture/GPU decode/publication/profile/settings/startup/NVIDIA/install.'}}
$sdk=Join-Path (Split-Path $PSScriptRoot) '.tools/dotnet/dotnet.exe'
if($Prepare){
 if(-not $OutputRoot -or (Test-Path -LiteralPath $OutputRoot) -or (Test-Path -LiteralPath $PlanFile)){throw 'Fresh explicit output/plan required.'}
 $hostRoot=Split-Path ([IO.Path]::GetFullPath($AssemblyPath));$files=[ordered]@{}
 foreach($f in Get-ChildItem -LiteralPath $hostRoot -File){$files[$f.FullName]=(Get-FileHash -LiteralPath $f.FullName).Hash}
 $sys=[Environment]::SystemDirectory;$decoder=Join-Path $sys 'msmpeg2vdec.dll'
 $signatures=foreach($name in @('msmpeg2vdec.dll','mfplat.dll','ole32.dll','oleaut32.dll')){
  $path=Join-Path $sys $name;$signature=Get-AuthenticodeSignature -LiteralPath $path
  if($signature.Status -ne 'Valid'){throw "System module signature unavailable: $name"}
  $files[$path]=(Get-FileHash -LiteralPath $path).Hash
  @{path=$path;signer=$signature.SignerCertificate.Subject;thumbprint=$signature.SignerCertificate.Thumbprint;status=$signature.Status.ToString()}
 }
 foreach($path in @($sdk,(Join-Path $PSScriptRoot 'mf-capability-pilot.ps1'),(Join-Path $PSScriptRoot 'MfDecoderPrototype/Program.cs'),(Join-Path $PSScriptRoot 'MfDecoderPrototype/MfDecoderPrototype.csproj'),(Join-Path $PSScriptRoot 'MfDecoderPrototype/packages.lock.json'))){$full=[IO.Path]::GetFullPath($path);$files[$full]=(Get-FileHash -LiteralPath $full).Hash}
 @{kind='mf-capability-only-v1';root=[IO.Path]::GetFullPath($OutputRoot);files=$files;signatures=$signatures;decoderDll=$decoder;createdUtc=[datetime]::UtcNow;maximumHelpers=1;cumulativeNativeHelpers=148;deadlineSeconds=20;maximumRamBytes=512000000;maximumThreads=128;maximumOutputBytes=1000000;minimumFreeBytes=60000000000;phases=@{startupAndPins=5;localCapabilityQueries=10;cleanup=5};claim='Capability only. Unsupported required configuration stops the route; no frame decoding, equivalence or latency acceptance.'}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $PlanFile
 return (Get-FileHash -LiteralPath $PlanFile)
}
if(-not $ExpectedPlanSha256 -or (Get-FileHash -LiteralPath $PlanFile).Hash -ne $ExpectedPlanSha256){throw 'Reviewed exact capability plan required.'}
$p=Get-Content -LiteralPath $PlanFile -Raw|ConvertFrom-Json
if($p.files.PSObject.Properties.Name -notcontains [IO.Path]::GetFullPath($AssemblyPath)){throw 'Wrong bound capability helper.'}
foreach($f in $p.files.PSObject.Properties){if((Get-FileHash -LiteralPath $f.Name).Hash -ne $f.Value){throw "Bound capability input changed:$($f.Name)"}}
$receipt="$PlanFile.controller.json";if(Test-Path -LiteralPath $receipt){throw 'Existing capability attempt; retain it.'}
$argsList=@(('"'+[IO.Path]::GetFullPath($AssemblyPath)+'"'),('"'+[IO.Path]::GetFullPath($PlanFile)+'"'),$ExpectedPlanSha256)
$clock=[Diagnostics.Stopwatch]::StartNew();$owned=$null;$failure=$null;$exit=$null;$peakRam=[long]0;$peakThreads=0
@{state='admitted';sha256=$ExpectedPlanSha256;utc=[datetime]::UtcNow}|ConvertTo-Json|Set-Content -LiteralPath $receipt
try{
 if(([IO.DriveInfo]::new([IO.Path]::GetPathRoot($p.root))).AvailableFreeSpace -lt 60000000000){throw 'Capability free-space floor.'}
 $owned=Start-Process -FilePath $sdk -ArgumentList $argsList -WindowStyle Hidden -PassThru -WorkingDirectory (Split-Path $PSScriptRoot) -RedirectStandardOutput "$PlanFile.stdout.txt" -RedirectStandardError "$PlanFile.stderr.txt"
 while(-not $owned.WaitForExit(50)){
  if($clock.Elapsed.TotalSeconds -gt 15){throw 'Capability deadline;5s cleanup reserved.'}
  $owned.Refresh();if(-not $owned.HasExited){$peakRam=[Math]::Max($peakRam,$owned.PeakWorkingSet64);$peakThreads=[Math]::Max($peakThreads,$owned.Threads.Count)}
  if($peakRam -gt 512000000 -or $peakThreads -gt 128){throw 'Capability process resource cap.'}
  if((Get-Item -LiteralPath "$PlanFile.stdout.txt").Length+(Get-Item -LiteralPath "$PlanFile.stderr.txt").Length -gt 65536){throw 'Capability diagnostic cap.'}
  if((Test-Path -LiteralPath $p.root) -and (Get-ChildItem -LiteralPath $p.root -File|Measure-Object Length -Sum).Sum -gt 1000000){throw 'Capability output cap.'}
 }
 $exit=$owned.ExitCode;if($exit -ne 0){throw "Capability process failed $exit; retain evidence."}
}catch{$failure=$_.Exception.Message;throw}
finally{
 if($owned -and -not $owned.HasExited){$owned.Kill($true);if(-not $owned.WaitForExit(5000)){$failure='Owned capability process exit unconfirmed.'}}
 @{exit=$exit;error=$failure;elapsedSeconds=$clock.Elapsed.TotalSeconds;pid=if($owned){$owned.Id}else{$null};confirmedExited=if($owned){$owned.HasExited}else{$true};sampledPeakRam=$peakRam;sampledPeakThreads=$peakThreads}|ConvertTo-Json|Set-Content -LiteralPath $receipt
 if($owned){$owned.Dispose()}
}
