using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace F10Recorder;

// Tiny structural coded-byte fixtures. No decoder/encoder, desktop, device or
// UI runs; controlled child invocation is intercepted before any process start.
internal static class ReplayCompletedHistoryChecks
{
    static void Require(bool condition,string message){if(!condition)throw new IOException(message);}
    static Exception Failure(Action work){try{work();}catch(Exception error){return error;}throw new IOException("Expected rejection.");}
    static void Wait(Func<bool> ready)
    {var clock=Stopwatch.StartNew();while(!ready()){if(clock.Elapsed.TotalSeconds>5)throw new TimeoutException("Completed-history fixture deadline.");Thread.Sleep(2);}}
    static ReplaySessionInfo Info(string path)=>new(path,Path.Combine(path,"segments.csv"),60,20,60,"h264_nvenc",".ts",false,false);
    static Segment Segment(string root,int n)
    {
        string path=Path.Combine(root,$"chunk-{n:D9}.ts");File.WriteAllBytes(path,ReplayTransportChecks.VideoSequence(60,253500+n*90000L));
        return new(){Path=path,Start=169/60d+n,End=169/60d+n+1};
    }
    sealed class ClosingFault : IDisposable
    {
        internal bool Fail=true;
        public void Dispose(){if(Fail)throw new IOException("Controlled reader close uncertainty.");}
    }
    sealed class Source : IReplaySessionSource
    {
        internal readonly List<Segment> Segments=[];
        public ReplaySessionInfo ReplayInfo {get;}
        public bool PreserveEvidence=>true;
        public bool SupportsOwnedExport=>true;
        public bool OutputProducerStopped=>true;
        public bool Stopped {get;private set;}
        public string Diagnostics=>"Controlled coded-byte source; no native producer.";
        internal bool LateFallback,CloseGuardFailure;
        internal int Fallbacks,Boundaries,Prepared;
        internal bool DependenciesClosed;
        internal ReplayExportRequest Request;
        internal Source(string root)
        {Directory.CreateDirectory(root);ReplayInfo=Info(root) with{ReplaySeconds=1};}
        internal void Add()
        {
            Segments.Add(Segment(ReplayInfo.Directory,Segments.Count));
            File.WriteAllText(ReplayInfo.Manifest,string.Concat(Segments.Select(s=>$"{Path.GetFileName(s.Path)},{s.Start.ToString("R",CultureInfo.InvariantCulture)},{s.End.ToString("R",CultureInfo.InvariantCulture)}\n")));
        }
        public List<Segment> ReadSegments()=>Segments.Select(s=>new Segment{Path=s.Path,Start=s.Start,End=s.End}).ToList();
        public double SaveCutoffAt(long timestamp)=>169/60d+1.25;
        public void FreezeAt(long timestamp){}
        public void StopCapture()=>Stopped=true;
        public void CheckCaptureHealth(double end){}
        public void Dispose(){}
        public ReplayValidation ValidateOutput(string path,ReplayInterval interval,Action<ReplayProducerProof> changed,Action check)
            =>throw new IOException("A fixture failure must not reach validation/publication.");
        public void Export(IReadOnlyList<string> arguments,string snapshot)=>throw new IOException("Request-only fixture.");
        public void Export(ReplayExportRequest request,string snapshot,Action<ReplayProducerProof> changed,Action check)
        {
            Request=request;Prepared=request.PreparedInputs?.Count??0;request.ExternalProducersAbsent=()=>true;
            void Guard(){if(CloseGuardFailure && request.Timing.SourceCloseStarted!=null)throw new IOException("Controlled storage failure after source readers.");check();}
            void Child(IReadOnlyList<string> arguments,Action guard)
            {
                guard();
                if(arguments[^1]!=request.Partial)
                {
                    Boundaries++;
                    using var file=new FileStream(arguments[^1],FileMode.Open,FileAccess.Write,FileShare.ReadWrite);
                    byte[] bytes=ReplayTransportChecks.VideoSequence(45,900000,true);file.Write(bytes);file.SetLength(bytes.Length);return;
                }
                Fallbacks++;
                for(int n=0;n<Segments.Count;n++)
                {
                    string alias=Path.Combine(snapshot,$"{n:D6}.ts");
                    Require(File.Exists(alias),"Fallback concat alias is missing.");
                    Require(File.ReadAllBytes(alias).SequenceEqual(File.ReadAllBytes(Segments[n].Path)),"Fallback alias changed source bytes.");
                    Require(Failure(()=>{using var writer=new FileStream(alias,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);}) is IOException,
                        "Fallback alias was writable before child invocation.");
                }
                Require(request.SourceProtection.Inputs.Count==request.Inputs.Count+1+Prepared,"Prepared alias readers were not owned by the child input lease.");
                if(!CloseGuardFailure)throw new IOException("Controlled fallback invocation reached with protected aliases.");
                using var output=new FileStream(arguments[^1],FileMode.Open,FileAccess.Write,FileShare.ReadWrite);
                output.Write(new byte[4096]);output.SetLength(4096);
            }
            try{ReplaySaveExport.Run(request,Child,Guard,LateFallback?ReplaySaveExport.Construction.PacketCopyBatchCancellable:ReplaySaveExport.Construction.ExactFallback);}
            finally{DependenciesClosed=request.SourceProtection?.DependenciesClosed==true;}
        }
    }
    internal static int Run(string root)
    {
        var passed=new List<string>();var clock=Stopwatch.StartNew();
        using var owner=PilotStorageObservation.Create(root);
        void Check(string name,Action work){work();passed.Add(name);Require(clock.Elapsed.TotalSeconds<60,"Native history control time bound.");}
        try
        {
            string files=Path.Combine(root,"owners");Directory.CreateDirectory(files);
            var first=Segment(files,0);var second=Segment(files,1);
            var history=new ReplayCompletedHistory(Info(files),maximumEntries:1);
            ReplayCompletedHistory.Snapshot held=null;
            Check("immutable-owner-independent-reads-and-private-metadata",()=>
            {
                history.Offer([first]);Wait(()=>history.Status.PreparedSources==1 && !history.Status.Parsing);
                held=history.Acquire([first]);Require(held?.Index(first.Path)!=null,"Prepared native metadata was not available.");
                var copy=held.Index(first.Path);Require(MemoryMarshal.TryGetArray(copy.Sps,out ArraySegment<byte> bytes),"Missing request metadata backing.");
                byte old=bytes.Array[bytes.Offset];bytes.Array[bytes.Offset]^=1;
                using var other=history.Acquire([first]);Require(other.Index(first.Path).Sps.Span[0]==old,"Request mutation escaped to history.");
                using var registry=new ReplayRootedFiles();registry.Root(files);registry.AdmitPreparedInput(first.Path,held.Extent(first.Path),held);
                using var one=registry.DirectoryOwner(files).Native.OpenAsyncRead(Path.GetFileName(first.Path),held.Extent(first.Path));
                using var two=registry.DirectoryOwner(files).Native.OpenAsyncRead(Path.GetFileName(first.Path),held.Extent(first.Path));
                Require(one.Data().ReadByte()==two.Data().ReadByte() && one.Data().Position==1 && two.Data().Position==1,"Request readers shared position.");
                Require(Failure(()=>File.Move(first.Path,first.Path+".moved")) is IOException,"Retained input pathname could be replaced.");
                Require(Failure(()=>{using var write=new FileStream(first.Path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);}) is IOException,"Retained source could be mutated.");
            });
            Check("rolling-owner-allows-retiring-save-hardlink-alias",()=>
            {
                using var registry=new ReplayRootedFiles();registry.Root(files);
                registry.AdmitPreparedInput(first.Path,held.Extent(first.Path),held);
                string aliases=Path.Combine(files,"aliases");registry.CreateDirectory(aliases);
                string alias=Path.Combine(aliases,"000000.ts");
                Require(registry.TryHardLink(alias,first.Path),"Required tiny native hardlink was unavailable.");
                held.Dispose();held=null; // Save readers/refs have actually finished.
                registry.DeleteFile(alias);Require(!File.Exists(alias),"Retiring a save alias failed while rolling history retained the original.");
                registry.DeleteDirectory(aliases);
                held=history.Acquire([first]);Require(held!=null,"Rolling history was consumed by save cleanup.");
            });
            Check("copies-clock-mismatch-and-ended-snapshot-reject",()=>
            {
                string copy=Path.Combine(files,"copy.ts");File.Copy(first.Path,copy);
                Require(history.Acquire([new(){Path=copy,Start=first.Start,End=first.End}])==null,"An unowned copy became a history hit.");
                Require(history.Acquire([new(){Path=first.Path,Start=first.Start+.001,End=first.End}])==null,"Mismatched segment clock became a hit.");
                using var registry=new ReplayRootedFiles();registry.Root(files);
                Require(Failure(()=>registry.AdmitPreparedInput(first.Path,ReplayStorage.Observe(copy),held)) is IOException,"Independent inventory replacement was ignored.");
            });
            Check("pruning-retirement-and-uncertain-reader-retain-owner",()=>
            {
                history.Trim(second.Path);Require(history.Acquire([first])==null,"Evicted entry could be reacquired.");
                history.Offer([second]);Wait(()=>history.Status.Pending==0 && !history.Status.Parsing);
                Require(history.Status.Entries==1 && history.Acquire([second])==null,"Live-entry limit ignored an outstanding request owner.");
                history.Dispose();Require(history.Status.Entries==1,"Retirement closed a borrowed original.");
                var lease=new ReplayExportInputLease();var fault=new ClosingFault();lease.Add(fault);lease.Depend(held);
                Require(Failure(lease.Dispose) is AggregateException && !lease.DependenciesClosed,"Uncertain reader released history dependencies.");
                Require(Failure(()=>File.WriteAllBytes(first.Path,new byte[188])) is IOException,"Unknown reader closure lost source protection.");
                fault.Fail=false;lease.Dispose();Require(lease.DependenciesClosed && history.Status.Entries==0,"Confirmed closure did not release all refs.");
                Require(Failure(()=>held.Index(first.Path)) is ObjectDisposedException,"Disposed snapshot granted metadata.");held=null;
                File.WriteAllBytes(first.Path,ReplayTransportChecks.VideoSequence(60,253500,differentConfiguration:true));
                using var replacement=new ReplayCompletedHistory(Info(files));replacement.Offer([first]);Wait(()=>replacement.Status.PreparedSources==1 && !replacement.Status.Parsing);
                using var fresh=replacement.Acquire([first]);Require(fresh.Index(first.Path).Sps.Span[3]==32,"New session reused retired configuration.");
            });
            Check("metadata-cap-miss-and-cancelled-real-builder",()=>
            {
                using(var capped=new ReplayCompletedHistory(Info(files),maximumMetadata:0))
                {capped.Offer([second]);Wait(()=>capped.Status.PreparedSources==1 && !capped.Status.Parsing);using var miss=capped.Acquire([second]);Require(miss!=null && miss.Index(second.Path)==null && capped.Status.MetadataBytes==0,"Metadata capacity miss was fabricated as a hit.");}
                using var entered=new ManualResetEventSlim();using var cancelled=new ReplayCompletedHistory(Info(files));
                cancelled.BeforeParseForChecks=token=>{entered.Set();token.WaitHandle.WaitOne(2000);token.ThrowIfCancellationRequested();};
                cancelled.Offer([second]);Require(entered.Wait(2000),"Builder never acquired native ownership.");cancelled.Dispose();
                Require(cancelled.Status.Entries==0 && cancelled.Status.PreparedSources==0,"Cancelled builder escaped ownership or published metadata.");
            });
            foreach(bool mixed in new[]{false,true})foreach(bool late in new[]{false,true})foreach(bool copy in new[]{false,true})
                Check($"actual-cache-{(mixed?"mixed":"hits")}-{(late?"late-config":"forced-exact")}-{(copy?"copy":"link")}-fallback",()=>
                {
                    string caseRoot=Path.Combine(root,$"cache-{mixed}-{late}-{copy}");Directory.CreateDirectory(caseRoot);
                    var source=new Source(Path.Combine(caseRoot,"session")){LateFallback=late};source.Add();
                    if(!mixed)source.Add();
                    using var cache=new ReplayCache(cacheDirectory:caseRoot,cacheBudgetBytes:32*1024*1024,diagnostic:_=>{});
                    if(copy)cache.OwnedRequestCreatedForChecks=files=>files.ForceCopyForChecks=true;
                    var id=cache.RegisterSession(source);
                    Wait(()=>{cache.Maintain(id);return cache.CompletedHistoryStatusForChecks(id).PreparedSources==(mixed?1:2);});
                    if(mixed)source.Add();
                    var save=cache.TrySave(id,Stopwatch.GetTimestamp(),Path.Combine(caseRoot,"output"));
                    Require(save.Accepted,save.Rejection);var result=save.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    Require(!result.Succeeded && source.Fallbacks==1 && source.Boundaries==(late?1:0),"Expected integrated fallback callsite was not reached: "+result.Error);
                    Require(source.Prepared==(mixed?1:2) && result.Timing.PreparedFallbackInputs==source.Prepared && source.DependenciesClosed,"Fallback lost prepared ownership/closure facts.");
                    cache.CloseAsync().GetAwaiter().GetResult();
                });
            Check("actual-cache-storage-failure-after-reader-close-releases-dependencies",()=>
            {
                string caseRoot=Path.Combine(root,"close-guard");Directory.CreateDirectory(caseRoot);
                var source=new Source(Path.Combine(caseRoot,"session")){CloseGuardFailure=true};source.Add();source.Add();
                using var cache=new ReplayCache(cacheDirectory:caseRoot,cacheBudgetBytes:32*1024*1024,diagnostic:_=>{});
                var id=cache.RegisterSession(source);Wait(()=>{cache.Maintain(id);return cache.CompletedHistoryStatusForChecks(id).PreparedSources==2;});
                var save=cache.TrySave(id,Stopwatch.GetTimestamp(),Path.Combine(caseRoot,"output"));Require(save.Accepted,save.Rejection);
                var result=save.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Require(!result.Succeeded && result.Timing.SourceHandlesClosed==true && source.DependenciesClosed
                    && result.Timing.SourceCloseError?.Contains("Controlled storage failure")==true,"Storage failure after closed readers leaked request dependencies.");
                cache.CloseAsync().GetAwaiter().GetResult();
                using var writable=new FileStream(source.Segments[0].Path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);
            });
            File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{result="passed",checks=passed,seconds=clock.Elapsed.TotalSeconds},PilotStorageObservation.Json));
            Console.WriteLine($"Passed {passed.Count} native completed-history controls.");return 0;
        }
        catch(Exception error)
        {
            File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{result="failed",checks=passed,error=error.ToString(),seconds=clock.Elapsed.TotalSeconds},PilotStorageObservation.Json));
            Console.Error.WriteLine(error);return 1;
        }
    }
}
