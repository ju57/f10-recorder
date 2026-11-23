using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// Tiny real encoder/file-sharing probe. Generated video only; no desktop,
// audio device, playback, UI, catalog, settings or installed-state access.
internal static class ReplaySourceWitnessProbe
{
    static void Need(bool value,string why){if(!value)throw new IOException(why);}
    internal static int Run(string planFile,string expected)
    {
        Need(new FileInfo(planFile).Length<=65536 && ReplayWorkerLatencyPilot.Hash(planFile)==expected,"Exact source-witness probe plan required.");
        using var doc=JsonDocument.Parse(File.ReadAllBytes(planFile));var p=doc.RootElement;
        Need(p.GetProperty("kind").GetString()=="open-tail-source-witness-probe-v1" && DateTime.UtcNow.AddSeconds(40)<p.GetProperty("expiresUtc").GetDateTime(),"Fresh finite probe required.");
        foreach(var f in p.GetProperty("files").EnumerateObject())Need(ReplayWorkerLatencyPilot.Hash(f.Name)==f.Value.GetString(),"Probe input changed.");
        string root=Path.GetFullPath(p.GetProperty("root").GetString());
        Need(root.StartsWith(Path.GetFullPath("artifacts")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && !Directory.Exists(root),"Fresh task probe root required.");
        MediaDependency.RootDirectory=p.GetProperty("engineRoot").GetString();string engine=MediaDependency.ResolveExecutable();
        Need(p.GetProperty("files").TryGetProperty(engine,out _) && p.GetProperty("files").TryGetProperty(Environment.ProcessPath,out _)
            && p.GetProperty("files").TryGetProperty(typeof(ReplaySourceWitnessProbe).Assembly.Location,out _)
            && p.GetProperty("files").TryGetProperty(typeof(CaptureSession).Assembly.Location,out _)
            && p.GetProperty("files").TryGetProperty(typeof(Settings).Assembly.Location,out _),"Actual probe/engine/runtime must be bound.");
        Directory.CreateDirectory(root);Log.DirectoryPath=root;
        string session=Path.Combine(root,"session-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(session);
        var witness=new ReplayOrdinarySourceWitness(Path.Combine(root,"source-audit"),32);
        var clock=Stopwatch.StartNew();object gate=new();using var owner=Process.GetCurrentProcess();
        long peakRam=0;int peakHandles=0;bool writerObserved=false,pruned=false,retired=false;string failure=null;
        ReplayExportProcessReceipt receipt=null;object child=null;Process encoder=null;Task<ReplayExportProcessReceipt> running=null;
        var rows=new List<object>();using var job=new Native.ProcessJob();
        void Guard()
        {
            lock(gate)
            {
                owner.Refresh();peakRam=Math.Max(peakRam,owner.WorkingSet64);peakHandles=Math.Max(peakHandles,owner.HandleCount);
                Need(clock.Elapsed.TotalSeconds<30 && peakRam<536870912 && peakHandles<4096,"Probe work/process bound.");
                Need(ReplayStorage.ObserveCapacity(root).AvailableBytes>=60000000000L,"Probe free-space floor.");
                long bytes=Directory.GetFiles(root,"*",SearchOption.AllDirectories).Sum(x=>new FileInfo(x).Length);
                Need(bytes<67108864,"Probe64MiB output bound.");
                if(encoder!=null && !encoder.HasExited)
                {
                    encoder.Refresh();
                    Need(encoder.WorkingSet64<2147483648L && encoder.HandleCount<4096,"Generated encoder resource bound.");
                }
            }
        }
        string[] args=["-hide_banner","-loglevel","warning","-y","-re","-f","lavfi","-i","testsrc2=size=640x360:rate=60",
            "-t","8","-an","-c:v","h264_nvenc","-preset","p3","-tune","ll","-rc","cbr","-b:v","2M","-maxrate","2M","-bufsize","4M",
            "-g","60","-bf","0","-forced-idr","1","-force_key_frames","expr:gte(t,n_forced*1)","-flags","+cgop",
            "-f","segment","-segment_time","1","-segment_format","mpegts","-reset_timestamps","0","-segment_list",Path.Combine(session,"segments.csv"),"-segment_list_type","csv",Path.Combine(session,"chunk-%09d.ts")];
        try
        {
            running=Task.Run(()=>ReplayExportProcess.Run(engine,args,root,job,started:x=>{lock(gate){encoder=Process.GetProcessById(x.Id);_=encoder.SafeHandle;child=new{x.Id,startedUtc=x.StartTime.ToUniversalTime(),image=engine};}},
                timeoutMilliseconds:15000,outputLimitBytes:131072,checkStorage:Guard));
            while(!running.IsCompleted)
            {
                Guard();witness.Mirror(session,check:Guard);
                foreach(var a in witness.Aliases.OrderByDescending(x=>x.Path))
                {
                    try{using var reader=new FileStream(a.Path,FileMode.Open,FileAccess.Read,FileShare.Read);}
                    catch(IOException error)when((error.HResult&0xffff)==32)
                    {writerObserved=true;rows.Add(new{control="actual-open-writer-sharing-denial",a.Path,a.CreatedQpc,at=Stopwatch.GetTimestamp()});break;}
                }
                string first=Path.Combine(session,"chunk-000000000.ts"),next=Path.Combine(session,"chunk-000000001.ts");
                if(writerObserved && !pruned && File.Exists(next) && File.Exists(first))
                {
                    string alias=Path.Combine(root,"source-audit","chunk-000000000.ts");
                    string hash=null;
                    try{using var closed=new FileStream(first,FileMode.Open,FileAccess.Read,FileShare.Read);hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(closed));}
                    catch(IOException error)when((error.HResult&0xffff)==32){ }
                    if(hash!=null)
                    {
                        lock(gate)ReplayCacheFileOperations.Prune(session,".ts",next,new HashSet<string>(),()=>{});
                        Need(!File.Exists(first) && File.Exists(alias) && ReplayWorkerLatencyPilot.Hash(alias)==hash,"Ordinary prune lost audit bytes.");
                        pruned=true;rows.Add(new{control="ordinary-live-prune-with-external-alias",first,alias,hash});
                    }
                }
                Thread.Sleep(50);
            }
            receipt=running.GetAwaiter().GetResult();
            Need(receipt.Success && receipt.ProducerAbsent && receipt.ActualExitObserved && receipt.ProcessId>0 && receipt.ProcessStartedUtc!=null,"Generated encoder did not close normally.");
            witness.Mirror(session,allowGap:true,check:Guard);
            Need(writerObserved && pruned && witness.Aliases.Count>=3,"Probe did not exercise actual open writer and pruning.");
            var before=witness.Aliases.Select(x=>new{x.Path,extent=ReplayStorage.Observe(x.Path),sha256=ReplayWorkerLatencyPilot.Hash(x.Path)}).ToArray();
            ReplayCacheFileOperations.DeleteTree(session);Need(!Directory.Exists(session),"Ordinary session retirement blocked on witness aliases.");
            foreach(var a in before)Need(ReplayStorage.Observe(a.Path)==a.extent && ReplayWorkerLatencyPilot.Hash(a.Path)==a.sha256,"Retirement changed audit objects/bytes.");
            retired=true;rows.Add(new{control="ordinary-session-retirement-no-read-handles",aliases=before});
        }
        catch(Exception error){failure=error.ToString();}
        finally
        {
            if(running is{IsCompleted:false})
            {
                try{job.Dispose();if(!running.Wait(5000))failure??="Generated producer/reader closure unconfirmed.";}
                catch(Exception close){failure=(failure??"")+"\nProbe closure: "+close;}
            }
            if(running is{IsCompletedSuccessfully:true})receipt=running.Result;
            encoder?.Dispose();
            ReplayWorkerLatencyPilot.Write(Path.Combine(root,"result.json"),new{result=failure==null?"passed-open-tail-source-witness-probe":"failed",failure,rows,writerObserved,pruned,retired,receipt,child,
                seconds=clock.Elapsed.TotalSeconds,peakRam,peakHandles,mediaChildren=1,desktop=false,systemAudio=false,playback=false,ui=false,
                claim="Generated real-encoder sharing, hardlink and actual cache prune/retirement mechanics only. No ordinary Saved, source lineage, qualification or latency acceptance."});
        }
        return failure==null?0:1;
    }
}
