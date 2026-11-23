using System.Text.Json;

namespace F10Recorder;

// Replays the observed mixed-parent shape at the actual second Cache protection
// boundary. The primary callback creates a same-object alias for the controlled
// last input; no dummy bytes are ever treated as playable or decoded media.
internal static class ReplayPairedAliasChecks
{
    sealed class Source(string directory,List<Segment> segments) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo => new(directory,Path.Combine(directory,"segments.csv"),2,20,60,"h264_nvenc",".ts",false,true);
        public bool SupportsOwnedExport=>true;
        public bool PreserveEvidence=>true;
        public bool OutputProducerStopped=>true;
        public bool Stopped=>true;
        public string Diagnostics=>"controlled mixed-parent protection only";
        public List<Segment> ReadSegments()=>segments.Select(s=>new Segment{Path=s.Path,Start=s.Start,End=s.End}).ToList();
        public double SaveCutoffAt(long _)=>2.75;
        public void FreezeAt(long _){}
        public void StopCapture(){}
        public void CheckCaptureHealth(double _){}
        public void Export(IReadOnlyList<string> _,string __)=>throw new NotSupportedException("No codec is admitted by this check.");
        public void Dispose(){}
    }
    internal static int Run(string root)
    {
        if(Directory.Exists(root)||File.Exists(root))throw new IOException("Preserve prior alias fixture.");
        Directory.CreateDirectory(root);
        string directory=Path.Combine(root,"buffer","session"),clips=Path.Combine(root,"clips");
        Directory.CreateDirectory(directory);Directory.CreateDirectory(clips);
        var segments=Enumerable.Range(0,3).Select(n=>new Segment{Path=Path.Combine(directory,$"chunk-{n:D9}.ts"),Start=n,End=n+1}).ToList();
        foreach(var s in segments)File.WriteAllBytes(s.Path,Enumerable.Repeat((byte)0x5a,1024).ToArray());
        File.WriteAllText(Path.Combine(directory,"segments.csv"),string.Join('\n',segments.Select(s=>$"{Path.GetFileName(s.Path)},{s.Start},{s.End}"))+"\n");
        using var source=new Source(directory,segments);
        using var cache=new ReplayCache(cacheDirectory:Path.Combine(root,"buffer"),cacheBudgetBytes:16_000_000,
            diagnostic:_=>{});
        ReplayPairedSelectionLease held=null;ReplayExportRequest original=null;ReplaySaveResult pairedResult=null;
        bool reachedStrictGuard=false,wrongObjectRejected=false,wrongIntervalRejected=false;
        var observations=new List<object>();
        ReplaySaveResult Save(ReplayCalibrationRun.ShortSource wrapped)
        {
            var submission=cache.TrySave(cache.RegisterSession(wrapped),0,clips);
            if(!submission.Accepted)throw new IOException(submission.Rejection);
            return submission.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        try
        {
            using var first=new ReplayCalibrationRun.ShortSource(source,2,2.75,exactFallback:true,protect:request=>{
                if(request.NativeFiles==null||request.Inputs.Count!=3||Math.Abs(request.Interval.Seconds-2)>0.000001)
                    throw new IOException("Actual owned-native protection/full-three-input interval was not reached.");
                string parent=Path.Combine(directory,"first-save-alias");Directory.CreateDirectory(parent);
                string alias=Path.Combine(parent,"000002.ts");
                if(!new ReplayFiles(ReplayFileMode.NativeInventoryForChecks).TryHardLink(alias,request.Inputs[^1].Path))throw new IOException("Same-object fixture alias failed.");
                var mixed=request.Inputs.ToArray();mixed[^1]=mixed[^1] with{Path=alias};original=request with{Inputs=mixed};
                if(ReplayStorage.Observe(alias).Identity!=mixed[^1].Extent.Identity)throw new IOException("Fixture alias changed native object.");
                held=new ReplayPairedSelectionLease(original,"controlled-mixed-parent",null);
                throw new IOException("controlled-primary-mixed-protected-before-codec");
            },preserveEvidence:true);
            var primary=Save(first);
            if(held==null||original==null||primary.Error!="controlled-primary-mixed-protected-before-codec")throw new IOException("Primary actual protection boundary was not reached: "+primary.Error);
            held.PrepareForReplay(source,null);
            using var paired=new ReplayCalibrationRun.ShortSource(source,2,2.75,exactFallback:true,protect:request=>{
                held.Verify(request,"controlled-mixed-parent",null);reachedStrictGuard=true;
                observations.Add(new{request.Interval,ids=request.Inputs.Select(i=>i.Extent.Identity).ToArray()});
                throw new IOException("controlled-mixed-pair-verified-before-codec");
            },preserveEvidence:true,protectedSegments:held.ReadSegments);
            pairedResult=Save(paired);
            if(reachedStrictGuard)
            {
                string wrong=Path.Combine(directory,"wrong-object.ts");File.WriteAllBytes(wrong,new byte[1024]);
                List<Segment> Wrong(){var rows=held.ReadSegments();rows[^1].Path=wrong;return rows;}
                using var changed=new ReplayCalibrationRun.ShortSource(source,2,2.75,exactFallback:true,protect:r=>held.Verify(r,"controlled-mixed-parent",null),preserveEvidence:true,protectedSegments:Wrong);
                wrongObjectRejected=Save(changed).Error=="Paired requests do not select the same ordered protected source objects and interval.";
                using var interval=new ReplayCalibrationRun.ShortSource(source,2,3,exactFallback:true,protect:r=>held.Verify(r,"controlled-mixed-parent",null),preserveEvidence:true,protectedSegments:held.ReadSegments);
                wrongIntervalRejected=Save(interval).Error=="Paired requests do not select the same ordered protected source objects and interval.";
            }
        }
        finally{held?.Dispose();cache.CloseAsync().GetAwaiter().GetResult();}
        bool passed=reachedStrictGuard&&wrongObjectRejected&&wrongIntervalRejected&&pairedResult?.Error=="controlled-mixed-pair-verified-before-codec";
        File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed,reachedStrictGuard,wrongObjectRejected,wrongIntervalRejected,
            primaryInfoDirectory=original.Info.Directory,primaryInputCount=original.Inputs.Count,primarySeconds=original.Interval.Seconds,
            primaryNativeGuard=original.NativeFiles!=null,originalParents=original.Inputs.Select(i=>Path.GetDirectoryName(i.Path)).Distinct().ToArray(),pairedResult,observations,
            children=0,captureStarted=false,actualMediaExports=0,aliases=held.ReplayAliases,scope="Actual second Cache/native-rooted protection of3same-native objects from two first-save child parents; re-rooted once into original session. Markers stop before codec. No media/model/worker qualification."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(new{passed,reachedStrictGuard,wrongObjectRejected,wrongIntervalRejected,error=pairedResult?.Error}));
        return passed?0:1;
    }
}
