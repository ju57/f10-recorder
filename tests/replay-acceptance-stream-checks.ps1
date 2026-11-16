param([switch]$Plan)
$ErrorActionPreference='Stop'
if($Plan){'Managed-only checks of the actual bounded helper-log copier: empty, exact cap, cap+1, large overflow and read cancellation/failure. In-memory Add-Type/streams; no Win32 call, child process, UI, capture, audio, file output or settings change.';return}
if(-not ('F10ReplayAcceptanceNative' -as [type])){Add-Type -LiteralPath (Join-Path $PSScriptRoot 'ReplayAcceptanceNative.cs')}
Add-Type -TypeDefinition @'
using System; using System.IO; using System.Threading; using System.Threading.Tasks;
public sealed class ReplayCheckReadStream : Stream {
 public bool Fail;
 public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false;
 public override long Length=>0; public override long Position {get=>0;set=>throw new NotSupportedException();}
 public override async Task<int> ReadAsync(byte[] b,int o,int c,CancellationToken token){if(Fail)throw new IOException("Injected read failure");await Task.Delay(Timeout.Infinite,token);return 0;}
 public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException(); public override void Flush(){}
 public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long x)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
}
'@
$cap=2097152;$passed=0
foreach($size in @(0,$cap,($cap+1),(3*$cap+5))){
 $bytes=[byte[]]::new($size);[Random]::new(83).NextBytes($bytes);$input=[IO.MemoryStream]::new($bytes,$false);$output=[IO.MemoryStream]::new()
 $copy=[F10ReplayAcceptanceNative+PrefixCopy]::new($input,$output,$cap)
 try{
  if(-not $copy.Completion.Wait(5000)){throw 'Bounded copier check deadline.'};$null=$copy.Completion.GetAwaiter().GetResult();$expected=[Math]::Min($cap,$size)
  if($copy.Read -ne $size -or $copy.Kept -ne $expected -or $output.Length -ne $expected -or $copy.Overflow -ne ($size -gt $cap)){throw 'Actual prefix copy/overflow/drain mismatch.'}
  $sha=[Security.Cryptography.SHA256]::Create()
  try{if([Convert]::ToHexString($sha.ComputeHash($bytes,0,$expected)) -cne [Convert]::ToHexString($sha.ComputeHash($output.ToArray()))){throw 'Retained prefix bytes differ.'}}finally{$sha.Dispose()}
  $passed++
 }finally{if($copy.Completion.IsCompleted){$copy.Dispose();$input.Dispose();$output.Dispose()}}
}
foreach($fail in @($false,$true)){
 $input=[ReplayCheckReadStream]::new();$input.Fail=$fail;$output=[IO.MemoryStream]::new();$copy=[F10ReplayAcceptanceNative+PrefixCopy]::new($input,$output,$cap)
 try{
  if(-not $fail){$copy.Cancel()};$rejected=$false
  try{$null=$copy.Completion.GetAwaiter().GetResult()}catch{$rejected=$true}
  if(-not $rejected -or -not $copy.Completion.IsCompleted -or $copy.Kept -ne 0 -or $copy.Overflow){throw 'Read cancellation/failure was lost.'}
  $passed++
 }finally{if($copy.Completion.IsCompleted){$copy.Dispose();$input.Dispose();$output.Dispose()}}
}
"Passed $passed actual bounded-stream checks."
