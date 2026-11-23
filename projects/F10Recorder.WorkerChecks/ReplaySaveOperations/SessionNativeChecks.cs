using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
namespace F10Recorder;
internal static class SessionNativeChecks
{
 sealed class Scope:IReplaySaveObservationScope{public bool RequiresConservative=>false;public void Dispose(){}}
 static void Need(bool ok,string why){if(!ok)throw new IOException(why);}
 static void Reject(Action action,string why){try{action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or ObjectDisposedException or System.ComponentModel.Win32Exception){return;}throw new IOException(why);}
 sealed class Counted(string main):ReplayFiles
 {
  internal int Main,Other,Capacity;internal bool DenyMain;
  internal override ReplayFileExtent Observe(string path){if(path==main){Main++;if(DenyMain)throw new IOException("Main pathname probe must not occur in owned callback.");}else Other++;return base.Observe(path);}
  internal override ReplayVolumeCapacity ObserveCapacity(string path){Capacity++;return base.ObserveCapacity(path);}
 }
 internal static void Run(string root,Action<string,object> passed,Action bound)
 {
  Directory.CreateDirectory(root);string authority=Path.Combine(root,"authority");
  long clock=0;var freshness=new ReplaySaveObservation<Scope>(action=>action(),()=>true,()=>new(),()=>{},_=>true,()=>true,timestamp:()=>clock,frequency:1000);
  freshness.Check(true);clock=ReplayCapacityPolicy.CoverageMilliseconds+100;Reject(freshness.RequireFreshness,"Terminal authority close renewed or skipped the declared capacity coverage");passed("session-terminal-freshness-only-check-does-not-renew",null);
  using(var session=new TrustedStorage.LockDirectoryLease(authority,"replay-volume-reservations"))
  {
   using(var op=session.AcquireOperation())
   {
    Reject(session.Dispose,"Authority closed with active shared-lock operation");
    var other=new ReplayVolumeReservations(authority);var sw=Stopwatch.StartNew();
    Reject(()=>other.Update(Guid.NewGuid().ToString("N"),[],new ReplayFiles(),acceptedWork:true),"A competitor entered the shared exclusive lock");
    Need(sw.ElapsedMilliseconds>=150&&sw.ElapsedMilliseconds<1000,"Accepted-work200ms lock wait changed");
    passed("session-shared-lock-kept-per-operation-and-accepted-wait",new{milliseconds=sw.Elapsed.TotalMilliseconds});
   }
   using(var op=session.AcquireOperation()){};passed("session-not-locked-for-entire-save",null);
   Reject(()=>Directory.Move(authority,authority+"-moved"),"Pinned authority root moved");passed("session-authority-root-pinned",null);
   var info=new DirectoryInfo(authority);var original=info.GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access);
   var changed=new DirectorySecurity();changed.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(),AccessControlSections.Access);changed.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid,null),FileSystemRights.Write,AccessControlType.Allow));
   try{info.SetAccessControl(changed);Reject(()=>session.AcquireOperation().Dispose(),"Cached authority ACL hid a new foreign writer");passed("session-fresh-acl-rejects-writer-before-restoration",null);}
   finally{var restore=new DirectorySecurity();restore.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(),AccessControlSections.Access);info.SetAccessControl(restore);var before=new RawSecurityDescriptor(original.GetSecurityDescriptorBinaryForm(),0);var after=new RawSecurityDescriptor(info.GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access).GetSecurityDescriptorBinaryForm(),0);Need(before.Owner==after.Owner&&before.DiscretionaryAcl!=null&&after.DiscretionaryAcl!=null&&before.DiscretionaryAcl.Count==after.DiscretionaryAcl.Count&&(before.ControlFlags&~ControlFlags.DiscretionaryAclAutoInherited)==(after.ControlFlags&~ControlFlags.DiscretionaryAclAutoInherited),"Own fixture DACL owner/protection/flags changed");byte[] expectedAcl=new byte[before.DiscretionaryAcl.BinaryLength],actualAcl=new byte[after.DiscretionaryAcl.BinaryLength];before.DiscretionaryAcl.GetBinaryForm(expectedAcl,0);after.DiscretionaryAcl.GetBinaryForm(actualAcl,0);Need(expectedAcl.SequenceEqual(actualAcl),"Own fixture exact ACE sequence/masks/SIDs did not restore");}
   using(var op=session.AcquireOperation()){};passed("session-fresh-acl-every-acquisition",null);
   using(var entered=new ManualResetEventSlim())using(var proceed=new ManualResetEventSlim())
   {
    var active=Task.Run(()=>{using var op=session.AcquireOperation();entered.Set();Need(proceed.Wait(2000),"Lifecycle handoff stalled");});
    try{Need(entered.Wait(2000),"Operation did not start");Reject(session.Dispose,"Concurrent lifecycle close overtook active operation");}finally{proceed.Set();active.GetAwaiter().GetResult();}
   }
   passed("session-concurrent-operation-disposal-fails-closed",null);
  }
  var volume=new ReplayVolumeReservations(Path.Combine(root,"volume"));using(var request=volume.BeginRequestSession())
  {Reject(()=>volume.BeginRequestSession(),"Second request replaced authority owner");volume.Update(Guid.NewGuid().ToString("N"),[],new ReplayFiles());}
  using(var request=volume.BeginRequestSession()){};passed("session-request-owner-reentry-and-clean-close",null);
  byte[] media=global::ReplayMp4Checks.PacketMuxFixture();var parsed=ReplayMp4Reader.Read(new MemoryStream(media),media.Length,new(0,1,1),60,true);
  var video=parsed.Tracks.Single(t=>t.Kind=="vide");var audio=parsed.Tracks.Single(t=>t.Kind=="soun");
  var proof=new ReplayMp4Expectation(60,0,video.Sps,video.Pps,video.Samples.Select(s=>new ReplayExpectedPicture(s.Idr,s.CodedSha256,s.PacketSha256)),1024,audio.Samples.Select(s=>new ReplayExpectedAudio(s.Duration,s.PayloadSha256)));
  string destination=Path.Combine(root,"output");Directory.CreateDirectory(destination);string main=Path.Combine(destination,"main.mp4"),foreign=Path.Combine(destination,"foreign.bin");File.WriteAllBytes(foreign,new byte[4096]);
  using var files=new ReplayRootedFiles();files.Root(destination);var req=new ReplayExportRequest(new(destination,"",1,20,60,"h264_nvenc",".ts",false,true),new(0,1,1),[],"",main,new());req.FreezeIntegrity(proof);req.NativeFiles=new(files,req);
  var counter=new Counted(main);var capacity=ReplayStorage.ObserveCapacity(destination);string owner=Guid.NewGuid().ToString("N"),otherOwner=Guid.NewGuid().ToString("N");
  var claims=new[]{new ReplayVolumeClaim(main,destination,capacity.Volume,capacity.AllocationUnit,ReplayReferenceModel.Round(media.Length+4096,capacity.AllocationUnit))};
  using var sessionVolume=volume.BeginRequestSession();volume.Update(owner,claims,counter);var output=req.NativeFiles.CreateMain();
  try
  {
   output.Write(media);output.Flush();using var snapshot=req.NativeFiles.Observe();using var evidence=req.NativeFiles.BorrowReservationEvidence(snapshot,owner);Need(evidence!=null,"Exclusive current writer did not provide callback evidence");counter.DenyMain=true;
   Reject(()=>volume.Update(owner,[..claims,new(foreign,destination,"wrong-volume",capacity.AllocationUnit,8192)],counter,evidence:evidence),"Invalid incoming claim accepted");
   string ledger=Path.Combine(root,"volume","replay-volume-reservations.json");Need(File.ReadAllText(ledger).Contains(snapshot.Main.Extent.Identity,StringComparison.Ordinal),"Own native materialization checkpoint lost on rejected proposal");passed("session-owned-first-appearance-durable-before-proposal-error",null);
   volume.Update(owner,claims,counter,evidence:evidence);volume.Update(owner,claims,counter,evidence:evidence);
   Need(counter.Main==0&&counter.Capacity>=2,"Owned callback re-opened main or skipped fresh capacity");passed("session-fresh-current-main-serves-refresh-and-incoming-only",new{counter.Main,counter.Capacity});
   Reject(()=>volume.Update(otherOwner,claims,counter,evidence:evidence),"Evidence owner changed");
   Reject(()=>Task.Run(()=>evidence.Observe(claims[0],null,null)).GetAwaiter().GetResult(),"Evidence escaped original producer thread");
   Reject(()=>evidence.Observe(claims[0],destination,"different-anchor"),"Prior anchor mismatch repaired by native fact");passed("session-main-evidence-owner-thread-anchor-guards",null);
   counter.DenyMain=false;try{volume.Update(otherOwner,[new(foreign,destination,capacity.Volume,capacity.AllocationUnit,8192)],counter);}finally{counter.DenyMain=true;}
   int before=counter.Other,mainBefore=counter.Main;volume.Update(owner,claims,counter,evidence:evidence);Need(counter.Other>before&&counter.Main==mainBefore,"Foreign holder borrowed current main evidence");volume.Release(otherOwner);passed("session-foreign-owner-still-freshly-observed",null);
   Need(evidence.Observe(new(foreign,destination,capacity.Volume,capacity.AllocationUnit,8192),null,null)==null,"Other file silently became main evidence");
   files.Admit(foreign,ReplayStorage.Observe(foreign),false);req.NativeFiles.CreateWorkspace();string workPath=ReplayOutputWorkspace.BoundaryPath(main,0);req.NativeFiles.CopyBoundary(foreign,workPath);
   using(var workSnapshot=req.NativeFiles.Observe())using(var workEvidence=req.NativeFiles.BorrowReservationEvidence(workSnapshot,owner)){
    int priorReads=counter.Other;volume.Update(owner,[..claims,new(workPath,destination,capacity.Volume,capacity.AllocationUnit,8192)],counter,evidence:workEvidence);Need(counter.Other>priorReads,"Work observer sharing was falsely treated as immutable evidence");}
   passed("session-work-without-immutable-owner-retains-fresh-probes",null);
   evidence.Dispose();Reject(()=>volume.Update(owner,claims,counter,evidence:evidence),"Expired callback evidence reused");passed("session-expired-evidence-fails-closed",null);
   using var next=req.NativeFiles.Observe();using var nextEvidence=req.NativeFiles.BorrowReservationEvidence(next,owner);snapshot.Dispose();
   using(var expired=req.NativeFiles.Observe())using(var borrowed=req.NativeFiles.BorrowReservationEvidence(expired,owner)){expired.Dispose();Reject(()=>borrowed.Observe(claims[0],null,null),"Disposed native snapshot survived");}passed("session-expired-native-snapshot-fails-closed",null);
   output.Dispose();Reject(()=>nextEvidence.Observe(claims[0],null,null),"Exclusive writer protection ended without rejection");
   using var gap=req.NativeFiles.Observe();Need(req.NativeFiles.BorrowReservationEvidence(gap,owner)==null,"Closed-writer gap reused unprotected main facts");passed("session-ended-write-protection-and-conservative-gap",null);
   counter.DenyMain=false;int conservativeBefore=counter.Main;volume.Update(owner,claims,counter);Need(counter.Main>conservativeBefore,"Conservative path stopped doing main observation");volume.Release(owner);
  }
  finally{output.Dispose();req.NativeFiles.Dispose();}
  bound();
 }
}

