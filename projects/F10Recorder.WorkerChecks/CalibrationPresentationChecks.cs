#nullable enable
using System.Diagnostics;
using System.Text.Json;
namespace F10Recorder;
internal static class CalibrationPresentationChecks
{
    sealed class MemorySink(int writeFailure=0,bool flushFailure=false,bool closeFailure=false):IPresentationSink
    {
        readonly MemoryStream stream=new();bool closed;
        public long Rows{get;private set;}public long Bytes=>stream.Length;public string Hash{get;private set;}="";public bool Closed=>closed;
        public void Write(PresentationRow row){if(writeFailure>0&&Rows+1==writeFailure)throw new IOException("Generated trace-write failure");byte[] b=System.Text.Encoding.UTF8.GetBytes(row.ToString()+"\n");stream.Write(b);Rows++;}
        public void FlushDurable(){if(flushFailure)throw new IOException("Generated trace-flush failure");Hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray()));}
        public void Dispose(){if(closeFailure)throw new IOException("Generated trace-close failure");closed=true;}
    }
    internal static int Run(string output)=>RunArgs(["--checks",output]);
    internal static int RunProtection(string output)
    {
        output=Path.GetFullPath(output);
        if(!output.StartsWith(Path.GetFullPath("artifacts")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||File.Exists(output))
            throw new IOException("Fresh ignored generated report required");
        var watch=Stopwatch.StartNew();var cases=new List<object>();
        void Check(string name,bool value){if(!value)throw new IOException(name);cases.Add(new{name,passed=true});if(watch.Elapsed.TotalSeconds>=30)throw new IOException("Generated protection30s bound");}
        PresentationRow Row(long n,int id,long t,bool flag,uint count=1)=>new(n,id,t,t-5,t+5,count,false,flag,true);
        PresentationTimeline Valid(bool flag)
        {var t=new PresentationTimeline(6000);for(int i=1;i<=5;i++)t.Add(Row(i,99+i,i*100,flag));return t;}
        byte[] Raster(int value)
        {
            var bytes=new byte[2304*48];
            for(int y=0;y<48;y++)for(int x=0;x<576;x++){bool on=(value&(1<<(x/24)))!=0;if(y>=24)on=!on;int p=y*2304+x*4;bytes[p]=bytes[p+1]=bytes[p+2]=(byte)(on?255:0);bytes[p+3]=255;}
            return bytes;
        }
        foreach(bool flag in new[]{false,true})
        {
            var row=Row(1,BarcodeRaster.Decode(Raster(12345),2304),100,flag);
            Check($"valid-complement-marker-retains-global-flag-{flag}",row.Id==12345&&row.Protected==flag);
            var good=Valid(flag);good.Complete(true,true,true);good.CheckFrame(101,100,0);good.CheckFrame(101,400,0);
            Check($"same-current-next-one-frame-interval-{flag}",good.Birth(101).Present==200);
            var stale=Valid(flag);stale.Complete(true,true,true);Check($"stale-source-rejected-{flag}",Rejected(()=>stale.CheckFrame(101,400.01,0)));
            var missing=Valid(flag);missing.Complete(true,true,true);Check($"missing-next-rejected-{flag}",Rejected(()=>missing.CheckFrame(104,500,0)));
            var wrong=Valid(flag);wrong.Complete(true,true,true);Check($"wrong-source-id-rejected-{flag}",Rejected(()=>wrong.CheckFrame(99,200,0)));
            var bridge=Valid(flag);bridge.Complete(true,true,true);Check($"bridge-bound-not-widened-{flag}",Rejected(()=>bridge.CheckFrame(101,200,.00201)));
            foreach(var invalid in new[]{Row(6,105,600,flag,2),Row(7,105,600,flag),Row(6,105,600,flag) with{Released=false},Row(6,105,400,flag),Row(6,102,600,flag),Row(6,105,600,flag) with{Before=-1}})
            {var t=Valid(flag);Check($"invalid-ledger-id-clock-rejects-{flag}-{cases.Count}",Rejected(()=>t.Add(invalid)));Check($"failure-remains-sticky-{flag}-{cases.Count}",Rejected(()=>t.Add(Row(6,105,600,flag))));}
            var pointer=Valid(flag);var pointerRow=new PresentationRow(6,-1,0,600,601,0,false,flag,true);pointer.Add(pointerRow);pointer.Add(Row(7,105,700,flag));pointer.Complete(true,true,true);
            Check($"flagged-pointer-has-no-presentation-{flag}",pointer.Birth(105).Present==700&&pointerRow.Protected==flag);
            using(var pointerReader=new NativePresentationReader((r,_)=>{r.FakeAcquire();r.FakeRelease();return pointerRow with{Ordinal=r.AcquiredRows};}))
            {
                var retainedPointer=pointerReader.Read(default)!;pointerReader.CommitThrough(retainedPointer.Ordinal);pointerReader.Dispose();
                Check($"pointer-row-full-reader-ledger-{flag}",retainedPointer.Protected==flag&&pointerReader.AcquiredRows==1&&pointerReader.ReleasedRows==1&&pointerReader.ProducedRows==1&&pointerReader.CommittedRows==1&&pointerReader.CanSeal);
            }
            var blank=new byte[2304*48];var ambiguous=Enumerable.Repeat((byte)128,2304*48).ToArray();var complement=Raster(12345);Array.Copy(complement,0,complement,2304*24,2304*24);
            foreach(var invalidRaster in new[]{blank,ambiguous,complement})
            {
                using var reader=new NativePresentationReader((r,_)=>{r.FakeAcquire();try{return Row(1,BarcodeRaster.Decode(invalidRaster,2304),100,flag);}finally{r.FakeRelease();}});
                Check($"invalid-roi-rejects-before-row-{flag}-{cases.Count}",Rejected(()=>reader.Read(default))&&reader.AcquiredRows==1&&reader.ReleasedRows==1&&reader.ProducedRows==0&&!reader.CanSeal);
                Check($"invalid-roi-failure-is-sticky-{flag}-{cases.Count}",Rejected(()=>reader.Read(default)));
            }
            PresentationTimeline Warm()
            {
                var t=new PresentationTimeline(6000);
                foreach(var r in new[]{Row(1,100,100,flag,4),Row(2,102,200,flag,6),Row(3,103,300,flag),Row(4,104,400,flag,2),Row(5,105,500,flag),Row(6,106,600,flag),Row(7,107,700,flag)})t.Add(r);
                return t;
            }
            var warming=new PresentationTimeline(6000);
            foreach(var r in new[]{Row(1,100,100,flag,4),Row(2,102,200,flag,6),Row(3,103,300,flag),Row(4,104,400,flag,2),Row(5,105,500,flag)})
            {warming.Add(r);Check($"bootstrap-prefix-does-not-declare-Ready-{flag}-{r.Ordinal}",!warming.Ready);}
            warming.Add(Row(6,106,600,flag));Check($"two-clean-transitions-after-last-reset-prove-Ready-{flag}",warming.Ready);
            warming.Add(Row(7,107,700,flag));warming.Complete(true,true,true);warming.CheckFrame(106,650,0);Check($"clean-qualified-current-next-still-valid-{flag}",true);
            foreach(int prefixId in new[]{100,102,103,104})
            {var prefix=Warm();prefix.Complete(true,true,true);Check($"discarded-prefix-ID-never-qualifies-{flag}-{prefixId}",Rejected(()=>prefix.CheckFrame(prefixId,650,0)));}
            var late=Warm();Check($"postReady-coalescence-still-rejects-{flag}",Rejected(()=>late.Add(Row(8,108,800,flag,2))));
            Check($"postReady-coalescence-remains-sticky-{flag}",Rejected(()=>late.Add(Row(8,108,800,flag))));
            var stable=Valid(flag);var originalBirth=stable.Birth(104);stable.Add(Row(6,104,600,flag,3));
            Check($"v5-same-ID-coalescence-retains-original-birth-{flag}",stable.Birth(104)==originalBirth);
            stable.Add(Row(7,105,700,flag));stable.Complete(true,true,true);stable.CheckFrame(104,400,0);stable.CheckFrame(104,800,0);stable.CheckFrame(103,450,0);
            Check($"v5-eventual-Ac1-next-link-and-original-interval-unchanged-{flag}",stable.Birth(104).Present==500&&stable.Birth(105).Present==700);
            foreach(var invalid in new[]{Row(6,104,600,flag,3) with{Released=false},Row(7,104,600,flag,3),Row(6,104,500,flag,3),Row(6,103,600,flag,3),Row(6,104,600,flag,3) with{Before=-1}})
            {var strict=Valid(flag);Check($"v5-repeat-cannot-bypass-ID-clock-ownership-{flag}-{cases.Count}",Rejected(()=>strict.Add(invalid)));Check($"v5-repeat-invalid-still-sticky-{flag}-{cases.Count}",Rejected(()=>strict.Add(Row(6,104,600,flag))));}
            foreach(var invalid in new[]{Row(2,99,200,flag,6),Row(3,102,200,flag,6),Row(2,102,200,flag,6) with{Released=false},Row(2,102,100,flag,6),Row(2,102,200,flag,6) with{Before=-1}})
            {var guarded=new PresentationTimeline(6000);guarded.Add(Row(1,100,100,flag,4));Check($"bootstrap-cannot-reset-ownership-ID-clock-fault-{flag}-{cases.Count}",Rejected(()=>guarded.Add(invalid)));Check($"bootstrap-invalid-prefix-remains-sticky-{flag}-{cases.Count}",Rejected(()=>guarded.Add(Row(2,102,200,flag))));}
        }
        var spec=new PresentationMethod(PresentationMethod.Identity,0,@"\\.\DISPLAY1",0,0,1920,1080,PresentationMethod.MarkerProtectionScope,PresentationMethod.MonotonicMarkerScope);
        PresentationMethod.Validate(spec);Check("explicit-v5-marker-and-monotonic-fixture-policy",spec.Method=="independent-desktop-presentation-interval-v5");
        foreach(var wrong in new[]{spec with{Method="independent-desktop-presentation-interval-v3"},spec with{Method="independent-desktop-presentation-interval-v4"},spec with{ProtectionScope=null!},spec with{ProtectionScope="whole-frame-unmasked"},spec with{MarkerTransitionScope=null!},spec with{MarkerTransitionScope="arbitrary-content;any-gap"}})
            Check("old-or-missing-or-other-policy-rejects-"+cases.Count,Rejected(()=>PresentationMethod.Validate(wrong)));
        int fixtureLifetime=CalibrationForeground.UserHandoffSeconds+420+15;PresentationMethod.ValidateFixture("H03",true,fixtureLifetime);
        Check("v5-fixed-H03-fixture-forward-no-divisor-no-wrap-domain",CalibrationPattern.MaximumAnimatedFrame(fixtureLifetime)<40000);
        foreach(var wrong in new[]{("H02",true,fixtureLifetime),("H03",false,fixtureLifetime),("H03",true,fixtureLifetime+1),("H03",true,4800)})
            Check("v5-arbitrary-or-wrapped-fixture-rejects-"+cases.Count,Rejected(()=>PresentationMethod.ValidateFixture(wrong.Item1,wrong.Item2,wrong.Item3)));
        byte[] result=JsonSerializer.SerializeToUtf8Bytes(new{kind="generated-v5-fixed-monotonic-marker-policy-controls-v1",method=spec,count=cases.Count,cases,seconds=watch.Elapsed.TotalSeconds,
            nativeCalls=0,children=0,capture=false,mediaAcceptance=false,fullFrameUnmaskedProof=false});
        if(result.Length>1048576)throw new IOException("Generated report1MiB bound");
        using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);file.Write(result);file.Flush(true);
        Console.WriteLine($"Passed {cases.Count} generated v4 marker/protection controls;no native/media action.");return 0;
    }
    static bool Rejected(Action body){try{body();return false;}catch(IOException){return true;}}
    static int RunArgs(string[] args)
    {
        if(args is ["--plan"]){Console.WriteLine("Offline generated presentation timeline/raster/ABI/lifecycle checks only;this route makes no GPU/desktop/audio/codec calls. Separate fixture native entry requires independent admission.30s/256MiB/1MiB report.");return 0;}
        if(args.Length!=2||args[0]!="--checks")return 2; // NO native acquisition entry is exposed.
        var watch=Stopwatch.StartNew();var cases=new List<object>();
        void Check(string name,bool value){cases.Add(new{name,passed=value});if(!value)throw new IOException(name);}
        PresentationRow Row(long n,int id,long t,uint accumulated=1,bool coalesced=false,bool released=true)=>new(n,id,t,t-5,t+5,accumulated,coalesced,false,released);
        PresentationTimeline Valid()
        {var t=new PresentationTimeline(6000);t.Add(Row(1,100,100,4,true));t.Add(Row(2,101,200));t.Add(Row(3,101,250));t.Add(Row(4,102,300));t.Add(Row(5,103,400));return t;}
        var good=Valid();Check("ready-after-two-proved-transitions",good.Ready);good.Complete(true,true,true);
        good.CheckFrame(101,150,0);good.CheckFrame(101,400,0);Check("unchanged-inclusive-one-frame-bounds",true);
        Check("old-source-still-rejected",Rejected(()=>good.CheckFrame(101,400.01,0)));
        var bootstrap=Valid();bootstrap.Complete(true,true,true);Check("unknown-bootstrap-birth-cannot-qualify",Rejected(()=>bootstrap.CheckFrame(100,100,0)));
        foreach(var bad in new[]{Row(6,104,500,2),Row(6,102,500),Row(6,104,400),Row(7,104,500),Row(6,104,500,released:false),Row(6,104,500) with{Before=-1}})
        {var t=Valid();Check("native-gap-reuse-coalescence-ordinal-release-"+cases.Count,Rejected(()=>t.Add(bad)));Check("failure-sticky-"+cases.Count,Rejected(()=>t.Add(Row(6,104,500))));}
        var spatial=Valid();spatial.Add(Row(6,104,500,1,true));Check("spatial-dirty-coalescence-not-a-missed-image",spatial.Ready);
        var pointer=Valid();pointer.Add(new(6,-1,0,500,501,0,false,false,true));pointer.Add(Row(7,104,600));pointer.Complete(true,true,true);Check("pointer-only-event-does-not-invent-presentation",pointer.Birth(104).Present==600);
        var skipped=Valid();skipped.Add(Row(6,107,500));skipped.Add(Row(7,109,600));skipped.Complete(true,true,true);
        skipped.CheckFrame(103,500,0);Check("unpresented-paint-jump-uses-next-actual-presentation",true);
        Check("never-presented-ID-cannot-qualify",Rejected(()=>skipped.CheckFrame(104,500,0)));
        foreach(double bridge in new[]{-.0001,.00201,double.NaN,double.PositiveInfinity})
        {var t=Valid();t.Complete(true,true,true);Check("invalid-or-expanded-bridge-rejected-"+cases.Count,Rejected(()=>t.CheckFrame(101,200,bridge)));}
        foreach(var closure in new[]{(false,true,true),(true,false,true),(true,true,false)})
        {var t=Valid();Check("closure-unknown-rejects-"+cases.Count,Rejected(()=>t.Complete(closure.Item1,closure.Item2,closure.Item3)));}
        var next=Valid();next.Complete(true,true,true);Check("missing-next-birth-rejects",Rejected(()=>next.CheckFrame(103,400,0)));
        // Exhaust all integer points around independently generated presentation intervals.
        int points=0;
        for(int p=90;p<=410;p++)
        {
            var t=Valid();t.Complete(true,true,true);bool actual=!Rejected(()=>t.CheckFrame(101,p,0));
            Check("interval-point-"+p,actual==(p>=100&&p<=400));points++;
        }
        byte[] Raster(int value,int pitch=2304)
        {
            var data=new byte[pitch*48];
            for(int y=0;y<48;y++)for(int x=0;x<576;x++){bool on=(value&(1<<(x/24)))!=0;if(y>=24)on=!on;int p=y*pitch+x*4;data[p]=data[p+1]=data[p+2]=(byte)(on?255:0);data[p+3]=255;}
            return data;
        }
        foreach(int value in new[]{0,1,2,255,6321246,0x7fffff,0xffffff})Check("native-complement-raster-"+value,BarcodeRaster.Decode(Raster(value,2560),2560)==value);
        Check("ambiguous-raster-rejects",Rejected(()=>BarcodeRaster.Decode(new byte[2304*48],2304)));
        Check("bad-row-pitch-rejects",Rejected(()=>BarcodeRaster.Decode(new byte[16],16)));
        Check("frame-info-native-size",System.Runtime.InteropServices.Marshal.SizeOf<NativePresentationReader.FrameInfo>()==48);
        Check("output-desc-native-size",System.Runtime.InteropServices.Marshal.SizeOf<NativePresentationReader.OutputDescription>()==96);
        Check("mapped-resource-native-size",System.Runtime.InteropServices.Marshal.SizeOf<NativePresentationReader.Mapped>()==16);
        int calls=0;
        var accessLost=new NativePresentationReader((_,_)=>{calls++;throw new IOException("Generated ACCESS_LOST before acquisition");});
        Check("actual-reader-acquire-error-rejects",Rejected(()=>accessLost.Read(default)));
        Check("actual-reader-acquire-error-is-sticky",Rejected(()=>accessLost.Read(default))&&calls==1&&accessLost.AcquiredRows==0);accessLost.Dispose();
        int timeoutCalls=0;
        var timeout=new NativePresentationReader((r,_)=>{if(++timeoutCalls==1)return null;r.FakeAcquire();r.FakeRelease();return Row(1,100,100);});
        Check("WAIT_TIMEOUT-does-not-acquire-or-fail",timeout.Read(default)==null&&!timeout.Failed&&timeout.AcquiredRows==0);
        timeout.Read(default);Check("returned-row-not-yet-trace-committed",timeout.ProducedRows==1&&timeout.CommittedRows==0&&!timeout.CanSeal);
        timeout.CommitThrough(1);timeout.Dispose();Check("normal-close-requires-native-and-trace-counts",timeout.CanSeal);
        var readError=new NativePresentationReader((r,_)=>{r.FakeAcquire();try{throw new IOException("Generated map failure");}finally{r.FakeRelease();}});
        Check("readback-error-preserves-acquired-vs-produced",Rejected(()=>readError.Read(default))&&readError.AcquiredRows==1&&readError.ReleasedRows==1&&readError.ProducedRows==0);
        readError.Dispose();Check("readback-error-cannot-seal",!readError.CanSeal);
        var cancelled=new NativePresentationReader((r,_)=>{r.FakeAcquire();try{throw new OperationCanceledException("Generated acquired-frame cancellation");}finally{r.FakeRelease();}});
        bool sawCancel=false;try{cancelled.Read(default);}catch(OperationCanceledException){sawCancel=true;}
        cancelled.Dispose();Check("cancellation-never-fabricates-a-committed-row",sawCancel&&cancelled.AcquiredRows==1&&cancelled.ReleasedRows==1&&cancelled.ProducedRows==0&&cancelled.CommittedRows==0&&!cancelled.CanSeal);
        var missingRelease=new NativePresentationReader((r,_)=>{r.FakeAcquire();return Row(1,100,100);});
        Check("missing-native-release-rejected",Rejected(()=>missingRelease.Read(default)));Check("unknown-release-retains-native-owner",Rejected(()=>missingRelease.Dispose())&&!missingRelease.CanSeal);
        var earlyCommit=new NativePresentationReader((r,_)=>{r.FakeAcquire();r.FakeRelease();return Row(1,100,100);});earlyCommit.Read(default);
        Check("unproduced-trace-commit-rejected",Rejected(()=>earlyCommit.CommitThrough(2)));earlyCommit.Dispose();
        var method=new PresentationMethod(PresentationMethod.Identity,0,@"\\.\DISPLAY1",0,0,1920,1080,PresentationMethod.MarkerProtectionScope,PresentationMethod.MonotonicMarkerScope);
        string methodHash=new('A',64);var release=new CalibrationForegroundRelease("H03",123,456,"fresh-generated-challenge");
        var request=new PresentationStart(methodHash,"H03",123,456,release.Nonce,Stopwatch.GetTimestamp());
        var binding=new PresentationPlanBinding("generated-only",methodHash,method);
        CalibrationPresentationIntegration.ValidateStart(request,release,binding,123,456,true,true);Check("matching-foreground-challenge-admits-only-generated-start",true);
        foreach(var wrong in new[]{request with{Pid=124},request with{Hwnd=457},request with{Nonce="old"},request with{MethodHash=new string('B',64)}})
            Check("wrong-presentation-start-rejects-"+cases.Count,Rejected(()=>CalibrationPresentationIntegration.ValidateStart(wrong,release,binding,123,456,true,true)));
        Check("no-foreground-cannot-start-observer",Rejected(()=>CalibrationPresentationIntegration.ValidateStart(request,release,binding,123,456,true,false)));
        foreach(var name in new[]{CalibrationPresentationReference.ReadyName,CalibrationPresentationReference.RequestName,CalibrationPresentationReference.ClosureName})
        {Check("volatile-presentation-final-"+name,CalibrationFixturePublication.IsVolatile(name));Check("volatile-presentation-pending-"+name,CalibrationFixturePublication.IsVolatile(name+".pending"));}
        NativePresentationReader Feed()=>new((r,_)=>{Thread.Sleep(1);r.FakeAcquire();r.FakeRelease();long now=Stopwatch.GetTimestamp();return new(r.AcquiredRows,100+(int)r.AcquiredRows,now-1,now-2,now,1,false,false,true);});
        foreach(var fault in new[]{"success","write","flush","close","ready-publish","metrics","initial-metrics","nan-initial-metrics"})
        {
            using var gotReady=new ManualResetEventSlim();PresentationClosure? terminal=null;
            var sink=new MemorySink(fault=="write"?4:0,fault=="flush",fault=="close");int metricCalls=0;
            var observer=new CalibrationPresentationReference(method,methodHash,request,Feed,()=>sink,r=>{if(fault=="ready-publish")throw new IOException("Generated Ready publication failure");gotReady.Set();},c=>terminal=c,
                ()=>{metricCalls++;if(fault=="metrics"&&metricCalls>1||fault=="initial-metrics"&&metricCalls==1)throw new IOException("Generated metrics failure");return(fault=="nan-initial-metrics"&&metricCalls==1?double.NaN:0,0);});
            bool signaled=gotReady.Wait(1000);bool rejected=false;try{observer.Dispose();}catch(IOException){rejected=true;}
            Check("actual-observer-wrapper-"+fault,fault=="success"?signaled&&!rejected&&terminal is{Successful:true,ThreadJoined:true,NativeOwnersClosed:true,WriterClosed:true}&&terminal.Acquired==terminal.Committed:rejected&&terminal?.Successful==false);
            if(fault is "initial-metrics" or "nan-initial-metrics")Check("no-invented-zero-CPU-baseline-"+fault,terminal?.ProcessCpuMilliseconds==null&&terminal?.ResourceError!=null&&terminal?.Acquired==0);
        }
        int nullClock=0,nullReads=0;PresentationClosure? nullResult=null;
        var nullObserver=new CalibrationPresentationReference(method,methodHash,request,()=>new NativePresentationReader((_,_)=>{Interlocked.Increment(ref nullReads);return null;}),()=>new MemorySink(),_=>{},c=>nullResult=c,()=> (0,0),()=>Interlocked.Increment(ref nullClock));
        SpinWait.SpinUntil(()=>nullObserver.Closure!=null,1000);Check("always-timeout-enforces-Ready-not700s",Rejected(()=>nullObserver.Dispose())&&nullReads<10&&nullResult?.Successful==false);
        Check("native-presentation-readset-has-nine-distinct-files",CalibrationPresentationIntegration.Files.Length==9&&CalibrationPresentationIntegration.Files.Distinct().Count()==9);
        using(var entered=new ManualResetEventSlim())using(var unblock=new ManualResetEventSlim())
        {
            var retainedSink=new MemorySink();int published=0;
            var blocked=new CalibrationPresentationReference(method,methodHash,request,
                ()=>new NativePresentationReader((_,_)=>{entered.Set();unblock.Wait();throw new IOException("Generated later reader fault");}),
                ()=>retainedSink,_=>{},_=>published++,()=> (0,0),joinForChecks:(_,_)=>false);
            Check("generated-reader-actually-blocked",entered.Wait(1000));
            try {Check("join-unknown-retains-owner-no-false-closure",Rejected(()=>blocked.Dispose())&&!retainedSink.Closed&&published==0);}
            finally {unblock.Set();Check("generated-blocked-thread-actually-joined-after-release",blocked.JoinGeneratedThreadForChecks(1000));}
            Check("join-error-preserved-before-later-reader-fault",blocked.Closure?.Error?.Contains("join unknown")==true&&blocked.Closure.Errors?.Length>=2&&!blocked.Closure.Successful&&!blocked.Closure.ThreadJoined&&retainedSink.Closed);
        }
        if(watch.Elapsed.TotalSeconds>=30||File.Exists(args[1]))throw new IOException("Offline bounds/fresh report");
        var report=new{kind="generated-independent-presentation-proof-controls",count=cases.Count,cases,points,seconds=watch.Elapsed.TotalSeconds,
            nativeAcquisitionCalls=0,deviceCalls=0,hardwareVerified=false,checkRouteMakesNativeCalls=false,separateFixtureNativeEntryNeedsAdmission=true,empiricalAcceptance=false};
        using var file=new FileStream(args[1],FileMode.CreateNew,FileAccess.Write,FileShare.Read);JsonSerializer.Serialize(file,report,new JsonSerializerOptions{WriteIndented=true});
        Console.WriteLine($"Passed {cases.Count} generated presentation controls;no hardware/native acquisition.");return 0;
    }
}
