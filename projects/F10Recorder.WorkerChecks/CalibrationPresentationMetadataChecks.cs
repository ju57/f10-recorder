using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F10Recorder;

// Generated JSON/CSV only, through the actual closed-reference reader. This
// does not construct a presentation reader, fixture, FileSink or media owner.
internal static class CalibrationPresentationMetadataChecks
{
    internal static int Run(string root)
    {
        root=Path.GetFullPath(root);
        if(!root.StartsWith(Path.GetFullPath("artifacts")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Directory.Exists(root)||File.Exists(root))
            throw new IOException("Fresh ignored generated metadata root required");
        Directory.CreateDirectory(root);var watch=Stopwatch.StartNew();var cases=new List<object>();
        var spec=new PresentationMethod(PresentationMethod.Identity,0,@"\\.\DISPLAY1",0,0,1920,1080,PresentationMethod.MarkerProtectionScope,PresentationMethod.MonotonicMarkerScope);
        string hash=new('A',64);var start=new PresentationStart(hash,"H03",123,456,"generated-nonce",900);
        var foreground=new CalibrationForegroundRelease(start.Label,start.Pid,start.Hwnd,start.Nonce);
        var ready=new PresentationReady(spec.Method,hash,start,1250,1000,spec.DeviceName,0,0,1920,1080);
        string trace="ordinal,id,present,before,after,accumulated,coalesced,protected,released\n"+
            string.Concat(Enumerable.Range(1,5).Select(i=>$"{i},{99+i},{900+i*100},{899+i*100},{901+i*100},1,0,1,1\n"));
        byte[] traceBytes=Encoding.UTF8.GetBytes(trace);
        var closure=new PresentationClosure(spec.Method,hash,start,1250,1000,spec.DeviceName,0,0,1920,1080,
            5,5,5,5,5,traceBytes.Length,Convert.ToHexString(SHA256.HashData(traceBytes)),true,true,true,true,true,null,1,1,4096,null,Errors:[]);
        var baseline=new Dictionary<string,byte[]> {
            [CalibrationPresentationReference.TraceName]=traceBytes,
            [CalibrationPresentationReference.ClosureName]=JsonSerializer.SerializeToUtf8Bytes(closure),
            [CalibrationPresentationReference.RequestName]=JsonSerializer.SerializeToUtf8Bytes(start),
            [CalibrationPresentationReference.ReadyName]=JsonSerializer.SerializeToUtf8Bytes(ready),
            [CalibrationForegroundRelease.FileName]=JsonSerializer.SerializeToUtf8Bytes(foreground),
            ["fixture-ready.json"]=JsonSerializer.SerializeToUtf8Bytes(new{pid=123,hwnd=456,label="H03",seed=24680,lifetimeSeconds=CalibrationForeground.UserHandoffSeconds+420+15,
                maximumAnimatedFrame=CalibrationPattern.MaximumAnimatedFrame(CalibrationForeground.UserHandoffSeconds+420+15),barcodeFrameDivisor=1,sourceIdRange=CalibrationPattern.ReferenceSourceIdRange,recipe=CalibrationSourceReference.Policy}),
            ["foreground-admission.json"]=JsonSerializer.SerializeToUtf8Bytes(new{fixturePid=123,hwnd=456,visible=true,foreground=true}),
            ["foreground-handoff-complete.json"]=JsonSerializer.SerializeToUtf8Bytes(new{release=foreground}),
            ["fixture-exit.json"]=JsonSerializer.SerializeToUtf8Bytes(new{pid=123,exited=true,forced=false,exit=0}) };
        void Bound()
        {
            var files=Directory.GetFiles(root,"*",SearchOption.AllDirectories);
            if(watch.Elapsed.TotalSeconds>=30||files.Length>64||files.Sum(f=>new FileInfo(f).Length)>1048576)
                throw new IOException("Generated metadata30s/64files/1MiB bound");
        }
        void Reset(){Bound();foreach(var file in baseline)File.WriteAllBytes(Path.Combine(root,file.Key),file.Value);}
        void Change(string name,Action<JsonObject> edit)
        {string p=Path.Combine(root,name);var node=JsonNode.Parse(File.ReadAllBytes(p)).AsObject();edit(node);File.WriteAllBytes(p,JsonSerializer.SerializeToUtf8Bytes(node));}
        bool Reject(Action body)
        {try{body();return false;}catch(Exception e)when(e is IOException or JsonException or FormatException or OverflowException or KeyNotFoundException or InvalidOperationException){return true;}}
        void Need(string name,bool result){if(!result)throw new IOException("Generated metadata check failed: "+name);cases.Add(new{name,passed=true});Bound();}
        void Bad(string name,Action mutation)
        {Reset();mutation();Need(name,Reject(()=>CalibrationPresentationReference.ReadClosed(root,1260,1000,hash,spec)));}
        void C(string field,object value)=>Change(CalibrationPresentationReference.ClosureName,n=>n[field]=JsonSerializer.SerializeToNode(value));
        void Trace(string value)
        {
            byte[] b=Encoding.UTF8.GetBytes(value);File.WriteAllBytes(Path.Combine(root,CalibrationPresentationReference.TraceName),b);
            Change(CalibrationPresentationReference.ClosureName,n=>{n["Bytes"]=b.Length;n["TraceHash"]=Convert.ToHexString(SHA256.HashData(b));});
        }
        Reset();var timeline=CalibrationPresentationReference.ReadClosed(root,1260,1000,hash,spec);
        timeline.CheckFrame(101,1150,0);Need("valid-v4-flagged-serialized-nine-file-closed-reference",timeline.Birth(101).Present==1100);
        Bad("v3-closure-cannot-enter-v4",()=>C("Method","independent-desktop-presentation-interval-v3"));
        Bad("v4-closure-cannot-enter-v5",()=>C("Method","independent-desktop-presentation-interval-v4"));
        Bad("v5-wrong-fixture-label",()=>Change("fixture-ready.json",n=>n["label"]="H02"));
        Bad("v5-wrong-fixture-divisor",()=>Change("fixture-ready.json",n=>n["barcodeFrameDivisor"]=2));
        Bad("v5-wrong-fixture-seed",()=>Change("fixture-ready.json",n=>n["seed"]=13579));
        Bad("v5-wrong-fixture-frame-bound",()=>Change("fixture-ready.json",n=>n["maximumAnimatedFrame"]=40000));
        Bad("v5-wrong-fixture-lifetime",()=>Change("fixture-ready.json",n=>n["lifetimeSeconds"]=4800));
        Bad("v5-wrong-fixture-reference-policy",()=>Change("fixture-ready.json",n=>n["recipe"]="arbitrary-content"));
        void ReadyAt(long value){C("ReadyQpc",value);Change(CalibrationPresentationReference.ReadyName,n=>n["ReadyQpc"]=value);}
        Bad("declared-Ready-cannot-precede-computed-prefix",()=>ReadyAt(1150));
        Bad("declared-Ready-cannot-hide-postReady-row",()=>ReadyAt(1350));
        Bad("postReady-coalescence-cannot-be-replayed-as-bootstrap",()=>Trace(trace.Replace("1301,1,0,1,1","1301,6,0,1,1",StringComparison.Ordinal)));
        Bad("wrong-closure-method-hash",()=>C("MethodHash",new string('B',64)));
        Bad("wrong-start-method-hash",()=>Change(CalibrationPresentationReference.RequestName,n=>n["MethodHash"]=new string('B',64)));
        Bad("wrong-ready-method",()=>Change(CalibrationPresentationReference.ReadyName,n=>n["Method"]="other"));
        Bad("wrong-ready-request",()=>Change(CalibrationPresentationReference.ReadyName,n=>n["Request"]["Nonce"]="old"));
        Bad("ready-after-source-epoch",()=>{C("ReadyQpc",1300);Change(CalibrationPresentationReference.ReadyName,n=>n["ReadyQpc"]=1300);});
        Bad("wrong-ready-output",()=>Change(CalibrationPresentationReference.ReadyName,n=>n["DeviceName"]=@"\\.\DISPLAY2"));
        Bad("wrong-foreground-challenge",()=>Change(CalibrationForegroundRelease.FileName,n=>n["Nonce"]="old"));
        Bad("wrong-handoff-release",()=>Change("foreground-handoff-complete.json",n=>n["release"]["Hwnd"]=457));
        Bad("wrong-fixture-pid",()=>Change("fixture-ready.json",n=>n["pid"]=124));
        Bad("not-foreground",()=>Change("foreground-admission.json",n=>n["foreground"]=false));
        Bad("forced-fixture-exit",()=>Change("fixture-exit.json",n=>n["forced"]=true));
        Bad("wrong-fixture-exit",()=>Change("fixture-exit.json",n=>n["exit"]=1));
        Bad("trace-hash-mismatch",()=>C("TraceHash",new string('0',64)));
        Bad("trace-byte-truncation",()=>File.WriteAllBytes(Path.Combine(root,CalibrationPresentationReference.TraceName),traceBytes[..^1]));
        Bad("trace-row-truncation-with-rehashed-bytes",()=>Trace(trace[..trace.LastIndexOf("5,104",StringComparison.Ordinal)]));
        Bad("trace-ordinal-gap-with-rehashed-bytes",()=>Trace(trace.Replace("3,102,","4,102,",StringComparison.Ordinal)));
        Bad("trace-unreleased-flagged-row-with-rehashed-bytes",()=>Trace(trace.Replace("1201,1,0,1,1","1201,1,0,1,0",StringComparison.Ordinal)));
        Bad("malformed-protection-flag-with-rehashed-bytes",()=>Trace(trace.Replace("1201,1,0,1,1","1201,1,0,2,1",StringComparison.Ordinal)));
        Bad("omitted-flagged-row-with-rehashed-bytes",()=>Trace(string.Join('\n',trace.Split('\n').Where(line=>!line.StartsWith("3,102,",StringComparison.Ordinal)))));
        Bad("unknown-closure-property",()=>C("UnknownClosure",true));
        Bad("unknown-resource-property",()=>C("ProcessGpuMilliseconds",0));
        Bad("missing-cpu-counter",()=>Change(CalibrationPresentationReference.ClosureName,n=>n.Remove("ProcessCpuMilliseconds")));
        Bad("null-cpu-counter",()=>C("ProcessCpuMilliseconds",null));
        Bad("negative-cpu-counter",()=>C("ProcessCpuMilliseconds",-1));
        Bad("zero-peak-counter",()=>C("ProcessPeakWorkingSetBytes",0));
        Bad("resource-error",()=>C("ResourceError","unknown metrics"));
        Bad("changed-resource-meaning",()=>C("ResourceMeaning","GPU verified zero"));
        Bad("unknown-error-list",()=>C("Errors",null));
        Bad("hidden-error-list",()=>C("Errors",new[]{"later fault"}));
        Bad("thread-closure-unknown",()=>C("ThreadJoined",false));
        Bad("native-closure-unknown",()=>C("NativeOwnersClosed",false));
        Bad("row-count-contradiction",()=>C("Committed",4));
        Reset();Need("nonfinite-source-clock-rejected",Reject(()=>CalibrationPresentationReference.ReadClosed(root,double.NaN,1000,hash,spec)));
        var binding=new PresentationPlanBinding("generated-only",hash,spec);
        CalibrationPresentationIntegration.ValidateRetainedMethod(binding,closure);Need("matching-retained-method-pre-media-guard",true);
        foreach(var wrong in new[]{closure with{MethodHash=new string('B',64)},closure with{Method="other"},closure with{DeviceName=@"\\.\DISPLAY2"},closure with{Left=1},closure with{Width=1280}})
        {
            int continuation=0;
            bool rejected=Reject(()=>{CalibrationPresentationIntegration.ValidateRetainedMethod(binding,wrong);continuation++;});
            Need("retained-method-rejects-before-continuation-"+cases.Count,rejected&&continuation==0);
        }
        Need("retained-cannot-omit-accepted-native-method",Reject(()=>CalibrationPresentationIntegration.ValidateRetainedMethod(null,closure)));
        string warmTrace="ordinal,id,present,before,after,accumulated,coalesced,protected,released\n"+
            string.Concat(Enumerable.Range(1,7).Select(i=>$"{i},{99+i},{900+i*100},{899+i*100},{901+i*100},{(i==1?4:i==2?6:i==4?2:1)},0,1,1\n"));
        Reset();Trace(warmTrace);ReadyAt(1550);foreach(string field in new[]{"Acquired","Released","Produced","Committed","Rows"})C(field,7);
        var warmed=CalibrationPresentationReference.ReadClosed(root,1560,1000,hash,spec);warmed.CheckFrame(105,1550,0);
        Need("closed-parser-replays-preReady-bootstrap-and-flagged-rows",warmed.Birth(105).Present==1500);
        Need("closed-parser-discarded-prefix-ID-cannot-qualify",Reject(()=>warmed.CheckFrame(102,1200,0)));
        ReadyAt(1450);Need("closed-parser-bootstrap-computed-Ready-boundary-cannot-be-backdated",Reject(()=>CalibrationPresentationReference.ReadClosed(root,1560,1000,hash,spec)));
        string repeatedTrace="ordinal,id,present,before,after,accumulated,coalesced,protected,released\n"+
            string.Concat(Enumerable.Range(1,6).Select(i=>$"{i},{(i<5?99+i:i==5?103:104)},{900+i*100},{899+i*100},{901+i*100},{(i==5?3:1)},0,1,1\n"));
        Reset();Trace(repeatedTrace);foreach(string field in new[]{"Acquired","Released","Produced","Committed","Rows"})C(field,6);
        var repeated=CalibrationPresentationReference.ReadClosed(root,1260,1000,hash,spec);repeated.CheckFrame(103,1290,0);repeated.CheckFrame(103,1400,0);
        Need("v5-closed-sameID-Ac3-retains-birth-and-eventual-clean-next",repeated.Birth(103).Present==1300&&repeated.Birth(104).Present==1500);
        Trace(repeatedTrace.Replace("5,103,","5,104,",StringComparison.Ordinal));Need("v5-closed-changed-ID-Ac3-still-rejects",Reject(()=>CalibrationPresentationReference.ReadClosed(root,1260,1000,hash,spec)));
        string methodPath=Path.Combine(root,"generated-method.json");
        void ReadMethod(JsonObject node,bool valid,string name)
        {
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(node);File.WriteAllBytes(methodPath,bytes);
            string methodHash=Convert.ToHexString(SHA256.HashData(bytes));
            Need(name,valid?!Reject(()=>PresentationMethod.Read(methodPath,methodHash)):Reject(()=>PresentationMethod.Read(methodPath,methodHash)));
        }
        var methodNode=JsonSerializer.SerializeToNode(spec).AsObject();ReadMethod(methodNode,true,"actual-v4-method-reader-explicit-policy");
        methodNode.Remove("ProtectionScope");ReadMethod(methodNode,false,"actual-method-reader-rejects-missing-policy");
        methodNode["ProtectionScope"]="whole-frame-unmasked";ReadMethod(methodNode,false,"actual-method-reader-rejects-wrong-policy");
        methodNode=JsonSerializer.SerializeToNode(spec).AsObject();methodNode.Remove("MarkerTransitionScope");ReadMethod(methodNode,false,"v5-method-reader-rejects-missing-transition-policy");
        methodNode["MarkerTransitionScope"]="arbitrary-content;any-gap";ReadMethod(methodNode,false,"v5-method-reader-rejects-wrong-transition-policy");
        methodNode=JsonSerializer.SerializeToNode(spec).AsObject();methodNode["Method"]="independent-desktop-presentation-interval-v4";ReadMethod(methodNode,false,"v5-method-reader-rejects-v4");
        var adjacent=spec with{Method=PresentationMethod.AdjacentIntervalIdentity,MarkerTransitionScope=PresentationMethod.AdjacentIntervalMarkerScope};
        var adjacentNode=JsonSerializer.SerializeToNode(adjacent).AsObject();
        ReadMethod(adjacentNode,true,"v6-method-reader-requires-explicit-interval-policy");
        string adjacentHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(methodPath)));
        ReadMethod(JsonSerializer.SerializeToNode(adjacent with{MarkerTransitionScope=spec.MarkerTransitionScope}).AsObject(),false,"v6-cannot-use-v5-policy");
        ReadMethod(JsonSerializer.SerializeToNode(spec with{MarkerTransitionScope=adjacent.MarkerTransitionScope}).AsObject(),false,"v5-cannot-use-v6-policy");
        string adjacentTrace=trace.Replace("1301,1,0,1,1","1301,2,0,1,1",StringComparison.Ordinal);
        Reset();Trace(adjacentTrace);
        Need("v5-closed-reader-rejects-adjacent-ac2",Reject(()=>CalibrationPresentationReference.ReadClosed(root,1260,1000,hash,spec)));
        Change(CalibrationPresentationReference.RequestName,n=>n["MethodHash"]=adjacentHash);
        foreach(string name in new[]{CalibrationPresentationReference.ReadyName,CalibrationPresentationReference.ClosureName})
            Change(name,n=>{n["Method"]=adjacent.Method;n["MethodHash"]=adjacentHash;n["Request"]["MethodHash"]=adjacentHash;});
        var bounded=CalibrationPresentationReference.ReadClosed(root,1260,1000,adjacentHash,adjacent);
        Need("v6-closed-reader-bounds-adjacent-birth",bounded.Birth(103) is {EarliestPresent:1200,Present:1300});
        double tolerance=1000*(1d/60+.002);
        bounded.CheckFrame(102,1200+tolerance,.002);
        bounded.CheckFrame(103,1300-tolerance,.002);
        Need("v6-picture-edges-are-inclusive",true);
        Need("v6-previous-picture-rejects-one-tick-after-earliest-next-plus-tolerance",Reject(()=>bounded.CheckFrame(102,1201+tolerance,.002)));
        var earlierPicture=CalibrationPresentationReference.ReadClosed(root,1260,1000,adjacentHash,adjacent);
        Need("v6-new-picture-rejects-one-tick-before-latest-current-minus-tolerance",Reject(()=>earlierPicture.CheckFrame(103,1299-tolerance,.002)));
        Need("v6-ready-and-closure-cannot-be-read-as-v5",Reject(()=>CalibrationPresentationReference.ReadClosed(root,1260,1000,adjacentHash,spec)));
        var referenceBinding=JsonSerializer.SerializeToElement(CalibrationPresentationIntegration.HashBinding(root,adjacent));
        CalibrationPresentationIntegration.ValidateBinding(root,referenceBinding,adjacent);
        Need("v6-closed-reference-binding-does-not-validate-as-v5",Reject(()=>CalibrationPresentationIntegration.ValidateBinding(root,referenceBinding,spec)));
        var closedAdjacent=CalibrationPresentationReference.ReadJson<PresentationClosure>(Path.Combine(root,CalibrationPresentationReference.ClosureName));
        Need("v6-retained-method-matches-original-only",!Reject(()=>CalibrationPresentationIntegration.ValidateRetainedMethod(new("generated-only",adjacentHash,adjacent),closedAdjacent))
            &&Reject(()=>CalibrationPresentationIntegration.ValidateRetainedMethod(new("generated-only",adjacentHash,spec),closedAdjacent)));
        Trace(adjacentTrace.Replace("1301,2,0,1,1","1301,3,0,1,1",StringComparison.Ordinal));
        Need("v6-closed-reader-rejects-changed-ac3",Reject(()=>CalibrationPresentationReference.ReadClosed(root,1260,1000,adjacentHash,adjacent)));
        Trace(adjacentTrace.Replace("4,103,","4,104,",StringComparison.Ordinal));
        Need("v6-closed-reader-rejects-skipped-ac2",Reject(()=>CalibrationPresentationReference.ReadClosed(root,1260,1000,adjacentHash,adjacent)));
        Change(CalibrationPresentationReference.RequestName,n=>n["QualifySavedPictures"]=true);
        foreach(string name in new[]{CalibrationPresentationReference.ReadyName,CalibrationPresentationReference.ClosureName})
            Change(name,n=>n["Request"]["QualifySavedPictures"]=true);
        PresentationTimeline Saved(int? last)=>CalibrationPresentationReference.ReadClosed(root,1260,1000,adjacentHash,adjacent,last);
        string badTail=adjacentTrace.Replace("1401,1,0,1,1","1401,3,0,1,1",StringComparison.Ordinal);
        Trace(badTail);
        var saved=Saved(102);saved.CheckFrame(102,1200+tolerance, .002);
        Need("saved-marker-qualified-through-its-bounded-successor-before-bad-tail",saved.LastQualifiedRow==4&&saved.HasSuccessor(102)&&!saved.HasSuccessor(103));
        Need("in-window-late-picture-still-fails",Reject(()=>saved.CheckFrame(102,1201+tolerance,.002)));
        Need("bad-final-required-transition-still-fails",Reject(()=>Saved(103)));
        Need("scoped-request-without-decoded-final-marker-fails",Reject(()=>Saved(null)));
        Trace(adjacentTrace);
        Need("missing-final-successor-fails",Reject(()=>Saved(104)));
        Trace(badTail.Replace("1401,3,0,1,1","1401,3,0,1,0",StringComparison.Ordinal));
        Need("post-window-unreleased-row-still-fails",Reject(()=>Saved(102)));
        long[][] paints=[[101,1000,1025],[102,1100,1125],[103,1200,1225],[104,1300,1326]];
        CalibrationReferencePhase.ValidatePaintBrackets(paints,1000,103);
        Need("long-paint-after-witness-scoped-to-diagnostics",true);
        Need("long-paint-at-witness-and-in-full-trace-fail",Reject(()=>CalibrationReferencePhase.ValidatePaintBrackets(paints,1000,104))
            &&Reject(()=>CalibrationReferencePhase.ValidatePaintBrackets(paints,1000,null)));
        Need("missing-witness-paint-fails",Reject(()=>CalibrationReferencePhase.ValidatePaintBrackets(paints,1000,105)));
        var atPress=new CaptureHealthSnapshot(false,null,null,300,299,0,299,0,17940,"");
        var atSave=atPress with{CompletedSegmentEnd=301};
        CalibrationReferencePhase.CheckRecordingAtPublication(false,atPress,atSave,300.25);
        Need("open-tail-advanced-through-request",true);
        Need("stopped-or-failed-audio-during-save-rejected",
            Reject(()=>CalibrationReferencePhase.CheckRecordingDuringSave(true,atSave))
            &&Reject(()=>CalibrationReferencePhase.CheckRecordingDuringSave(false,atSave with{AudioFailure="generated"})));
        Need("unadvanced-or-short-recording-at-publication-rejected",
            Reject(()=>CalibrationReferencePhase.CheckRecordingAtPublication(false,atPress,atPress,299))
            &&Reject(()=>CalibrationReferencePhase.CheckRecordingAtPublication(false,atPress,atSave,302)));
        Reset();Bound();var result=new{kind="generated-fixed-fixture-closed-reference-parser-controls-v2",count=cases.Count,cases,
            seconds=watch.Elapsed.TotalSeconds,fixtureFiles=baseline.Count,fixtureBytes=baseline.Values.Sum(x=>x.Length),children=0,
            deviceCalls=0,gpuCalls=0,uiCalls=0,audioCalls=0,codecCalls=0,captures=0,nativeFileSinkCalls=0,
            generatedOnly=true,mediaAcceptance=false,guardPlacement="RetainedCoveragePair validates accepted native closure method immediately after reference binding, before selection copying/export/decode"};
        using(var file=new FileStream(Path.Combine(root,"result.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))JsonSerializer.Serialize(file,result,new JsonSerializerOptions{WriteIndented=true});
        Bound();Console.WriteLine($"Passed {cases.Count} generated metadata/parser and early retained method controls;no native/media action.");return 0;
    }
}
