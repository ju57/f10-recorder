using System.Diagnostics;
namespace F10Recorder;
internal static class SessionCacheIntegrationChecks
{
    static void Need(bool value,string why){if(!value)throw new IOException(why);}
    sealed class Source(string directory,string output,ReplayRootedFiles files,byte[] template,ReplayMp4Expectation proof,string fault,Action bound) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo {get;}=new(directory,Path.Combine(directory,"segments.csv"),1,20,60,"h264_nvenc",".ts",false,true);
        readonly ReplayExportLifetime lifetime=new(); internal bool Injected;
        internal Action AfterValidation;internal Action BeforeValidation;
        public bool Stopped=>true;public bool OutputProducerStopped=>lifetime.Stopped;public string Diagnostics=>"Synthetic nonplayable completeCache fixture, no capture/codec.";
        public string OutputDemandIdentity=>"TEST-native-cache-synthetic-v1";
        public double SaveCutoffAt(long timestamp)=>1;public void FreezeAt(long timestamp){} public void StopCapture(){}public void CheckCaptureHealth(double end){}public void Dispose(){}
        public List<Segment> ReadSegments()=>files.ReadSegments(directory);
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {var capacity=ReplayStorage.ObserveCapacity(output);return new(ReplayOutputEvidenceKind.SourceBound,"TEST-synthetic-byte-ceiling",selection.Identity,selection.ExporterIdentity,capacity.Volume,capacity.AllocationUnit,262144,false,"Tiny explicit synthetic byte ceiling only.",null,new(4096,4096));}
        public void Export(IReadOnlyList<string> args,string snapshot)=>throw new IOException("Request route required.");
        public void Export(ReplayExportRequest request,string snapshot,Action<ReplayProducerProof> changed,Action check)
        {
            using var active=lifetime.Begin(); try {
            request.SelectRecipe(ReplaySaveExport.PacketRecipe);request.FreezeIntegrity(proof);
            using(var input=request.NativeFiles.OpenInput(request.Inputs[0].Path,request.Inputs[0].Extent,true))
            {byte[] bytes=new byte[4096];input.ReadExactlyAsync(bytes).AsTask().GetAwaiter().GetResult();Need(bytes.All(b=>b==0x31),"Protected source input changed.");}
            if(fault=="workspace")
            {request.NativeFiles.CreateWorkspace();request.OwnWorkDirectory=true;files.WriteText(Path.Combine(request.WorkDirectory,"unknown.bin"),"foreign-sentinel");check();}
            if(fault=="reserved-workspace") { files.CreateDirectory(request.WorkDirectory); check(); }
            byte[] data=template.ToArray();if(fault=="corrupt")data[^5]^=1;
            if(fault=="drift")data=(byte[])typeof(global::ReplayMp4Checks).GetMethod("Fixture",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,new object[]{"drift"});
            request.Closure.BeginPacketMux();
            using(var writer=request.NativeFiles.CreateMain())
            {
                writer.Write(data);writer.Flush();
                if(fault=="alias")
                {var root=files.DirectoryOwner(output).Native;using var read=root.OpenObservation(Path.GetFileName(request.Partial),ReplayStorage.Observe(request.Partial));read.LinkTo(files.DirectoryOwner(directory).Native,"alias.ts");Injected=true;}
                if(fault=="expiry"){Injected=true;Thread.Sleep(ReplayCapacityPolicy.CoverageMilliseconds+10);}
                for(int i=0;i<4;i++){check();bound();Thread.Sleep(2);}
            }
            using var process=Process.GetCurrentProcess();var actual=ReplayStorage.Observe(request.Partial);
            request.FreezeManagedMux(new(Environment.ProcessId,process.StartTime.ToUniversalTime(),true,true,"Completed",1,template.Length,0,0,actual,null,null,null,65536,0,new(),new(),0,0));
            if(fault=="collision")files.WriteText(request.Partial[..^".partial.mp4".Length],"foreign-collision");
            } finally {
                if(request.Closure.PacketStarted && request.ManagedMux==null) { using var parent=Process.GetCurrentProcess(); request.FreezeManagedMux(new(Environment.ProcessId,parent.StartTime.ToUniversalTime(),request.NativeFiles.MainWriterClosed,false,"Poisoned",0,0,0,0,null,"controlled source failure",null,null,0,0,new(),new(),0,0)); }
                lifetime.ObserveRequest(request);
            }
        }
        public ReplayValidation ValidateOutput(ReplayExportRequest request,Action<ReplayProducerProof> changed,Action check)
        {using var active=lifetime.Begin();try{BeforeValidation?.Invoke();var result=ReplayOutputIntegrity.Read(request,check,true);AfterValidation?.Invoke();return new(ReplayOutputIntegrity.Kind,0,0,result.CodedPictures,result.AudioPackets,request.Recipe,request.Integrity.Fingerprint,request.ValidatedOutput.CheckedIdentity);}finally{lifetime.ObserveRequest(request);}}
    }
    internal static void Run(string root,Action<string,object> passed,Action bound,Action<string,string> junction,Action<string> removeJunction)
    {
        Directory.CreateDirectory(root);byte[] template=global::ReplayMp4Checks.PacketMuxFixture();var interval=new ReplayInterval(0,1,1);
        var indexed=ReplayMp4Reader.Read(new MemoryStream(template),template.Length,interval,60,true);
        var video=indexed.Tracks.Single(t=>t.Kind=="vide");var audio=indexed.Tracks.Single(t=>t.Kind=="soun");
        var proof=new ReplayMp4Expectation(60,0,video.Sps,video.Pps,video.Samples.Select(s=>new ReplayExpectedPicture(s.Idr,s.CodedSha256,s.PacketSha256)),1024,audio.Samples.Select(s=>new ReplayExpectedAudio(s.Duration,s.PayloadSha256)));
        foreach(string fault in new[]{"healthy","corrupt","drift","copy-fallback","alias","workspace","reserved-workspace","expiry","collision","protect-redirect","cleanup-redirect","publication-redirect","authority-close-expiry","release-retry","release-close","admission-persist-fault"})
        {
            string folder=Path.Combine(root,fault),buffer=Path.Combine(folder,"buffer"),session=Path.Combine(buffer,"session"),output=Path.Combine(folder,"output");
            Directory.CreateDirectory(session);Directory.CreateDirectory(output);string media=Path.Combine(session,"000000.ts"),manifest=Path.Combine(session,"segments.csv");
            File.WriteAllBytes(media,Enumerable.Repeat((byte)0x31,4096).ToArray());File.WriteAllText(manifest,"000000.ts,0,1\n");
            using var files=new ReplayRootedFiles();files.Root(buffer);files.Child(session,true);files.Root(output);
            files.Admit(media,ReplayStorage.Observe(media),true);files.Admit(manifest,ReplayStorage.Observe(manifest),true);
            string foreign=Path.Combine(folder,"foreign");Directory.CreateDirectory(foreign);string sentinel=Path.Combine(foreign,"sentinel.bin");File.WriteAllText(sentinel,"foreign-sentinel");
            var foreignExpected=ReplayStorage.Observe(sentinel);
            void ForeignUnchanged(){var entries=Directory.GetFileSystemEntries(foreign);Need(entries.Length==1&&entries[0]==sentinel,"Redirected operation created/deleted a foreign leaf.");var current=ReplayStorage.Observe(sentinel);Need(current.Identity==foreignExpected.Identity&&current.Length==foreignExpected.Length,"Foreign native object changed.");}
            bool redirected=false,cleanupReported=false,restoreActive=false,publicationApplied=false,publicationPrevented=false;string redirectedPath=null;
            if(fault=="protect-redirect") files.DirectoryCreatedForChecks=(path,node)=>
            {
                if(!Path.GetFileName(path).StartsWith("save-"))return;
                node.Native.BeforeNativeMutationForChecks=()=>{junction(path,foreign);redirected=true;restoreActive=true;redirectedPath=path;};
                node.Native.AfterNativeMutationForChecks=()=>{try{ForeignUnchanged();}finally{if(restoreActive){removeJunction(path);restoreActive=false;}}};
            };
            if(fault=="copy-fallback")files.DirectoryCreatedForChecks=(path,node)=>
            {if(Path.GetFileName(path).StartsWith("save-"))node.Native.BeforeNativeMutationForChecks=()=>{node.Native.BeforeNativeMutationForChecks=null;throw new System.ComponentModel.Win32Exception(17);};};
            if(fault=="cleanup-redirect")
            {
                files.Registry.BeforeEmptyDirectoryDeleteForChecks=node=>{if(node.Leaf?.StartsWith("save-")!=true)return;redirectedPath=node.Native.OriginalPath;junction(redirectedPath,foreign);redirected=true;restoreActive=true;};
                files.Registry.AfterEmptyDirectoryDeleteForChecks=node=>{if(restoreActive){removeJunction(redirectedPath);restoreActive=false;}};
            }
            bool saved=false;int polls=0;ReplaySaveResult result;bool rejectRelease=fault is "release-retry" or "release-close";var reservations=new ReplayVolumeReservations(Path.Combine(folder,"authority"),60000000000);if(rejectRelease)reservations.BeforeReleaseForChecks=()=>{if(rejectRelease)throw new IOException("Controlled durable release failure");};if(fault=="authority-close-expiry")reservations.BeforeRequestCloseForChecks=()=>Thread.Sleep(ReplayCapacityPolicy.CoverageMilliseconds+100);
            using(var cache=new ReplayCache(files:files,cacheDirectory:buffer,diagnostic:message=>{if(message.StartsWith("Saved "))saved=true;if(message.Contains("cleanup failed"))cleanupReported=true;},volumeReservations:reservations,fileMode:ReplayFileMode.OwnedSessionForChecks))
            {
                bool metadataClosedBeforeValidation=false;cache.StorageObservedForChecks=trace=>{if(trace.Scope=="CompletedInputMetadataClose")metadataClosedBeforeValidation=true;};var source=new Source(session,output,files,template,proof,fault,bound);source.BeforeValidation=()=>Need(metadataClosedBeforeValidation,"Completed-original metadata observer was not closed at joined export handoff");var id=cache.RegisterSession(source);if(fault=="admission-persist-fault"){int admissionThread=Environment.CurrentManagedThreadId;bool armed=true;reservations.AfterUpdatePersistForChecks=()=>{if(armed&&Environment.CurrentManagedThreadId==admissionThread){armed=false;throw new IOException("Controlled failure AFTER durable Update persist");}};}
                if(fault=="publication-redirect")source.AfterValidation=()=>files.DirectoryOwner(output).Native.BeforeNativeMutationForChecks=()=>
                {
                    try{junction(output,foreign);publicationApplied=true;restoreActive=true;redirectedPath=output;}
                    catch(System.ComponentModel.Win32Exception error)when(error.NativeErrorCode is 5 or 32 or 145){publicationPrevented=true;}
                    finally{if(restoreActive){removeJunction(output);restoreActive=false;}}
                    redirected=publicationApplied||publicationPrevented;ForeignUnchanged();throw new IOException(publicationApplied?"Applied/restored publication-parent redirect stopped before operation.":"Prevented publication-parent redirect.");
                };
                using var stop=new CancellationTokenSource();
                var status=Task.Run(()=>{while(!stop.IsCancellationRequested){_ = cache.Status;Interlocked.Increment(ref polls);Thread.Sleep(1);}});
                try
                {
                    var submission=cache.TrySave(id,Stopwatch.GetTimestamp(),output);if(fault=="admission-persist-fault"){Need(!submission.Accepted&&!saved&&!cache.AuthoritySessionOwnedForChecks,"Admission partialcommit leaked authority or reported Saved");using(var released=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"authority","replay-volume-reservations.json"))))Need(released.RootElement.GetProperty("Owners").GetArrayLength()==0,"Partially persisted admission holder was not durably released");submission=cache.TrySave(id,Stopwatch.GetTimestamp(),output);}Need(submission.Accepted,submission.Rejection);
                    // Never unwind a live Cache job. The bounded outer owner
                    // terminates this task-owned helper if a lock deadlocks.
                    result=submission.Completion.GetAwaiter().GetResult();
                }
                finally{stop.Cancel();status.GetAwaiter().GetResult();}
                Need(polls>0,"Concurrent Cache status did not execute.");
                if(fault is "healthy" or "cleanup-redirect" or "copy-fallback" or "admission-persist-fault")Need(result.Succeeded&&saved&&result.Validation.PublicationIdentityBound,"Native completeCache positive failed: "+result.Error);
                else Need(!result.Succeeded&&!saved,"Native completeCache fault reported Saved: "+fault);
                if(fault is "release-retry" or "release-close"){Need(cache.AuthoritySessionOwnedForChecks,"Failed durable release dropped authority session");rejectRelease=false;if(fault=="release-retry"){var retry=cache.TrySave(id,Stopwatch.GetTimestamp(),output);Need(retry.Accepted,retry.Rejection);var done=retry.Completion.GetAwaiter().GetResult();Need(done.Succeeded&&saved&&!cache.AuthoritySessionOwnedForChecks,"Release retry failed to retire old ownership before new save");}else{cache.CloseAsync().GetAwaiter().GetResult();Need(!cache.AuthoritySessionOwnedForChecks,"CloseAsync did not retire released authority session");}}if(fault=="authority-close-expiry")Need(result.Error?.Contains(ReplayCapacityPolicy.CoverageMilliseconds.ToString())==true&&!Directory.GetFiles(output,"*.mp4").Any(p=>!p.EndsWith(".partial.mp4")),"Delayed authority closure published or failed for another cause");
                if(fault is "alias" or "expiry")Need(source.Injected,"Fault site was not reached: "+fault);
                if(fault=="alias")Need(result.Error?.Contains("aliases retained")==true,"Alias was rejected for another reason: "+result.Error);
                if(fault=="expiry")Need(result.Error?.Contains(ReplayCapacityPolicy.CoverageMilliseconds.ToString())==true,"Expiry was rejected for another reason: "+result.Error);
                if(fault=="copy-fallback")Need(result.Timing.CopiedInputs==1&&result.Timing.HardLinkedInputs==0,"Actual Cache copy fallback did not execute.");
                if(fault.EndsWith("redirect"))Need(redirected&&!restoreActive,"Redirect was neither exercised/prevented nor restored.");
                if(fault=="cleanup-redirect")Need(cleanupReported,"Uncertain cleanup was silently reported as normal.");
            }
            if(fault=="healthy")Need(!Directory.GetDirectories(output,"*.work").Any()&&Directory.GetFileSystemEntries(buffer).Length==0,"Native normal cleanup was incomplete.");
            if(fault=="workspace")Need(Directory.GetFiles(output,"unknown.bin",SearchOption.AllDirectories).Any(),"Uncertain workspace evidence was not retained.");
            if(fault=="collision")Need(Directory.GetFiles(output,"*.mp4").Any(p=>new FileInfo(p).Length==System.Text.Encoding.UTF8.GetByteCount("foreign-collision")),"Collision destination was changed.");
            Need(File.ReadAllText(sentinel)=="foreign-sentinel","Actual Cache operation touched redirected foreign data.");
            ForeignUnchanged();bound();passed("session-complete-cache-"+fault,new{result.Succeeded,result.Error,polls,saved,redirected,cleanupReported,publicationApplied,publicationPrevented});
        }
        {
            string folder=Path.Combine(root,"uncertain-scope");Directory.CreateDirectory(folder);
            using var files=new ReplayRootedFiles();files.Root(folder);
            var request=new ReplayExportRequest(new(folder,Path.Combine(folder,"segments.csv"),1,20,60,"h264_nvenc",".ts",false,true),interval,[],"unused",Path.Combine(folder,"main.mp4"),new());
            request.IndexBatch=new(){Workers=1,Joined=true};request.IndexReaders.Begin();var scope=new ReplayNativeExportScope(files,request);
            Need(!scope.CanDispose,"Unknown source batch became disposable.");bool rejected=false;
            try{scope.Dispose();}catch(IOException){rejected=true;}Need(rejected,"Direct unsafe scope disposal was accepted.");
            request.IndexBatch.Joined=true;Need(!scope.CanDispose,"Diagnostic join released active readers.");request.IndexReaders.Complete(true);Need(!scope.CanDispose,"Later healthy evidence revived uncertain ownership.");
            // No IO task, stream or output was created in this simulated
            // unknown-receipt control; the independent root owner can close.
            passed("session-unknown-scope-retains-ownership-despite-later-healthy-receipt",null);
        }
    }
}

