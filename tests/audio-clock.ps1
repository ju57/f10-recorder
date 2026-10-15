param([string]$Ffmpeg='C:\Program Files\ffmpeg\bin\ffmpeg.exe')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$data=Join-Path $root ('artifacts\audio-clock-contract-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$env:DOTNET_CLI_HOME=Join-Path $root '.tools\home'
$env:NUGET_PACKAGES=Join-Path $root '.tools\packages'
& (Join-Path $root '.tools\dotnet\dotnet.exe') run --project (Join-Path $root 'projects\F10Recorder.CoreChecks') -c Release --no-restore -- $data
if($LASTEXITCODE -ne 0){throw 'Core clock checks failed.'}
$template=Get-Content (Join-Path $data 'clock-filter.txt') -Raw
$probe=Join-Path (Split-Path -Parent $Ffmpeg) 'ffprobe.exe'
foreach($format in 'u8','s16le','s24le','s32le','f32le'){
    $input=Join-Path $data ($format+'-delayed.mka')
    $json=& $probe -v error -show_streams -show_packets -of json $input
    if($LASTEXITCODE -ne 0){throw "Invalid timestamped PCM transport: $format"}
    $media=($json -join [Environment]::NewLine) | ConvertFrom-Json
    if($media.streams.Count -ne 1 -or $media.streams[0].sample_rate -ne '48000' -or $media.streams[0].channels -ne 2){throw 'PCM format was not preserved.'}
    if($media.packets.Count -ne 2 -or [double]$media.packets[0].pts_time -ne .5 -or [double]$media.packets[1].pts_time -ne .51){throw 'Transport lost acquisition timestamps under delivery delay.'}
    $output=Join-Path $data ($format+'-decoded.pcm')
    & $Ffmpeg -hide_banner -v error -xerror -i $input -map '0:a:0' -c:a ('pcm_'+$format) -f $format $output
    if($LASTEXITCODE -ne 0 -or (Get-FileHash $output).Hash -ne (Get-FileHash (Join-Path $data ($format+'-expected.pcm'))).Hash){throw "PCM sample round trip failed: $format"}
    $json | Set-Content (Join-Path $data ($format+'-probe.json'))
}
$results=@()
foreach($delayUs in 300000,400000){
    # A deterministic acquisition time on the first frame, followed by an RTC
    # step. Actual FFmpeg evaluation must preserve the source intervals.
    $first=1800000000000000L+$delayUs
    $expression=$template.Replace('RTCTIME',('if(eq(N,0),'+$first+',1800000999000000)'))
    $hash=Join-Path $data ('frames-'+$delayUs+'.sha256')
    & $Ffmpeg -hide_banner -v warning -f lavfi -i 'testsrc2=size=32x32:rate=60:duration=0.1' -vf ('settb=AVTB,'+$expression) -fps_mode passthrough -enc_time_base:v '1:1000000' -f framehash $hash 2> (Join-Path $data ('ffmpeg-'+$delayUs+'.stderr.log'))
    if($LASTEXITCODE -ne 0){throw 'FFmpeg clock expression failed.'}
    $rows=@(Get-Content $hash | Where-Object {$_ -and -not $_.StartsWith('#')} | ForEach-Object {,$_.Split(',')})
    if($rows.Count -ne 6){throw 'Clock filter changed the source frame count.'}
    $pts=@($rows | ForEach-Object {[long]$_[2]})
    if($pts[0] -ne $delayUs){throw 'Video first acquisition was reset instead of mapped to the shared epoch.'}
    for($i=1;$i -lt $pts.Count;$i++){if([Math]::Abs(($pts[$i]-$pts[0])-$i*1000000/60) -gt 1){throw 'Source timestamp intervals changed after the first RTC bridge.'}}
    $results+=@{acquisitionDelayUs=$delayUs;pts=$pts;frameCount=$rows.Count}
}
@{result='passed';trials=$results;meaning='100ms later acquisition advances media PTS100ms; later RTC changes do not rebase frames. This does not emulate a system clock step inside ddagrab itself.'} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $data 'verification.json')
Write-Host "Passed clock contract: $data"
