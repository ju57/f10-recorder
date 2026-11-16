using System.Diagnostics;
using System.Text.Json.Nodes;
namespace F10Recorder;

internal static class CacheAnchorChecks
{
    static void Need(bool value,string why){if(!value)throw new IOException(why);}
    sealed class Source(string session,string output,byte[] template,ReplayMp4Expectation proof):IReplaySessionSource
    {
        readonly ReplayExportLifetime lifetime=new();
        internal Action<ReplayExportRequest> DuringWriter;
        public ReplaySessionInfo ReplayInfo {get;}=new(session,Path.Combine(session,"segments.csv"),1,20,60,"h264_nvenc",".ts",false,true);
        public bool SupportsOwnedExport=>true;
        public bool Stopped=>true;
        public bool OutputProducerStopped=>lifetime.Stopped;
        public string Diagnostics=>"Synthetic Cache anchor regression; no capture/codec/device";
        public string OutputDemandIdentity=>"TEST-ancestor-anchor-cache";
        public double SaveCutoffAt(long timestamp)=>1;
        public void FreezeAt(long timestamp){}
        public void StopCapture(){}
        public void CheckCaptureHealth(double end){}
        public void Dispose(){}
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            var capacity=ReplayStorage.ObserveCapacity(output);
            return new(ReplayOutputEvidenceKind.SourceBound,"TEST-synthetic-only",selection.Identity,selection.ExporterIdentity,capacity.Volume,capacity.AllocationUnit,262144,false,"No model qualification",null,new(4096,4096));
        }
        public void Export(IReadOnlyList<string> args,string snapshot)=>throw new IOException("Request route required");
        public void Export(ReplayExportRequest request,string snapshot,Action<ReplayProducerProof> changed,Action check)
        {
            using var activity=lifetime.Begin();
            try {
                request.SelectRecipe(ReplaySaveExport.PacketRecipe);request.FreezeIntegrity(proof);
                request.Closure.BeginPacketMux();
                using(var writer=request.NativeFiles.CreateMain()){writer.Write(template);writer.Flush();DuringWriter?.Invoke(request);check();}
                using var self=Process.GetCurrentProcess();
                request.FreezeManagedMux(new(Environment.ProcessId,self.StartTime.ToUniversalTime(),true,true,"Completed",1,4096,0,0,ReplayStorage.Observe(request.Partial),null,null,null,65536,0,new(),new(),0,0));
            }finally{lifetime.ObserveRequest(request);}
        }
        public ReplayValidation ValidateOutput(ReplayExportRequest request,Action<ReplayProducerProof> changed,Action check)
        {
            using var activity=lifetime.Begin();try{var result=ReplayOutputIntegrity.Read(request,check,true);return new(ReplayOutputIntegrity.Kind,0,0,result.CodedPictures,result.AudioPackets,request.Recipe,request.Integrity.Fingerprint,request.ValidatedOutput.CheckedIdentity);}finally{lifetime.ObserveRequest(request);}
        }
    }
    internal static object Run(string root)
    {
        Directory.CreateDirectory(root);byte[] template=global::ReplayMp4Checks.PacketMuxFixture();var parsed=ReplayMp4Reader.Read(new MemoryStream(template),template.Length,new(0,1,1),60,true);
        var video=parsed.Tracks.Single(t=>t.Kind=="vide");var audio=parsed.Tracks.Single(t=>t.Kind=="soun");var proof=new ReplayMp4Expectation(60,0,video.Sps,video.Pps,video.Samples.Select(s=>new ReplayExpectedPicture(s.Idr,s.CodedSha256,s.PacketSha256)),1024,audio.Samples.Select(s=>new ReplayExpectedAudio(s.Duration,s.PayloadSha256)));
        var rows=new List<object>();
        foreach(string mode in new[]{"healthy","peer-fault","producer-fault"})
        {
            bool fault=mode!="healthy";
            string folder=Path.Combine(root,mode),buffer=Path.Combine(folder,"buffer"),session=Path.Combine(buffer,"session"),output=Path.Combine(folder,"output"),authority=Path.Combine(folder,"authority");Directory.CreateDirectory(session);
            File.WriteAllBytes(Path.Combine(session,"chunk-000000000.ts"),Enumerable.Repeat((byte)0x31,4096).ToArray());File.WriteAllText(Path.Combine(session,"segments.csv"),"chunk-000000000.ts,0,1\n");
            var reservations=new ReplayVolumeReservations(authority);var peer=reservations.CreatePeer();
            bool saved=false,maintenanceFailed=false;string maintenanceError=null;using var cache=new ReplayCache(cacheDirectory:buffer,cacheBudgetBytes:1048576,diagnostic:m=>{if(m.StartsWith("Saved ",StringComparison.Ordinal))saved=true;},volumeReservations:reservations);
            var source=new Source(session,output,template,proof);var id=cache.RegisterSession(source);
            source.DuringWriter=request=>{
                string ledger=Path.Combine(authority,"replay-volume-reservations.json");string healthy=File.ReadAllText(ledger);
                if(fault){var json=JsonNode.Parse(healthy);var held=json["Owners"][0]["Claims"].AsArray().First(c=>c["Claim"]["Kind"].GetValue<int>()==0);held["AnchorIdentity"]="changed-ancestor-identity";File.WriteAllText(ledger,json.ToJsonString());}
                // Periodic capacity no longer queries holder metadata. Exercise
                // the SAME integrity fault at a comprehensive owner/peer update
                // boundary, retaining the restored-fault/no-Saved oracle.
                try{
                    if(mode=="healthy")Task.Run(()=>cache.Maintain(id)).GetAwaiter().GetResult();
                    else {
                        var holder=JsonNode.Parse(File.ReadAllText(ledger))["Owners"][0];
                        string owner=holder["Owner"].GetValue<string>();
                        var claims=holder["Claims"].AsArray().Select(c=>System.Text.Json.JsonSerializer.Deserialize<ReplayVolumeClaim>(c["Claim"].ToJsonString())).ToArray();
                        if(mode=="producer-fault")reservations.Update(owner,claims,new ReplayFiles());
                        else Task.Run(()=>peer.Update(Guid.NewGuid().ToString("N"),[],new ReplayFiles())).GetAwaiter().GetResult();
                    }
                }
                catch(IOException e){maintenanceFailed=true;maintenanceError=e.Message;}
                finally{if(fault)File.WriteAllText(ledger,healthy);}
                Need(fault==maintenanceFailed,"Maintenance fault/control was not exercised");
            };
            var submission=cache.TrySave(id,Stopwatch.GetTimestamp(),output);Need(submission.Accepted,"Cache admission failed: "+submission.Rejection);var result=submission.Completion.GetAwaiter().GetResult();
            bool published=Directory.Exists(output)&&Directory.GetFiles(output,"*.mp4").Any(p=>!p.EndsWith(".partial.mp4",StringComparison.OrdinalIgnoreCase));
            rows.Add(new{mode,fault,maintenanceFailed,maintenanceError,result.Succeeded,result.Error,saved,published,checkedIdentity=result.Validation?.PublicationIdentityBound});
            File.WriteAllText(Path.Combine(root,"result.json"),System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Need(fault?!result.Succeeded&&!saved&&!published:result.Succeeded&&saved&&published&&result.Validation.PublicationIdentityBound,"Full Cache anchor regression failed; see result.json");
        }
        return rows;
    }
}
