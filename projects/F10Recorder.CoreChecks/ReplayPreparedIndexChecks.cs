using F10Recorder;
using System.Runtime.InteropServices;

internal static class ReplayPreparedIndexChecks
{
    sealed class Observed(byte[] bytes):MemoryStream(bytes)
    {
        internal int Reads;
        public override int Read(byte[] buffer,int offset,int count){Reads++;return base.Read(buffer,offset,count);}
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellation=default)
        {Reads++;return base.ReadAsync(buffer,cancellation);}
    }
    static void Require(bool value,string message){if(!value)throw new IOException(message);}
    static Exception Fail(Action work){try{work();}catch(Exception error){return error;}throw new Exception("Expected failure.");}
    internal static void Run()
    {
        byte[] sequence=ReplayTransportChecks.VideoSequence(60,253500);
        var sequenceIndex=ReplayTransportReader.Read(new MemoryStream(sequence),sequence.Length,60,false);
        Require(sequenceIndex.Pictures.Count==60 && sequenceIndex.Pictures[0].Pts==253500 && sequenceIndex.Pictures[^1].Pts==342000,
            "Native lifecycle video fixture changed its physical picture/timestamp inventory.");
        byte[][] bytes=Enumerable.Range(0,4).Select(n=>ReplayTransportChecks.Fixture(codedPayload:Enumerable.Repeat((byte)(0x80+n),1000).ToArray())).ToArray();
        var originals=bytes.Select(b=>ReplayTransportReader.Read(new MemoryStream(b),b.Length,60,true)).ToArray();
        foreach(bool mixed in new[]{false,true})
        {
            var streams=bytes.Select(b=>new Observed(b)).ToArray();
            try
            {
                var copied=originals.Select(ReplayPreparedIndexAcquisition.Copy).ToArray();
                if(mixed)copied[2]=null;
                var acquisition=new ReplayPreparedIndexAcquisition();var lifetime=new ReplayWorkerLifetime();var timing=new ReplayTransportBatchTiming();
                int thread=Environment.CurrentManagedThreadId,checks=0;
                var result=acquisition.Read(streams.Select(s=>new ReplayTransportSource(s,s.Length)).ToArray(),copied,60,true,
                    ()=>{Require(thread==Environment.CurrentManagedThreadId,"Metadata hit dispatched an owner guard to a worker.");checks++;},timing,lifetime);
                Require(acquisition.Complete && acquisition.Hits==(mixed?3:4) && acquisition.Misses==(mixed?1:0),"Wrong prepared acquisition counts.");
                Require(lifetime.Joined && lifetime.Started==mixed && timing.Workers==(mixed?1:0),"All-hit acquisition fabricated parser workers or a miss lacked real readers.");
                Require(streams.Select((s,n)=>n==2&&mixed?s.Reads>0:s.Reads==0).All(x=>x),"Prepared acquisition reread hits or skipped a real miss.");
                Require(result.Select(i=>i.Sha256).SequenceEqual(originals.Select(i=>i.Sha256)),"Prepared/miss ordering or metadata changed.");
                Require(checks>=2 && (mixed || checks==2),"All-hit operation used per-input guard rendezvous.");
            }
            finally{foreach(var stream in streams)stream.Dispose();}
        }
        var isolated=ReplayPreparedIndexAcquisition.Copy(originals[0]);
        Require(MemoryMarshal.TryGetArray(isolated.Sps,out ArraySegment<byte> config),"Request configuration has no owned test backing.");
        byte prior=originals[0].Sps.Span[0];config.Array[config.Offset]^=1;
        Require(originals[0].Sps.Span[0]==prior,"Request metadata mutation reached retained index.");
        Require(Fail(()=>((IList<ReplayTransportPicture>)isolated.Pictures)[0]=isolated.Pictures[0] with{Pts=1}) is NotSupportedException,"Picture arrays escaped mutable.");
        var all=bytes.Select(b=>(Stream)new MemoryStream(b)).ToArray();
        try
        {
            var readers=new ReplayWorkerLifetime();var acquisition=new ReplayPreparedIndexAcquisition();
            Require(Fail(()=>acquisition.Read(all.Select((s,n)=>new ReplayTransportSource(s,s.Length+(n==0?188:0))).ToArray(),originals,
                60,true,null,new(),readers)) is IOException && !readers.Started && !acquisition.Complete,"Mismatched extent became a hit.");
            Require(Fail(()=>new ReplayPreparedIndexAcquisition().Read(all.Select(s=>new ReplayTransportSource(s,s.Length)).ToArray(),originals,
                60,true,()=>throw new OperationCanceledException(),new(),new())) is OperationCanceledException,"Owner cancellation was ignored for hits.");
        }
        finally{foreach(var stream in all)stream.Dispose();}
        byte[] unsupported=ReplayTransportChecks.Fixture(videoGap:true), corrupt=bytes[1].ToArray();corrupt[0]=0;
        using(var first=new MemoryStream(unsupported))using(var second=new MemoryStream(corrupt))
        {
            var lifetime=new ReplayWorkerLifetime();
            Require(Fail(()=>new ReplayPreparedIndexAcquisition().Read([new(first,first.Length),new(second,second.Length)],
                new ReplayTransportIndex[2],60,true,null,new(),lifetime)) is IOException && lifetime.Started && lifetime.Joined,
                "Unsupported miss masked a corrupt peer or escaped reader join.");
        }
        using(var changed=new MemoryStream(bytes[1]))using(var workspace=new ReplayPacketSampleWorkspace())
            Require(Fail(()=>ReplayTransportPacketSamples.Read(changed,originals[0],null,null,null,workspace,new())) is IOException,
                "A prepared metadata hit became fresh byte proof, including an input with no emitted packets.");
        Console.WriteLine("Passed: prepared all-hit/mixed acquisition, true reader lifetimes, metadata isolation, cancellation, corruption precedence and fresh omitted-source bytes.");
    }
}
