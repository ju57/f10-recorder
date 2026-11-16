using System.Security.Cryptography;
namespace F10Recorder;
internal static class PipelineNativeChecks
{
    static void Need(bool b,string why){if(!b)throw new IOException(why);}
    static void Reject(Action run,string why){try{run();}catch(Exception e)when(e is IOException or UnauthorizedAccessException){return;}throw new IOException(why);}
    internal static void Run(string root,Action<string,object> passed,Action bound)
    {
        Directory.CreateDirectory(root);byte[] payload=Enumerable.Range(0,4096).Select(n=>(byte)(n%251)).ToArray();string partial=Path.Combine(root,"checked.bin");File.WriteAllBytes(partial,payload);
        using var directory=new ReplayGuardDirectory(root,Native.DirectoryIdentity(root),ReplayStorage.ObserveCapacity(root).Volume);var extent=ReplayStorage.Observe(partial);
        using var ticket=directory.OpenAsyncPublicationRead("checked.bin",extent);Need(ticket.IsImmutablePublicationReader,"Async reader weakened sharing");
        using var lease=new ReplayPublicationLease(partial,directory,ticket,extent);Need(lease.Stream.IsAsync,"Async checked-read flag missing");var read=lease.OpenPacketReadWork();Need(read!=null,"Verified async borrow missing");
        Reject(()=>{using var writer=new FileStream(partial,FileMode.Open,FileAccess.Write,FileShare.Read|FileShare.Delete);},"Late writer entered immutable async reader");passed("pipeline-async-immutable-reader-denies-late-writer",null);
        using var entered=new ManualResetEventSlim();using var proceed=new ManualResetEventSlim();var request=new ReplayExportRequest(new(root,"",1,20,60,"h264_nvenc",".ts",false,false),new(0,1,1),[],"",partial,new()){PacketRead=read};
        var lifetime=new ReplayExportLifetime();string checkedHash=null;int guardOwner=0;
        var owner=Task.Factory.StartNew(()=>
        {
            guardOwner=Environment.CurrentManagedThreadId;
            read.Run(new[]{new ReplayPacketReadSample(0,0,new(0,payload.Length,0,1024,false,null,null,null),false,null,null)},row=>checkedHash=row.Sample.CodedSha256,
                ()=>{Need(Environment.CurrentManagedThreadId==guardOwner,"Native checked read guard escaped owner");if(!entered.IsSet){entered.Set();if(!proceed.Wait(TimeSpan.FromSeconds(2)))throw new IOException("Native checked-read handoff timed out");}},false);
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
            Need(entered.Wait(TimeSpan.FromSeconds(2))&&!read.Joined,"Checked phase did not become active");
            lifetime.ObserveRequest(request);Need(!lifetime.Stopped,"Global lifetime ignored unjoined checked readers");
            Reject(lease.Dispose,"Unjoined async lease closed early");Need(lease.Stream.CanRead,"Rejected disposal closed data handle");
        }
        finally{proceed.Set();if(!owner.Wait(TimeSpan.FromSeconds(3)))throw new IOException("Native checked reader join unconfirmed");owner.GetAwaiter().GetResult();}
        Need(read.Joined&&read.Used&&checkedHash==Convert.ToHexString(SHA256.HashData(payload)),"Actual immutable offset read changed bytes or did not join");
        Need(!lifetime.Stopped,"Later healthy join repaired uncertain lifetime");Reject(()=>lifetime.Begin().Dispose(),"Uncertain lifetime admitted another request");passed("pipeline-checked-read-global-lifetime-and-lease-closure",read.Report());
        string final=Path.Combine(root,"final.bin");lease.Publish(final);var actual=ReplayStorage.Observe(final);Need(actual.Identity==extent.Identity&&actual.Volume==extent.Volume&&actual.Length==extent.Length,"Checked async publication changed native object");passed("pipeline-async-checked-object-publication",new{actual.Identity,actual.Length});
        var sourceLifetime=new ReplayExportLifetime();var pending=request with{PacketRead=null,PacketMuxWork=new(){Workers=1,Joined=false}};pending.PacketReaders.Begin();sourceLifetime.ObserveRequest(pending);Need(!sourceLifetime.Stopped,"Global lifetime ignored unjoined packet producers");pending.PacketMuxWork.Joined=true;sourceLifetime.ObserveRequest(pending);Need(!sourceLifetime.Stopped,"Diagnostic join released active readers");pending.PacketReaders.Complete(true);sourceLifetime.ObserveRequest(pending);Need(!sourceLifetime.Stopped,"Later packet join cleared uncertain producer");passed("pipeline-source-join-lifetime-sticky",null);
        bound();
    }
}

