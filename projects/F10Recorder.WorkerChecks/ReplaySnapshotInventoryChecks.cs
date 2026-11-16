using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
namespace F10Recorder;

internal static class ReplaySnapshotInventoryChecks
{
    sealed class NativeFiles : ReplayFiles
    {
        internal int InventoryCalls;
        internal Action BeforePublish;
        internal override void Publish(ReplayExportRequest request, string final) { BeforePublish?.Invoke(); base.Publish(request, final); }
        internal override ReplayNamespaceIdentity ObserveNamespace(string path, ReplayFileExtent enumerated)
            => new(enumerated.Volume, enumerated.Identity);
        internal override ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated)
        { InventoryCalls++; return base.ObserveInventory(path, enumerated); }
    }
    sealed class Source(string session, ReplayVolumeCapacity capacity) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo { get; } = new(session, Path.Combine(session, "segments.csv"), 1, 20, 60, "controlled", ".ts", false, false);
        internal bool Retain = true;
        public bool PreserveEvidence => Retain;
        public bool Stopped { get; private set; }
        public string Diagnostics => "Nonmedia native inventory lifecycle fixture.";
        public bool OutputProducerStopped => true;
        public string OutputDemandIdentity => "native-inventory-control-v1";
        public double SaveCutoffAt(long timestamp) => 1;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() => Stopped = true;
        public void CheckCaptureHealth(double end) { }
        public void Dispose() { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => new(ReplayOutputEvidenceKind.CalibratedEstimate,
            "controlled-not-model", selection.Identity, selection.ExporterIdentity, capacity.Volume, capacity.AllocationUnit, 16*capacity.AllocationUnit, true, null);
        public ReplayValidation ValidateOutput(string path, ReplayInterval interval, Action<ReplayProducerProof> changed, Action check)
            => new("ControlledMetadataFixture", 0, 0);
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        {
            var lease = new ReplayPublicationLease(request.Partial);
            try
            {
                request.FreezeValidatedOutput(lease);
                return new("SIMULATED-NONMEDIA-MetadataFixture", 0, 0, CheckedOutput: lease.CheckedIdentity, PublicationIdentityBound: true);
            }
            catch { lease.Dispose(); throw; }
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Request overload required.");
        internal Action<ReplayExportRequest, Action> Work;
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        { File.WriteAllBytes(request.Partial, new byte[4096]); check(); Work(request, check); }
    }
    internal static int Run(string root)
    {
        using var owner = PilotStorageObservation.Create(root);
        var passed = new List<string>();
        passed.AddRange(ReplayActiveOutputObservationChecks.Run(Path.Combine(root, "active-output")));
        void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
        Dictionary<string,ReplayFileExtent> Scan(string path, ReplaySnapshotInventory reader)
        { reader.BeginScan();try{return ReplayStorage.Inventory([path],reader,32,8);}finally{reader.EndScan();} }
        string live=Path.Combine(root,"live");Directory.CreateDirectory(live);
        string file=Path.Combine(live,"chunk.ts");File.WriteAllBytes(file,new byte[4096]);
        using(var reader=new ReplaySnapshotInventory())
        {
            Scan(live,reader);Require(reader.HandleCount==0,"Growing acquisition file acquired a persistent handle.");
            passed.Add("live-acquisition-uses-short-metadata-leases");
        }
        string snapshot=Path.Combine(root,"save-owned");Directory.CreateDirectory(snapshot);
        string input=Path.Combine(snapshot,"000000.ts");File.WriteAllBytes(input,new byte[4096]);
        using(var reader=new ReplaySnapshotInventory())
        {
            reader.Seal(snapshot);var before=Scan(snapshot,reader)[input];Require(reader.HandleCount==1,"Sealed input handle not retained.");
            using(var writer=new FileStream(input,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){writer.Write(new byte[4096]);writer.Flush(true);}
            var grown=Scan(snapshot,reader)[input];Require(grown.Length==8192&&grown==ReplayStorage.Observe(input),"Allocation/EOF was cached.");
            passed.Add("sealed-handle-still-refreshes-real-allocation-and-EOF");
            string old=input+".old";File.Move(input,old);File.WriteAllBytes(input,new byte[12288]);
            var replaced=Scan(snapshot,reader)[input];Require(replaced.Identity!=before.Identity&&replaced.Length==12288,"Same name retargeted a cached handle.");
            passed.Add("same-name-replacement-revalidates-identity");
            File.Delete(input);Require(!Scan(snapshot,reader).ContainsKey(input)&&reader.HandleCount==0,"Vanished input kept its cached handle.");
            passed.Add("disappearance-releases-unseen-input-handle");
            File.WriteAllBytes(input,new byte[4096]);Scan(snapshot,reader);
            string metadata=Path.Combine(snapshot,"metadata.json");File.WriteAllText(metadata,"first");Scan(snapshot,reader);
            File.WriteAllText(metadata+".pending","second");File.Move(metadata+".pending",metadata,true);Scan(snapshot,reader);
            Require(reader.HandleCount==1,"Atomic metadata acquired a retained handle.");passed.Add("atomic-metadata-is-never-held");
            reader.ReleaseUnder(snapshot);Require(reader.HandleCount==0,"Cleanup retained metadata handles.");Directory.Delete(snapshot,true);
            passed.Add("namespace-retirement-releases-handles-before-removal");
        }
        // CloseAsync is the maintained host shutdown route, not just IDisposable.
        using(var cache=new ReplayCache(cacheDirectory:live)){cache.CloseAsync().GetAwaiter().GetResult();cache.CloseAsync().GetAwaiter().GetResult();}
        passed.Add("empty-native-cache-closeAsync-is-idempotent");
        var atomicBehavior=new Dictionary<string,bool>();var atomicRows=new List<object>();
        foreach(string mode in new[]{"uncached","sealed-snapshot","completed-input"})
        {
            string probe=Path.Combine(root,"atomic-replacement-"+mode);Directory.CreateDirectory(probe);
            string target=Path.Combine(probe,"input.ts"),replacement=target+".replacement";
            File.WriteAllBytes(target,new byte[4096]);File.WriteAllBytes(replacement,new byte[4096]);
            using var reader=new ReplaySnapshotInventory();
            using var selected=mode=="completed-input"?reader.LeaseCompletedInputs([target]):null;
            if(mode=="sealed-snapshot")reader.Seal(probe);
            Scan(probe,reader);int handles=reader.HandleCount;bool allowed=false;string error=null;int? hresult=null;
            SafeHandle held=null;
            if(handles!=0)
            {
                var entries=(System.Collections.IDictionary)typeof(ReplaySnapshotInventory).GetField("handles",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(reader);
                held=(SafeHandle)entries[target].GetType().GetProperty("Handle").GetValue(entries[target]);
            }
            using(var append=new FileStream(target,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){append.Write(new byte[8192]);append.Flush(true);}
            Scan(probe,reader);var beforeAtomic=ReplayStorage.Observe(target);
            try{File.Move(replacement,target,true);allowed=true;}
            catch(Exception ex)when(ex is UnauthorizedAccessException or IOException){error=ex.ToString();hresult=ex.HResult;}
            var afterAtomic=ReplayStorage.Observe(target);
            selected?.Dispose();reader.Dispose();
            bool? afterClose=null;string afterCloseError=null;
            if(!allowed)
            {
                try{File.Move(replacement,target,true);afterClose=true;}
                catch(Exception ex)when(ex is UnauthorizedAccessException or IOException){afterClose=false;afterCloseError=ex.ToString();}
            }
            atomicBehavior.Add(mode,allowed);atomicRows.Add(new{mode,handles,beforeAtomic,allowed,error,hresult,afterAtomic,
                afterClose,afterCloseError,oldHandleClosed=held?.IsClosed,final=ReplayStorage.Observe(target)});
            passed.Add("atomic-overwrite-behavior-recorded-"+mode);
        }
        File.WriteAllText(Path.Combine(root,"atomic-replacement-semantics.json"),JsonSerializer.Serialize(atomicRows,new JsonSerializerOptions{WriteIndented=true}));
        Require(atomicBehavior["sealed-snapshot"]==atomicBehavior["completed-input"],
            "Experimental metadata observer changed atomic replacement behavior beyond existing sealed-reader semantics.");
        string scoped=Path.Combine(root,"completed-scope");Directory.CreateDirectory(scoped);
        string complete=Path.Combine(scoped,"complete.ts"),tail=Path.Combine(scoped,"tail.ts");
        File.WriteAllBytes(complete,new byte[4096]);File.WriteAllBytes(tail,new byte[4096]);
        using(var reader=new ReplaySnapshotInventory())
        {
            SafeHandle Held(string path)
            {
                var entries=(System.Collections.IDictionary)typeof(ReplaySnapshotInventory).GetField("handles",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(reader);
                return (SafeHandle)entries[path].GetType().GetProperty("Handle").GetValue(entries[path]);
            }
            var first=reader.LeaseCompletedInputs([complete]);var second=reader.LeaseCompletedInputs([complete]);
            Scan(scoped,reader);Require(reader.HandleCount==1,"Unselected current tail acquired a retained handle.");
            var original=Held(complete);passed.Add("completed-scope-excludes-unselected-current-tail");
            first.Dispose();first.Dispose();Require(!original.IsClosed&&reader.HandleCount==1,"One scope released another scope's observer.");
            passed.Add("overlapping-completed-scopes-retain-native-owner");
            using(var append=new FileStream(complete,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){append.Write(new byte[8192]);append.Flush(true);}
            var grown=Scan(scoped,reader)[complete];var fresh=ReplayStorage.Inventory([scoped],new ReplayFiles())[complete];
            Require(grown.Length==12288&&grown.AllocatedBytes==fresh.AllocatedBytes,"Completed-scope reader reused stale native allocation/EOF.");
            passed.Add("completed-scope-refreshes-growth-allocation-and-EOF");
            // Use the existing sealed-reader control's rename-aside+recreate
            // operation. Atomic overwrite semantics are measured separately.
            string replacement=complete+".replacement";File.WriteAllBytes(replacement,new byte[4096]);
            File.Move(complete,complete+".old");File.Move(replacement,complete);
            var replaced=Scan(scoped,reader)[complete];Require(original.IsClosed&&replaced.Identity!=grown.Identity,"Replacement retained the old observer object.");
            var replacementHandle=Held(complete);passed.Add("completed-scope-replacement-closes-old-object");
            File.Delete(complete);Scan(scoped,reader);Require(replacementHandle.IsClosed&&reader.HandleCount==0,"Disappeared completed input retained its observer.");
            passed.Add("completed-scope-disappearance-closes-native-object");
            File.WriteAllBytes(complete,new byte[4096]);Scan(scoped,reader);var finalHandle=Held(complete);
            second.Dispose();Require(finalHandle.IsClosed&&reader.HandleCount==0,"Final completed-input scope retained native ownership.");
            passed.Add("completed-scope-final-release-closes-held-handle");
        }
        string pruneRoot=Path.Combine(root,"completed-original-unlink"),pruneSnapshot=Path.Combine(pruneRoot,"snapshot");
        Directory.CreateDirectory(pruneSnapshot);
        string pruneOriginal=Path.Combine(pruneRoot,"complete.ts"),pruneCopy=Path.Combine(pruneSnapshot,"input.ts");
        File.WriteAllBytes(pruneOriginal,new byte[4096]);Require(Native.CreateHardLink(pruneCopy,pruneOriginal,IntPtr.Zero),"Unlink fixture hardlink failed.");
        using(var reader=new ReplaySnapshotInventory())
        using(var selected=reader.LeaseCompletedInputs([pruneOriginal]))
        {
            reader.Seal(pruneSnapshot);var initial=Scan(pruneRoot,reader)[pruneCopy];
            Require(reader.HandleCount==2,"Unlink fixture did not retain both native observers.");
            File.Delete(pruneOriginal);var remaining=Scan(pruneRoot,reader);
            Require(!remaining.ContainsKey(pruneOriginal)&&remaining[pruneCopy].Identity==initial.Identity&&reader.HandleCount==1,
                "Ordinary original-name unlink invalidated protected snapshot or retained the deleted observer.");
            passed.Add("completed-original-unlink-preserves-snapshot-and-releases-observer");
        }
        foreach(bool optin in new[]{false,true})
        foreach(bool fail in new[]{false,true})
        foreach(bool? reuse in optin?new bool?[]{false,true,null}:new bool?[]{false,null})
        {
            string cell=Path.Combine(root,$"cache-{optin}-{fail}-{reuse}"),buffer=Path.Combine(cell,"buffer"),session=Path.Combine(buffer,"session"),clips=Path.Combine(cell,"clips");
            Directory.CreateDirectory(session);Directory.CreateDirectory(clips);
            File.WriteAllBytes(Path.Combine(session,"chunk-000000000.ts"),new byte[4096]);
            File.WriteAllText(Path.Combine(session,"segments.csv"),"chunk-000000000.ts,0,1\n");
            var source=new Source(session,ReplayStorage.ObserveCapacity(cell));var files=new NativeFiles();
            using var cache=new ReplayCache(files,cacheDirectory:buffer,fileMode:optin ? ReplayFileMode.NativeInventoryForChecks : ReplayFileMode.Conservative,reuseCompletedInputMetadata:reuse);
            SafeHandle[] retained=[],originalObservers=[];
            files.BeforePublish=()=>Require(originalObservers.All(h=>h.IsClosed),"Original-input metadata observers survived publication.");
            source.Work=(request,check)=>
            {
                Require((cache.NativeSnapshotHandleCountForChecks>0)==optin,"Explicit reader selection did not match opened snapshot handles.");
                if(!optin)Require(files.InventoryCalls>0,"Custom default provider's inventory override was bypassed.");
                string selected=request.Inputs[0].Path;
                using(var growth=new FileStream(selected,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){growth.Write(new byte[4096]);growth.Flush(true);}
                long actual=ReplayStorage.Bytes(ReplayStorage.Inventory([buffer],new ReplayFiles()));
                Require(cache.Status.CacheBytes==actual,"Cache reused stale snapshot allocation/EOF after native growth.");
                if(optin)
                {
                    // Keep actual SafeHandle references across Cache shutdown;
                    // an empty dictionary alone would not prove native disposal.
                    var reader=typeof(ReplayCache).GetField("snapshotInventory",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(cache);
                    var entries=(System.Collections.IDictionary)typeof(ReplaySnapshotInventory).GetField("handles",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(reader);
                    retained=entries.Values.Cast<object>().Select(e=>(SafeHandle)e.GetType().GetProperty("Handle").GetValue(e)).ToArray();
                    Require(retained.Length>0&&retained.All(h=>!h.IsClosed),"No live native snapshot handle was captured.");
                    var originalEntry=entries[Path.Combine(session,"chunk-000000000.ts")];
                    originalObservers=originalEntry==null?[]:[(SafeHandle)originalEntry.GetType().GetProperty("Handle").GetValue(originalEntry)];
                    Require(originalObservers.Length==((reuse??optin)?1:0),"Original-input reader eligibility changed.");
                }
                check();if(fail)throw new IOException("controlled-native-inventory-export-failure");
            };
            var sessionId=cache.RegisterSession(source);cache.Maintain(sessionId);
            Require((cache.NativeSnapshotHandleCountForChecks??0)==0,"Pre-save capture maintenance retained input handles.");
            var submission=cache.TrySave(sessionId,0,clips);Require(submission.Accepted,submission.Rejection);
            var result=submission.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Require(result.Succeeded!=fail&&(!fail||result.Error.Contains("controlled-native-inventory-export-failure")),"Native lifecycle fixture outcome changed: "+result.Error);
            Require(originalObservers.All(h=>h.IsClosed),"Completed source observer survived accepted-save completion.");
            cache.CloseAsync().GetAwaiter().GetResult();
            Require((cache.NativeSnapshotHandleCountForChecks??0)==0&&retained.All(h=>h.IsClosed),"Successful/failing Cache teardown retained native snapshot handles.");
            // External fixture retirement follows confirmed native-owner release.
            Directory.Delete(session,true);
            passed.Add($"custom-native-{(optin?"explicit-reader":"default-overrides")}-{(fail?"failure":"success")}-original-reuse-{reuse?.ToString()??"default"}-fresh-extents-and-closed-handles");
        }
        foreach(bool fail in new[]{false,true})
        {
            string cell=Path.Combine(root,"native-default-normal-cleanup-"+fail),buffer=Path.Combine(cell,"buffer"),session=Path.Combine(buffer,"session"),clips=Path.Combine(cell,"clips");
            Directory.CreateDirectory(session);Directory.CreateDirectory(clips);
            string original=Path.Combine(session,"chunk-000000000.ts");File.WriteAllBytes(original,new byte[4096]);
            File.WriteAllText(Path.Combine(session,"segments.csv"),"chunk-000000000.ts,0,1\n");
            var source=new Source(session,ReplayStorage.ObserveCapacity(cell)){Retain=false};
            SafeHandle[] originals=[],all=[];bool? originalsClosedAtSaved=null;
            using var cache=new ReplayCache(diagnostic:message=>{if(message.StartsWith("Saved ",StringComparison.Ordinal))originalsClosedAtSaved=originals.All(h=>h.IsClosed);},cacheDirectory:buffer);
            var id=cache.RegisterSession(source);cache.Maintain(id);
            Require(cache.NativeSnapshotHandleCountForChecks==0,"Native default moved observer opening into capture maintenance.");
            source.Work=(request,check)=>{
                var reader=typeof(ReplayCache).GetField("snapshotInventory",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(cache);
                var entries=(System.Collections.IDictionary)typeof(ReplaySnapshotInventory).GetField("handles",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(reader);
                var originalEntry=entries[original];Require(originalEntry!=null,"Actual native default did not select completed-input reuse.");
                originals=[(SafeHandle)originalEntry.GetType().GetProperty("Handle").GetValue(originalEntry)];
                all=entries.Values.Cast<object>().Select(e=>(SafeHandle)e.GetType().GetProperty("Handle").GetValue(e)).ToArray();
                check();if(fail)throw new IOException("controlled-native-default-failure");
            };
            var submission=cache.TrySave(id,0,clips);Require(submission.Accepted,submission.Rejection);
            var result=submission.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Require(result.Succeeded!=fail&&(!fail||result.Error.Contains("controlled-native-default-failure")),"Native default outcome changed: "+result.Error);
            Require(originals.Length==1&&all.All(h=>h.IsClosed)&&cache.NativeSnapshotHandleCountForChecks==0,
                "Ordinary cleanup retained completed-original or snapshot handles after save completion.");
            if(!fail)Require(originalsClosedAtSaved==true,"Actual native default published before original metadata observers closed.");
            passed.Add("actual-native-default-no-capture-migration-normal-cleanup-"+(fail?"failure":"success"));
        }
        File.WriteAllText(Path.Combine(root,"verification.json"),JsonSerializer.Serialize(new{result="passed",passed,effects="Tiny private native metadata fixtures only; no engine, child, capture, audio, model or user-state change."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Snapshot metadata checks: {passed.Count}/{passed.Count} passed");return 0;
    }
}
