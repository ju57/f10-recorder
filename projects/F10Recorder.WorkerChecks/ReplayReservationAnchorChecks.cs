using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace F10Recorder;
internal static class ReplayReservationAnchorChecks
{
 internal static int Run(string root)
 {
root=Path.GetFullPath(root);
if(!root.StartsWith(Path.GetFullPath("artifacts")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Directory.Exists(root)||File.Exists(root))throw new IOException("Fresh task-owned root required");
Directory.CreateDirectory(root);
var clock=Stopwatch.StartNew();var rows=new List<object>();bool failed=false,unexpected=false;
foreach(int repetition in new[]{1,2})
foreach(bool existing in new[]{true,false})
foreach(bool producer in new[]{false,true})
{
 string folder=Path.Combine(root,$"{repetition}-"+(existing?"existing":"late")+(producer?"-producer":"-peer")),output=Path.Combine(folder,"output");Directory.CreateDirectory(folder);
 if(existing)Directory.CreateDirectory(output);
 var capacity=ReplayStorage.ObserveCapacity(folder);string main=Path.Combine(output,"main.partial.mp4"),owner=Guid.NewGuid().ToString("N");
 var store=new ReplayVolumeReservations(Path.Combine(folder,"authority"));var peer=store.CreatePeer();var claims=new[]{new ReplayVolumeClaim(main,output,capacity.Volume,capacity.AllocationUnit,8192)};
 store.Update(owner,claims,new ReplayFiles());
 string before=File.ReadAllText(Path.Combine(folder,"authority","replay-volume-reservations.json"));
 using var files=new ReplayRootedFiles();files.EnsureOutputRoot(output);
 // Matches Cache's final conservative refresh before registering its owner.
 store.Update(owner,claims,new ReplayFiles());
 var request=new ReplayExportRequest(new(output,"",1,20,60,"h264_nvenc",".ts",false,true),new(0,1,1),[],"",main,new());
 var scope=new ReplayNativeExportScope(files,request);scope.RegisterReservationObservation(store,owner,8192);
 Exception failure=null;FileStream writer=null;ReplayNativeExportScope.Snapshot snapshot=null;ReplayReservationEvidence evidence=null;bool renameDenied=false,changedAnchorRejected=false,peerFailureRetained=false;string faultError=null;
 try {
  if(producer){string digest=new('A',64);request.FreezeIntegrity(new ReplayMp4Expectation(60,0,new byte[]{0x67},new byte[]{0x68},Enumerable.Range(0,60).Select(n=>new ReplayExpectedPicture(n==0,digest,digest))));writer=scope.CreateMain();writer.Write(new byte[4096]);writer.Flush();snapshot=scope.Observe();evidence=scope.BorrowReservationEvidence(snapshot,owner);if(evidence==null)throw new IOException("Missing actual producer evidence");}
  // The rooted capability must still prevent actual ancestor redirection.
  try{Directory.Move(folder,folder+"-moved");}catch(IOException){renameDenied=true;}
  if(!renameDenied){Directory.Move(folder+"-moved",folder);throw new IOException("Protected ancestor unexpectedly moved");}
  void Refresh(){if(producer)store.Update(owner,claims,new ReplayFiles(),evidence:evidence);else peer.Update(Guid.NewGuid().ToString("N"),[],new ReplayFiles());}
  Refresh();
  // Task-owned ledger fault only; no installed authority or user media.
  string ledger=Path.Combine(folder,"authority","replay-volume-reservations.json"),healthy=File.ReadAllText(ledger);var json=JsonNode.Parse(healthy);json["Owners"][0]["Claims"][0]["AnchorIdentity"]="changed-anchor-identity";File.WriteAllText(ledger,json.ToJsonString());
  string expectedFault=existing||producer?"Owned reservation target or anchor changed.":"Owned reservation ancestor identity changed.";
  try{Refresh();}catch(IOException e){faultError=e.Message;changedAnchorRejected=e.Message==expectedFault;}finally{File.WriteAllText(ledger,healthy);}
  if(!changedAnchorRejected)throw new IOException("Changed anchor identity was accepted or failed for another cause");
  if(!producer){try{scope.RequireHealthyPublication();}catch(IOException e){peerFailureRetained=e.Message.Contains("previously failed",StringComparison.Ordinal);}if(!peerFailureRetained)throw new IOException("Healthy metadata repaired a failed peer owner observation");}
 }
 catch(Exception e) {failure=e;failed=true;unexpected=true;}
 finally {evidence?.Dispose();snapshot?.Dispose();writer?.Dispose();store.Release(owner);scope.Dispose();}
 rows.Add(new{repetition,existing,producer,error=failure?.ToString(),renameDenied,changedAnchorRejected,peerFailureRetained,faultError,admissionLedger=before,seconds=clock.Elapsed.TotalSeconds});
 using var self=Process.GetCurrentProcess();if(clock.Elapsed.TotalSeconds>10||self.WorkingSet64>268435456||self.HandleCount>2048)throw new IOException("Probe bound exceeded");
}
object cache=null;string cacheError=null;
try{cache=CacheAnchorChecks.Run(Path.Combine(root,"cache"));}catch(Exception e){cacheError=e.ToString();failed=true;unexpected=true;}
File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{failed,unexpected,rows,cache,cacheError,seconds=clock.Elapsed.TotalSeconds},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(new{failed,unexpected,seconds=clock.Elapsed.TotalSeconds}));
return unexpected?2:failed?1:0;

 }
}
