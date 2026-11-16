# Default production operations. The lifecycle entry may replace these names
# only inside its separately hash-bound, no-capture scenario process.
function Get-ControllerFacts($Parent){
 @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($Parent.Id)" -Property ProcessId,ParentProcessId,CreationDate,ExecutablePath,WorkingSetSize,Name,CommandLine -OperationTimeoutSec 2)
}
function Open-ControllerProcess([int]$ChildPid){[Diagnostics.Process]::GetProcessById($ChildPid)}
function Read-ControllerHandle($Process){try{$Process.GetType().GetProperty('SafeHandle').GetValue($Process)}catch{throw [InvalidOperationException]::new(('Handle acquisition failed: '+$_.Exception.Message),$_.Exception)}}
function Read-ControllerBirth($Process){try{$Process.GetType().GetProperty('StartTime').GetValue($Process)}catch{throw [InvalidOperationException]::new(('Birth acquisition failed: '+$_.Exception.Message),$_.Exception)}}
function Read-ControllerImage($Process,$HeldHandle){
 # The process already has its own held handle/birth. Query the executable
 # identity directly; do not enumerate remote loader modules or reopen a PID.
 if($null-eq$script:controllerImageQuery){
  $product=Split-Path -Parent $helper[0]
  $expected=@{}
  foreach($name in @('F10Recorder.Core.dll','F10Recorder.Windows.dll')) { $expected[$name]=$p.Files.(Join-Path $product $name); if($expected[$name]-cnotmatch'^[0-9A-F]{64}$'){throw 'Bound current image-query assembly required'} }
  $leases=[Collections.Generic.List[IDisposable]]::new()
  try{
   foreach($name in @('F10Recorder.Core.dll','F10Recorder.Windows.dll')){
    $path=Join-Path $product $name;$lease=[IO.FileStream]::new($path,'Open','Read',[IO.FileShare]::Read);$leases.Add($lease)
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($lease))-cne$expected[$name]){throw 'Frozen image-query assembly changed'}
    $assembly=[Reflection.Assembly]::LoadFrom($path)
   }
   $script:controllerImageQuery=$assembly.GetType('F10Recorder.Native',$true).GetMethod('QueryFullProcessImageName',([Reflection.BindingFlags]::NonPublic-bor[Reflection.BindingFlags]::Static))
   if($null-eq$controllerImageQuery-or$controllerImageQuery.GetParameters()[0].ParameterType-ne[IntPtr]){throw 'Exact held-handle image query unavailable'}
  }finally{foreach($lease in $leases){$lease.Dispose()}}
 }
 $held=$HeldHandle;$added=$false
 if($null-eq$held-or$held.IsInvalid-or$held.IsClosed){throw 'Image query lacks valid held process handle'}
 try{
  $held.DangerousAddRef([ref]$added)
  $name=[Text.StringBuilder]::new(32768);$arguments=[object[]]@($held.DangerousGetHandle(),[uint32]0,$name,[uint32]32768)
  $ok=[bool]$controllerImageQuery.Invoke($null,$arguments);$nativeError=[Runtime.InteropServices.Marshal]::GetLastPInvokeError()
  if($ok-and$name.Length-gt0){return $name.ToString()}
  if($Process.HasExited){return $null} # No image proof: caller still requires its distinct CIM/cache/creator path.
  throw [ComponentModel.Win32Exception]::new($nativeError,('Live held executable image query failed; Win32='+$nativeError))
 }finally{if($added){$held.DangerousRelease()}}
}
function Stop-ControllerProcess($Process){$Process.Kill()}
function Join-ControllerProcess($Process,[int]$Milliseconds,[string]$Stage){$Process.WaitForExit($Milliseconds)}
function Write-ControllerBytes([string]$Path,[byte[]]$Bytes){[IO.File]::WriteAllBytes($Path,$Bytes)}
function Dispose-ControllerProcess($Process){$Process.Dispose()}
function Get-ControllerPolicyMilliseconds {$clock.Elapsed.TotalMilliseconds}
function Drain-ControllerCopy($Copy,[int]$Milliseconds){if(-not$Copy.Wait($Milliseconds)){throw 'Log drain unconfirmed'};$null=$Copy.GetAwaiter().GetResult()}
function Close-ControllerStream($Stream,[string]$Stage){$Stream.Dispose()}
function Restore-ControllerEnvironment([string]$Name,[string]$Value){[Environment]::SetEnvironmentVariable($Name,$Value)}
function Register-ControllerRoot($Process,[DateTime]$Birth){}
function Register-ControllerCreatedRoot($Process){}
function Test-ControllerParentAlive { $owned-and-not$owned.HasExited }
function Read-ControllerProofBytes([string]$Path){
 $stream=$null
 try{$stream=[IO.FileStream]::new($Path,'Open','Read',[IO.FileShare]::Read)}catch [IO.IOException]{return $null}
 try{if($stream.Length-gt65536){throw 'Control proof64KiB bound'};$bytes=[byte[]]::new([int]$stream.Length);$stream.ReadExactly($bytes);return ,$bytes}finally{$stream.Dispose()}
}
function Write-ControllerRaw($Stream,[byte[]]$Bytes){$Stream.Write($Bytes)}
