using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Retained-file exercise of the shipping history/admission/acquisition and
// bounded extraction implementations. No writer, codec, capture or Saved claim.
internal static class ReplayCompletedHistoryFilePilot
{
    static readonly JsonSerializerOptions Json=new(){WriteIndented=true,IncludeFields=true};
    sealed record Source(string Path,string Origin,string Session,string Sha256,ReplayFileExtent Extent);
    [StructLayout(LayoutKind.Sequential)]struct Io{public ulong Reads,Writes,Other,ReadBytes,WriteBytes,OtherBytes;}
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetProcessIoCounters(IntPtr process,out Io io);
    sealed record Totals(long Qpc,double Cpu,long Allocated,long Heap,long Working,long Private,long Peak,int Gen0,int Gen1,int Gen2,ulong? ReadBytes,ulong? WriteBytes);
    static Totals Observe()
    {
        long qpc=Stopwatch.GetTimestamp();using var process=Process.GetCurrentProcess();
        bool available=GetProcessIoCounters(process.Handle,out var io);
        return new(qpc,process.TotalProcessorTime.TotalSeconds,GC.GetTotalAllocatedBytes(true),GC.GetTotalMemory(false),
            process.WorkingSet64,process.PrivateMemorySize64,process.PeakWorkingSet64,GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2),
            available?io.ReadBytes:null,available?io.WriteBytes:null);
    }
    static object Difference(Totals before,Totals after)=>new{milliseconds=Stopwatch.GetElapsedTime(before.Qpc,after.Qpc).TotalMilliseconds,
        cpuSeconds=after.Cpu-before.Cpu,allocatedBytes=after.Allocated-before.Allocated,heapBefore=before.Heap,heapAfter=after.Heap,
        workingBefore=before.Working,workingAfter=after.Working,privateBefore=before.Private,privateAfter=after.Private,lifetimePeakWorking=after.Peak,
        gc=new[]{after.Gen0-before.Gen0,after.Gen1-before.Gen1,after.Gen2-before.Gen2},logicalReadBytes=after.ReadBytes-before.ReadBytes,logicalWriteBytes=after.WriteBytes-before.WriteBytes};
    static void Require(bool condition,string reason){if(!condition)throw new IOException(reason);}
    internal static int Run(string planPath,string expectedHash)
    {
        byte[] raw=File.ReadAllBytes(planPath);Require(raw.Length<=262144 && Convert.ToHexString(SHA256.HashData(raw))==expectedHash,"Changed bounded file plan.");
        using var document=JsonDocument.Parse(raw);var plan=document.RootElement;
        Require(plan.GetProperty("kind").GetString()=="f10-completed-history-file-pilot-v1","Wrong file pilot.");
        foreach(var binding in plan.GetProperty("files").EnumerateObject())
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binding.Name)))==binding.Value.GetString(),"Changed file pilot input: "+binding.Name);
        string cohortPath=plan.GetProperty("cohortPlan").GetString();
        using var cohort=JsonDocument.Parse(File.ReadAllBytes(cohortPath));
        var sources=cohort.RootElement.GetProperty("sources").Deserialize<Source[]>(Json);
        Require(sources.Length==301 && sources.Sum(s=>s.Extent.Length)<=1000000000 && sources.Select(s=>s.Session).Distinct().Count()==1,"Wrong retained cohort.");
        string rowsPath=cohort.RootElement.GetProperty("provenance").GetProperty("selection").GetString();
        var rows=File.ReadLines(rowsPath).Select(line=>JsonSerializer.Deserialize<ReplayOutputInput>(line,Json)).ToArray();
        Require(rows.Length==sources.Length && rows.Select(r=>r.Path).SequenceEqual(sources.Select(s=>s.Path)),"Selection order changed.");
        var segments=sources.Select((s,n)=>new Segment{Path=s.Origin,Start=rows[n].Start,End=rows[n].End}).ToArray();
        string root=plan.GetProperty("output").GetString();using var owner=PilotStorageObservation.Create(root);
        var observations=new List<object>();var whole=Stopwatch.StartNew();
        void Check(){Require(whole.Elapsed.TotalSeconds<75,"Retained-file work deadline.");}
        ReplayCompletedHistory history=null;
        var wholeBefore=Observe();
        try
        {
            var info=new ReplaySessionInfo(sources[0].Session,Path.Combine(sources[0].Session,"segments.csv"),300,20,60,"h264_nvenc",".ts",false,true);
            object Prepare(int count,string label)
            {
                var before=Observe();
                history??=new(info); // Root admission and worker startup are build work.
                while(history.Status.PreparedSources<count || history.Status.Parsing || history.Status.Pending!=0)
                {Check();history.Offer(segments.Take(count));Thread.Sleep(2);}
                var after=Observe();return new{label,resources=Difference(before,after),history=history.Status};
            }
            object Request(string label,bool useHistory,out string outputDigest)
            {
                var before=Observe();var tickets=new List<ReplayGuardDirectory.FileTicket>();
                ReplayCompletedHistory.Snapshot snapshot=null;var registry=new ReplayRootedFiles();
                var indexLife=new ReplayWorkerLifetime();var packetLife=new ReplayWorkerLifetime();
                var acquisition=new ReplayPreparedIndexAcquisition();var batch=new ReplayTransportBatchTiming();var work=new ReplayPacketWorkTiming();
                string digest=null;long protectedAt=0,acquiredAt=0,extractedAt=0;int snapshotCount=0;
                try
                {
                    registry.Root(info.Directory);
                    if(useHistory)snapshot=history.Acquire(segments,Check);
                    snapshotCount=snapshot?.Count??0;
                    var streams=new List<FileStream>();
                    for(int n=0;n<sources.Length;n++)
                    {
                        if(n%32==0)Check();var source=sources[n];
                        if(snapshot?.Contains(source.Origin)==true)registry.AdmitPreparedInput(source.Origin,source.Extent,snapshot);
                        else registry.Admit(source.Origin,source.Extent,false);
                        var ticket=registry.DirectoryOwner(info.Directory).Native.OpenAsyncRead(Path.GetFileName(source.Origin),source.Extent);
                        tickets.Add(ticket);streams.Add(ticket.Data());
                    }
                    var inputs=streams.Select((s,n)=>new ReplayTransportSource(s,sources[n].Extent.Length)).ToArray();
                    protectedAt=Stopwatch.GetTimestamp();
                    var indices=acquisition.Read(inputs,sources.Select(s=>snapshot?.Index(s.Origin)).ToArray(),60,true,Check,batch,indexLife);
                    int expectedHits=label=="prepared-first-one-miss"?300:label=="prepared-repeat"?301:0;
                    Require(acquisition.Complete && acquisition.Hits==expectedHits && acquisition.Misses==301-expectedHits
                        && indexLife.Started==(expectedHits!=301) && indexLife.Joined
                        && snapshotCount==expectedHits,"Retained-file request did not exercise its declared ownership/hit/miss cell.");
                    for(int n=0;n<indices.Length;n++)Require(indices[n].Sha256==sources[n].Sha256,"Retained source differs from frozen expected bytes.");
                    acquiredAt=Stopwatch.GetTimestamp();
                    using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var workers=Enumerable.Range(0,4).Select(_=>new ReplayPacketPrepareWorker()).ToArray();
                    var jobs=streams.Select((s,n)=>{s.Position=0;return new ReplayPacketPrepareInput(s,indices[n],true,true);}).ToArray();
                    int ordinal=0;
                    try
                    {
                        Require(indices.All(ReplayPreparedPackets.Eligible),"Retained input cannot use bounded packet preparation.");
                        ReplayPacketWork.Run(jobs,4,16777216,(job,context)=>workers[context.Worker].Prepare(job,context),body=>body.CapacityBytes,
                            (body,pump)=>{pump();Require(body.SourceHashComplete && body.SourceBytes==indices[ordinal++].Bytes,"Complete source hash omitted.");for(int n=0;n<body.Count;n++)hash.AppendData(body.Payload(body.Event(n)));},
                            Check,work,TimeSpan.FromSeconds(30),packetLife);
                        Require(ordinal==sources.Length && packetLife.Started && packetLife.Joined,"Packet readers did not join every accepted source.");
                        digest=Convert.ToHexString(hash.GetHashAndReset());extractedAt=Stopwatch.GetTimestamp();
                    }
                    finally{if(packetLife.Joined)foreach(var worker in workers)worker.Dispose();}
                }
                finally
                {
                    Require(indexLife.Joined && packetLife.Joined,"Unjoined reader owners stay retained.");
                    foreach(var ticket in tickets)ticket.Dispose();
                    snapshot?.Dispose();registry.Dispose();
                }
                var after=Observe();outputDigest=digest;
                return new{label,resources=Difference(before,after),snapshotCount,acquisition.Hits,acquisition.Misses,
                    parserStarted=indexLife.Started,parserJoined=indexLife.Joined,batch,packetStarted=packetLife.Started,packetJoined=packetLife.Joined,
                    work,acquireInclusiveMilliseconds=Stopwatch.GetElapsedTime(before.Qpc,acquiredAt).TotalMilliseconds,
                    protectionAndOpenMilliseconds=Stopwatch.GetElapsedTime(before.Qpc,protectedAt).TotalMilliseconds,
                    indexAcquisitionMilliseconds=Stopwatch.GetElapsedTime(protectedAt,acquiredAt).TotalMilliseconds,
                    extractionMilliseconds=Stopwatch.GetElapsedTime(acquiredAt,extractedAt).TotalMilliseconds,
                    closeMilliseconds=Stopwatch.GetElapsedTime(extractedAt,after.Qpc).TotalMilliseconds,digest};
            }
            observations.Add(Request("control-first",false,out string first));
            observations.Add(Prepare(300,"prepare-closed-prefix"));
            observations.Add(Request("prepared-first-one-miss",true,out string hit1));
            observations.Add(Prepare(301,"prepare-newest"));
            observations.Add(Request("prepared-repeat",true,out string hit2));
            observations.Add(Request("control-repeat",false,out string last));
            var retireBefore=Observe();history.Dispose();var retireAfter=Observe();
            var retired=history.Status;
            Require(retired.Entries==0 && retired.Pending==0 && !retired.Parsing && retired.OwnedBytes==0 && retired.MetadataBytes==0,
                "History retirement retained an unclosed source or metadata owner.");
            observations.Add(new{label="retirement",resources=Difference(retireBefore,retireAfter),history=history.Status});
            var wholeAfter=Observe();
            Require(first==hit1 && hit1==hit2 && hit2==last && first=="B551E00C606057C4911018B573701FF6A1DFCA8F4F6CA1597B0A4A20C5A4535E","Prepared history changed complete ordered coded payloads.");
            File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{result="passed",observations,resources=Difference(wholeBefore,wholeAfter),seconds=whole.Elapsed.TotalSeconds,
                scope="Retained-file shipping history/admission/acquisition/extraction only; no MP4, actual worker, Saved, percentile, physical-I/O or capture-overhead result."},Json));return 0;
        }
        catch(Exception error)
        {
            File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{result="failed",observations,error=error.ToString(),seconds=whole.Elapsed.TotalSeconds},Json));
            Console.Error.WriteLine(error);return 1;
        }
        finally{history?.Dispose();}
    }
}
