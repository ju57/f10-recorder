using System.Diagnostics;
using System.Text.Json.Nodes;
namespace F10Recorder;
internal static class LedgerCheckpointChecks
{
 sealed class Files:ReplayFiles{
  internal int CapacityCalls,Observations;internal long? Free;
  internal override ReplayVolumeCapacity ObserveCapacity(string path){CapacityCalls++;var value=base.ObserveCapacity(path);return Free.HasValue?value with{AvailableBytes=Free.Value}:value;}
  internal override ReplayFileExtent Observe(string path){Observations++;return base.Observe(path);}
 }
 static void Need(bool ok,string why){if(!ok)throw new IOException(why);}
 static JsonObject Json(string path)=>JsonNode.Parse(File.ReadAllText(path)).AsObject();
 static string Id(string path)=>ReplayStorage.Observe(path).Identity;

 internal static void Run(string root,Action<string,object> passed,Action bound){
  Directory.CreateDirectory(root);
  {
   string dir=Path.Combine(root,"production"),authority=Path.Combine(dir,"authority");Directory.CreateDirectory(dir);string input=Path.Combine(dir,"main.bin");File.WriteAllBytes(input,new byte[4096]);var capacity=ReplayStorage.ObserveCapacity(dir);var files=new Files();
   var claim=new ReplayVolumeClaim(input,dir,capacity.Volume,capacity.AllocationUnit,8192);string owner=Guid.NewGuid().ToString("N"),other=Guid.NewGuid().ToString("N");
   var store=new ReplayVolumeReservations(authority);string ledger=Path.Combine(authority,"replay-volume-reservations.json");
   store.Update(owner,[claim],files);Need(Json(ledger)["Version"].GetValue<int>()==3,"Missing ledger must durably publish Version3");
   {
    string initial=Id(ledger);int queries=files.CapacityCalls,observed=files.Observations;int changed=0;var clock=Stopwatch.StartNew();
    for(int i=0;i<5;i++){bound();string beforeId=Id(ledger);store.Update(owner,[claim],files);if(Id(ledger)!=beforeId)changed++;}
    Need(files.CapacityCalls>=queries+5&&files.Observations>=observed+10,"Stable payload skipped fresh capacity/native checks");Need(changed==5,"Every accepted update must durably publish a fresh epoch");
    passed("ledger-production-rewrites-unchanged-payload",new{calls=5,changed,milliseconds=clock.Elapsed.TotalMilliseconds,capacityQueries=files.CapacityCalls-queries,nativeObservations=files.Observations-observed});
   }
   { string old=Id(ledger);
    string initial=old;files.Free=0;bool rejected=false;try{store.Update(owner,[claim],files);}catch(IOException){rejected=true;}files.Free=null;
    Need(rejected&&Id(ledger)==initial,"Stable payload bypassed fresh insufficient capacity");passed("ledger-unchanged-still-rejects-fresh-low-capacity",null);
   }
   using(var locked=TrustedStorage.AcquireLock(authority,"replay-volume-reservations")){
    bool rejected=false;try{store.Update(owner,[claim],files);}catch(ReplayVolumeReservationBusyException){rejected=true;}Need(rejected,"Stable payload bypassed shared authority lock");passed("ledger-unchanged-still-obeys-authority-lock",null);
   }
   { string old=Id(ledger);claim=claim with{PeakAllocatedBytes=12288};store.Update(owner,[claim],files);Need(Id(ledger)!=old,"Changed peak wasn't durable");}
   { string old=Id(ledger);claim=claim with{PeakAllocatedBytes=8192};store.Update(owner,[claim],files);Need(Id(ledger)!=old,"Reduced peak wasn't durable");}passed("ledger-changed-growth-transitions-still-publish",null);
   string inputB=Path.Combine(dir,"other.bin");File.WriteAllBytes(inputB,new byte[4096]);var claimB=claim with{Path=inputB};store.Update(other,[claimB],files);store.Update(owner,[claim],files);
   { string old=Id(ledger);int queries=files.CapacityCalls,observed=files.Observations;store.Update(owner,[claim],files);Need(Id(ledger)!=old&&Json(ledger)["Owners"].AsArray().Count==2&&files.CapacityCalls>queries&&files.Observations>=observed+3,"Other unknown owner's fresh obligation was lost");}
   store.Release(other);Need(Json(ledger)["Owners"].AsArray().Count==1,"Release transition not durable");passed("ledger-other-owner-obligations-and-release-preserved",null);
   var legacy=Json(ledger);legacy["Version"]=1;File.WriteAllText(ledger,legacy.ToJsonString());{ string old=Id(ledger);store.Update(owner,[claim],files);Need(Id(ledger)!=old&&Json(ledger)["Version"].GetValue<int>()==3,"Version1 incorrectly skipped upgrade");}passed("ledger-legacy-state-forces-version3-write",null);
   File.AppendAllText(ledger," ");{ string old=Id(ledger);store.Update(owner,[claim],files);Need(Id(ledger)!=old,"Accepted update failed to publish a fresh epoch");}passed("ledger-valid-wire-state-still-durably-republished",null);
   using(var denyMutation=new FileStream(ledger,FileMode.Open,FileAccess.Read,FileShare.Read)){
    foreach(var proposal in new[]{claim,claim with{PeakAllocatedBytes=12288}}){bool rejected=false;try{store.Update(owner,[proposal],files);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){rejected=true;}Need(rejected,"Accepted update bypassed failed durable publication");}
   }passed("ledger-unchanged-and-changed-write-failures-reject",null);
   File.WriteAllText(ledger,"{");bool corrupt=false;try{store.Update(owner,[claim],files);}catch(IOException){corrupt=true;}Need(corrupt&&File.ReadAllText(ledger)=="{","Cached payload bypassed unreadable ledger");passed("ledger-fresh-corrupt-state-fails-closed",null);
  }
  string m=Path.Combine(root,"materialization"),a=Path.Combine(m,"authority");Directory.CreateDirectory(m);string leaf=Path.Combine(m,"future.bin"),id=Guid.NewGuid().ToString("N");var cap=ReplayStorage.ObserveCapacity(m);var f=new Files();var reserved=new ReplayVolumeClaim(leaf,m,cap.Volume,cap.AllocationUnit,8192);var s=new ReplayVolumeReservations(a);s.Update(id,[reserved],f);File.WriteAllBytes(leaf,new byte[4096]);f.Free=0;bool failed=false;try{s.Update(id,[reserved],f);}catch(IOException){failed=true;}string actual=Json(Path.Combine(a,"replay-volume-reservations.json"))["Owners"][0]["Claims"][0]["Identity"].GetValue<string>();Need(failed&&actual==Id(leaf),"Materialization checkpoint lost when later proposal rejected");passed("ledger-first-materialization-durable-even-if-proposal-rejected",null);bound();
  string recovery=Path.Combine(root,"recovery-aba");Directory.CreateDirectory(recovery);string data=Path.Combine(recovery,"main.bin");File.WriteAllBytes(data,new byte[4096]);var rc=ReplayStorage.ObserveCapacity(recovery);var rf=new Files();var c=new ReplayVolumeClaim(data,recovery,rc.Volume,rc.AllocationUnit,8192);string o=Guid.NewGuid().ToString("N");
  using var current=Process.GetCurrentProcess();long oldTicks=current.StartTime.ToUniversalTime().Ticks-1;var absent=new ReplayProducerProof(current.Id,oldTicks,ReplayProducerProof.JobPrefix+Guid.NewGuid().ToString("N"),true);Need(ReplayProducerProof.Valid(absent)&&absent.ConfirmedAbsent(),"Synthetic old-process/absent-job control didn't prove absence");
  var recoveryAuthority=new ReplayVolumeReservations(Path.Combine(recovery,"authority"));recoveryAuthority.Update(o,[c],rf,absent);string file=Path.Combine(recovery,"authority","replay-volume-reservations.json");byte[] recoveryPayload=File.ReadAllBytes(file);
  recoveryAuthority.Update(o,[c],rf,absent);Need(!File.ReadAllBytes(file).AsSpan().SequenceEqual(recoveryPayload)&&Json(file)["Owners"].AsArray().Count==1,"Recovery ABA skipped required reintroduced identical reservation");passed("ledger-recovery-write-invalidates-old-comparison-checkpoint",null);bound();
 }
}

