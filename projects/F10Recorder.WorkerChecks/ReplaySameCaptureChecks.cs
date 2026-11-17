using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    internal static int SameCaptureEnvelopeChecks(string templatePath,string reportPath)
    {
        Require(!File.Exists(reportPath),"Preserve prior envelope checks.");
        var template=JsonNode.Parse(File.ReadAllBytes(templatePath)).AsObject();var cases=new List<object>();
        var now=DateTime.UtcNow;
        int maximumBytes=0;
        JsonObject Filled(int count)
        {
            var a=template.DeepClone().AsObject();a["createdUtc"]=now.AddSeconds(-1);a["expiresUtc"]=now.AddHours(1);
            string c00=Path.GetDirectoryName(Path.GetDirectoryName(a["OriginalSelectionPath"].GetValue<string>()));
            string session=Path.Combine(c00,"H03","buffer","session-"+new string('a',32)),snapshot=Path.Combine(session,"save-"+new string('b',32));
            var inputs=new JsonArray();var selected=new JsonArray();
            for(int n=0;n<count;n++)
            {
                string original=Path.Combine(snapshot,$"chunk-{n:D9}.ts"),current=Path.Combine(session,$"paired-input-{new string('c',32)}-{n:D6}.ts");
                inputs.Add(new JsonObject{["OriginalPath"]=original,["CurrentPath"]=current,["ContentSha256"]=new string('A',64)});selected.Add(current);
            }
            a["Inputs"]=inputs;a["Resources"]["SelectedRetainedFiles"]=selected;return a;
        }
        foreach(int count in new[]{1,61,65})
        {
            var a=Filled(count);byte[] raw=JsonSerializer.SerializeToUtf8Bytes(a);maximumBytes=Math.Max(maximumBytes,raw.Length);
            var p=ReadRetainedFinal(raw,a["prospectivePlanPath"].GetValue<string>());
            Require(p.Inputs.Length==count&&p.Resources.SelectedRetainedFiles.Length==count&&p.ExpiresUtc.Kind==DateTimeKind.Utc&&raw.Length<=131072,
                "Actual v10 reader/maximum-path-size roundtrip failed.");
            cases.Add(new{name="actual-v10-envelope-"+count,bytes=raw.Length});
        }
        foreach(string fault in new[]{"unknown-field","wrong-owner","missing-expiry","duplicate-member"})
        {
            var a=Filled(61);
            if(fault=="unknown-field")a["unreviewed"]=true;
            if(fault=="wrong-owner")a["controllerKind"]="issue83-two-confirmation-owner-v1";
            if(fault=="missing-expiry")a.Remove("expiresUtc");
            byte[] raw=JsonSerializer.SerializeToUtf8Bytes(a);
            if(fault=="duplicate-member")raw=System.Text.Encoding.UTF8.GetBytes("{\"operation\":\"retained-H03-1m\","+System.Text.Encoding.UTF8.GetString(raw)[1..]);
            bool rejected=false;try{_=ReadRetainedFinal(raw,a["prospectivePlanPath"].GetValue<string>());}catch(Exception e)when(e is IOException or JsonException or KeyNotFoundException or InvalidOperationException){rejected=true;}
            Require(rejected,"Unreviewed actual retained envelope accepted: "+fault);cases.Add(new{name="rejected-"+fault});
        }
        Write(reportPath,new{result="passed-actual-v10-retained-envelope-roundtrip",count=cases.Count,maximumBytes,cases,
            generatedMetadataOnly=true,empiricalAcceptance=false,mediaRead=false,nativeCalls=false,processesLaunched=0});
        Console.WriteLine($"Passed {cases.Count} actual retained-envelope controls; max{maximumBytes}B; no media/native.");return 0;
    }
    internal static int SameCapturePlanShape(string path)
    {
        var p=ContextFixturePlan() with {FreshCandidateAdmission=SameCaptureContextAdmission,MaximumChildren=16,ValidationChildren=6,OfflineDeadlineSeconds=900};
        Require(ContextEnvelopeValid(p),"Prospective C00 shape must match current implemented admission.");
        var retained=new CalibrationPlan("unqueried-retained-output","unqueried-engine","same-C00-original-acquisition",new(),["H03"],["unqueried-metadata"],0,0,
            MaximumChildren:9,BudgetBytes:13_000_000_000,StopBytes:12_000_000_000,DeadlineSeconds:900,RetainedOfflineSeconds:0,MetadataHandleRoots:["unqueried-metadata"],
            ReuseCurrentMetadataHandles:true,DirectoryLimit:128,OutputComponentBytes:500_000_000,RetainedBudgetBytes:1_000_000_000,FileLimit:8192,
            OfflineDeadlineSeconds:600,OutsideBuildAllowanceBytes:2_000_000_000,FreezeRetainedAllocations:true,ReplayExporterRecipe:ReplaySaveExport.PolicyIdentity,
            ValidationChildren:5,SourceComponentBytes:200_000_000,CopyFallbackReserveBytes:ReplayReferenceModel.SnapshotEnvelope(65536,1),SelectedRetainedFiles:[])
            {ReplayMinutes=1,FreshCandidateAdmission=SameCaptureRetainedAdmission};
        Write(path,new{C00=p,Retained=retained,NewCohortBytes=NewCohortBytes(p),NewCohortFiles=QualificationFileForecast(p),
            RequiredC00CallerFree=QualificationFreeRequirement(p),RequiredRetainedCallerFree=QualificationFreeRequirement(retained),
            ContextArguments=CaptureTemplate("0").Select(a=>a.Replace("<clock>","<common-clock-filter>")).ToArray(),
            ReplayReferenceModel.ExporterIdentity,windowsSha256=ReplayReferenceModel.AssemblyHash,coreSha256=ReplayReferenceModel.CoreHash,
            recipeHash=ReplayCalibrationCatalog.RecipeHash,observationMilliseconds=ReplayCapacityPolicy.CoverageMilliseconds});
        Console.WriteLine("Wrote implemented prospective shapes and PURE argument/commitment metadata; no device/native/media/process.");return 0;
    }
    internal static int SameCaptureFreezeRequest(string configuration,string output)
    {
        using var document=JsonDocument.Parse(File.ReadAllBytes(configuration));var c=document.RootElement;
        var originals=c.GetProperty("parents").EnumerateArray().Select((p,n)=>{
            string path=p.GetProperty("path").GetString(),hash=p.GetProperty("sha256").GetString();
            byte[] bytes=ReadCoverageMetadata(path,hash);
            return new ReplayQualificationParent(n==0?"original-5m-3T3H":"original-30m-3T3H",bytes,hash,
                ReplayCalibrationCatalog.OriginalModelHash(bytes),c.GetProperty("historicalOmissionReviewHash").GetString());
        }).ToArray();
        Require(originals.Length==2,"Two unchanged original parents required.");
        foreach(var parent in originals)_=ReplayCalibrationCatalog.ReadMeasurement(parent);
        var host=c.GetProperty("host").Deserialize<ReplayHostFacts>();Require(host.Valid(),"Expected canonical host operands are invalid.");
        var context=new ReplayQualificationContext(CaptureTemplate("0").Select(a=>a.Replace("<clock>","<common-clock-filter>")).ToArray(),1920,1080,
            c.GetProperty("endpoint").GetString(),48000,2,"f32le");
        var review=new ReplayApplicabilityReview(c.GetProperty("reviewEvidenceHash").GetString(),c.GetProperty("sourceDeltaHash").GetString(),
            c.GetProperty("reviewer").GetString(),DateTime.UtcNow,true,originals.Select(p=>p.Sha256).ToArray());
        Write(output,new CoverageFreezeRequest(originals,context,host,review),524288);
        Console.WriteLine("Wrote original-parent/expected-host/context/applicability REQUEST only; no freeze, device query, future receipt or approval window.");return 0;
    }
    internal static int SameCaptureOfflineChecks(string output)
    {
        Require(!File.Exists(output),"Preserve prior same-C00 pure report.");
        var watch=Stopwatch.StartNew();var cases=new List<string>();
        void Check(string name,bool value){Require(value,"Same-C00 pure check: "+name);cases.Add(name);}
        bool Rejected(Action a){try{a();return false;}catch(Exception e)when(e is IOException or ArgumentException or InvalidOperationException or OverflowException){return true;}}
        string H(string s)=>ReplayReferenceModel.Hash(s);
        var inputs=Enumerable.Range(0,301).Select(n=>new ReplayCompletedInputDigest(Path.GetFullPath($"artifacts/generated-nonmedia/chunk-{n:D9}.ts"),n,n+1,
            new("controlled-volume","controlled-id-"+n,4096,17,4096),H("controlled-digest-"+n))).ToArray();
        var info=new ReplaySessionInfo("metadata","metadata",300,20,60,"h264_nvenc",".ts",true,true);
        var request=new ReplayExportRequest(info,new(.2,300.2,300),inputs.Select(i=>new ReplayOutputInput(i.Path,i.Start,i.End,i.Extent)).ToArray(),"unused","unused",new());
        string selection=ReplayPairedSelectionLease.Identity(request,"controlled-scope");
        var q=JsonSerializer.Deserialize<ReplayCalibrationSourceReceipt>("{}") with { Scope="controlled-scope",AcquisitionIdentity=H("C00 acquisition"),
            SelectionHash=selection,InputBytes=inputs.Sum(i=>i.Extent.Length),SegmentCount=inputs.Length,OutputStart=.2,OutputEnd=300.2,ExportSeconds=300,SampleHash=H("packet") };
        var c00=new ReplayCoverageConfirmation("C00-current-5m-pair",H("freeze"),"controlled-target",null,DateTime.UtcNow.AddMinutes(-2),DateTime.UtcNow.AddMinutes(-1),H("admission"),H("observed"),null,null,q,q with { SampleHash=H("exact") },H("confirmation"));
        var m=new ReplaySameCaptureManifest(ReplayCalibrationCatalog.SameCaptureRevision,c00.ConfirmationHash,q.AcquisitionIdentity,selection,.2,300.2,H("clock"),H("reference"),inputs,null);
        ReplaySameCaptureManifest Seal(ReplaySameCaptureManifest value)=>value with { ManifestHash=ReplayCalibrationCatalog.SameManifestHash(value) };
        m=Seal(m);ReplayCalibrationCatalog.ValidateSameManifest(m,c00);Check("complete-generated-manifest-and-native-selection",true);
        var minute=ReplayCalibrationCatalog.SameCaptureMinute(m);Check("prospective-final-minute-complete-head",minute.Length==61&&minute[0].Start==240&&minute[^1].End==301);
        foreach(var bad in new[]{m with {C00ConfirmationHash=H("wrong parent")},m with {End=301.2},m with {SourceClockHash=null},m with {SourceReferenceHash=null},
            m with {Inputs=inputs.Take(300).ToArray()},m with {Inputs=inputs.Select((i,n)=>n==1?i with{Extent=i.Extent with{Identity="substituted"}}:i).ToArray()},
            m with {Inputs=inputs.Select((i,n)=>n==299?i with{Sha256=null}:i).ToArray()},m with {Inputs=inputs.Reverse().ToArray()}})
            Check("rejected-manifest-"+cases.Count,Rejected(()=>ReplayCalibrationCatalog.ValidateSameManifest(Seal(bad),c00)));
        Check("missing-C00-rejected",Rejected(()=>ReplayCalibrationCatalog.ValidateSameManifest(m,null)));
        var checks=new[]{q,c00.Exact}.Select(x=>new ReplaySameCaptureOutputCheck(x.SampleHash,H("new decoder "+x.SampleHash),H("source evidence"),18000,18000,140,.001,.009,.025)).ToArray();
        var acceptance=new ReplaySameCaptureAcceptance(c00.ConfirmationHash,m.ManifestHash,CalibrationSourceReference.Policy,H("oracle report"),checks,"controlled reviewer",DateTime.UtcNow);
        ReplayCalibrationCatalog.ValidateSameAcceptance(acceptance,c00,m);Check("generated-nonempirical-acceptance-shape",true);
        foreach(var bad in new[]{checks[0] with{ComparedPictures=17999},checks[0] with{AudioMarkers=19},checks[0] with{MaximumBoundaryErrorSeconds=.017},
            checks[0] with{MaximumAvErrorSeconds=.026},checks[0] with{ReferenceUncertaintySeconds=double.NaN}})
            Check("rejected-oracle-summary-"+cases.Count,Rejected(()=>ReplayCalibrationCatalog.ValidateSameAcceptance(acceptance with{Outputs=[bad,checks[1]]},c00,m)));
        // Actual marker classifier, over generated PCM only: no codec, device or media file.
        const int frames=24000,offset=2400,id=127;var pcm=new float[frames*2];
        for(int n=0;n<frames-offset;n++) pcm[2*(n+offset)]=pcm[2*(n+offset)+1]=CalibrationSourceReference.Signal((long)id*CalibrationSourceReference.Block+n*6);
        var marker=LocateSameMarker(pcm,offset+40);Check("generated-PCM-absolute-ID-and-five-ms-offset",marker.Id==id&&marker.Sample==offset&&marker.Correlation>.999);
        Check("generated-silence-rejected",Rejected(()=>LocateSameMarker(new float[frames*2],offset)));
        Check("unbounded-marker-context-rejected",Rejected(()=>LocateSameMarker(pcm,0)));
        Check("opposite-signed-AV-residuals-rejected",SameAvBound(.03,.03,-.03,-.03,.001)>.05);
        Check("sample-grid-uncertainty-cannot-pass50ms-edge",SameAvBound(.02,.02,-.0299,-.0299,SameReferenceSamplingSeconds)>.05);
        Check("common-positive-latency-cancels-without-offset-fit",SameAvBound(.02,.02,.02,.02,.005)==.005);
        Check("independent-extrema-not-same-index-pairing",SameAvBound(0,.06,0,.06,0)>.05);
        Check("opposite-sign-maxima-cannot-cancel",SameAvBound(-.03,-.02,.02,.03,.001)>.05);
        Check("audio-output-range-union-cannot-reduce-bound",SameAvBound(-.01,.02,.015,.017,.02)<=SameAvBound(-.01,.02,-.05,.017,.02));
        Check("increasing-uncertainty-cannot-reduce-signed-bound",SameAvBound(-.02,.03,.01,.02,.01)<=SameAvBound(-.02,.03,.01,.02,.03));
        Check("reversed-visual-range-rejected",Rejected(()=>SameAvBound(1,0,0,1,0)));
        Check("reversed-audio-range-rejected",Rejected(()=>SameAvBound(0,1,1,0,0)));
        Check("negative-relative-uncertainty-rejected",Rejected(()=>SameAvBound(0,1,0,1,-.001)));
        Check("nonfinite-signed-range-rejected",Rejected(()=>SameAvBound(double.NaN,1,0,1,0)));
        Check("nonfinite-relative-uncertainty-rejected",Rejected(()=>SameAvBound(0,1,0,1,double.PositiveInfinity)));
        const double ub=.000002,uc=.00025,ui=.0003,uf=10000000;
        double NativeU(double gdi)=>SameReferenceUncertainty(true,ub,uc,gdi,ui,uf);
        foreach(double half in new[]{0d,.001,.00868265,.0125})
        {
            Check("native-U-independent-of-diagnostic-GDI-"+half,NativeU(half)==NativeU(0));
            double expected=ub+uc+1d/60+half+ui+SameReferenceSamplingSeconds;
            Check("legacy-U-bitwise-original-"+half,SameReferenceUncertainty(false,ub,uc,half,ui,uf)==expected);
        }
        foreach(double frequency in new[]{1000000d,10000000d,1000000000d})
            Check("native-U-retains-one-full-QPC-tick-"+frequency,Math.Abs(SameReferenceUncertainty(true,ub,uc,0,ui,frequency)-SameReferenceUncertainty(false,ub,uc,0,ui,frequency)-1d/frequency)<1e-16);
        double currentU=NativeU(.0125);
        var boundedBirth=new PresentationBirth(103,10080000,10000000);
        var (minimumResidual,maximumResidual)=boundedBirth.ResidualBounds(1+.05-currentU+.001,0,uf);
        double estimatedResidual=(minimumResidual+maximumResidual)/2;
        Check("adjacent-presentation-residual-preserves-50ms-av-bound",
            SameAvBound(estimatedResidual,estimatedResidual,0,0,currentU)<.05
            &&SameAvBound(minimumResidual,maximumResidual,0,0,currentU)>.05);
        Check("native-U-retains-bridge",Math.Abs(SameReferenceUncertainty(true,ub+.001,uc,.0125,ui,uf)-currentU-.001)<1e-16);
        Check("native-U-retains-render-clock-bracket",Math.Abs(SameReferenceUncertainty(true,ub,uc+.001,.0125,ui,uf)-currentU-.001)<1e-16);
        Check("native-U-retains-interpolation",Math.Abs(SameReferenceUncertainty(true,ub,uc,.0125,ui+.001,uf)-currentU-.001)<1e-16);
        Check("native-U-retains-CFR-and-complete-sampling",SameReferenceUncertainty(true,0,0,0,0,uf)==1d/60+SameReferenceSamplingSeconds+1d/uf);
        Check("sampling-still-includes-existing-conversion-guard",SameReferenceSamplingSeconds==1d/8000+1d/48000+.000001);
        Check("native-U-under-50ms-case-can-pass",SameAvBound(.001,.001,0,0,currentU)<.05);
        Check("native-U-cannot-pass-50ms-error",SameAvBound(.05,.05,0,0,currentU)>.05);
        Check("native-U-applied-at-threshold-edge",SameAvBound(.05-currentU+.000001,.05-currentU+.000001,0,0,currentU)>.05);
        foreach(var bad in new[]{new[]{double.NaN,uc,.001,ui,uf},new[]{ub,double.PositiveInfinity,.001,ui,uf},new[]{ub,uc,double.NaN,ui,uf},
            new[]{ub,uc,.001,-1d,uf},new[]{-1d,uc,.001,ui,uf},new[]{ub,uc,.001,ui,0d},new[]{ub,uc,.001,ui,-1d},new[]{double.MaxValue,double.MaxValue,.001,ui,uf}})
            Check("invalid-actual-U-input-rejected-"+cases.Count,Rejected(()=>SameReferenceUncertainty(true,bad[0],bad[1],bad[2],bad[3],bad[4])));
        var v5spec=new PresentationMethod(PresentationMethod.Identity,0,@"\\.\DISPLAY1",0,0,1920,1080,PresentationMethod.MarkerProtectionScope,PresentationMethod.MonotonicMarkerScope);
        PresentationMethod.Validate(v5spec);Check("actual-v5-method-validates",v5spec.Method=="independent-desktop-presentation-interval-v5");
        Check("v2-method-cannot-silently-use-v5-estimator",Rejected(()=>PresentationMethod.Validate(v5spec with{Method="independent-desktop-presentation-interval-v2"})));
        var phaseBinding=new PresentationPlanBinding("generated-only",new string('A',64),v5spec);
        var phaseRequest=new PresentationStart(phaseBinding.Hash,"H03",123,456,"generated-phase-nonce",100);
        var phaseRelease=new CalibrationForegroundRelease("H03",123,456,phaseRequest.Nonce);
        int audioStarts=0,nativeStarts=0;
        int StartPhase(PresentationStart request,Func<(bool Visible,bool Foreground)> state,Action audio=null)
            =>CalibrationPresentationIntegration.AfterValidatedForeground(request,phaseRelease,phaseBinding,123,456,state,
                _=>{audioStarts++;audio?.Invoke();},()=>{nativeStarts++;return 42;});
        foreach(var wrong in new[]{phaseRequest with{Nonce="old"},phaseRequest with{MethodHash=new string('B',64)},phaseRequest with{Pid=124}})
            Check("phase-wrong-identity-never-starts-factories-"+cases.Count,Rejected(()=>StartPhase(wrong,()=> (true,true)))&&audioStarts==0&&nativeStarts==0);
        Check("phase-no-foreground-never-starts-factories",Rejected(()=>StartPhase(phaseRequest,()=> (true,false)))&&audioStarts==0&&nativeStarts==0);
        Check("phase-invisible-never-starts-factories",Rejected(()=>StartPhase(phaseRequest,()=> (false,true)))&&audioStarts==0&&nativeStarts==0);
        bool audioReady=false;
        int value=CalibrationPresentationIntegration.AfterValidatedForeground(phaseRequest,phaseRelease,phaseBinding,123,456,()=> (true,true),_=>audioReady=true,()=>audioReady?42:throw new IOException("Native before audio Ready"));
        Check("phase-audio-ready-before-native-factory",value==42&&audioReady);
        Check("phase-audio-failure-prevents-native",Rejected(()=>StartPhase(phaseRequest,()=> (true,true),()=>throw new IOException("Generated audio startup failure")))&&nativeStarts==0);
        int stateCalls=0;
        Check("phase-post-audio-foreground-loss-prevents-native",Rejected(()=>StartPhase(phaseRequest,()=> (true,++stateCalls==1)))&&nativeStarts==0&&stateCalls==2);
        var prePaint=new StringWriter();CalibrationReferencePhase.WritePaint(prePaint,1,1000,1026,1000,false);
        Check("wide-pre-admission-paint-retained-unqualified",prePaint.ToString().Contains("1,1000,1026",StringComparison.Ordinal));
        string budgetHeader="id,before_qpc,after_qpc"+Environment.NewLine,budgetRow="1,1000,1026"+Environment.NewLine;
        int exactBytes=System.Text.Encoding.UTF8.GetByteCount(budgetHeader+budgetRow);
        var exactWriter=new StringWriter();var exactBudget=new CalibrationReferencePhase.TextBudget(exactBytes);exactBudget.Write(exactWriter,budgetHeader);
        CalibrationReferencePhase.WritePaint(exactWriter,1,1000,1026,1000,false,exactBudget);
        Check("pre-admission-exact-cap-charges-header-and-wide-row",exactBudget.Bytes==exactBytes&&exactWriter.ToString()==budgetHeader+budgetRow);
        var shortWriter=new StringWriter();var shortBudget=new CalibrationReferencePhase.TextBudget(exactBytes-1);shortBudget.Write(shortWriter,budgetHeader);
        Check("pre-admission-one-byte-over-cap-rejects-before-row-write",Rejected(()=>CalibrationReferencePhase.WritePaint(shortWriter,1,1000,1026,1000,false,shortBudget))
            &&shortWriter.ToString()==budgetHeader&&shortBudget.Bytes==System.Text.Encoding.UTF8.GetByteCount(budgetHeader));
        Check("diagnostic-summary-exact16KiB-serialized-bound",CalibrationReferencePhase.DiagnosticSummaryBytes(new string('a',16382)).Length==16384);
        Check("diagnostic-summary-one-byte-over-bound-rejects-before-file",Rejected(()=>CalibrationReferencePhase.DiagnosticSummaryBytes(new string('a',16383))));
        foreach(string mode in new[]{"native-qualified","legacy"})
        {var badPaint=new StringWriter();Check("wide-"+mode+"-paint-retained-then-rejected",Rejected(()=>CalibrationReferencePhase.WritePaint(badPaint,1,1000,1026,1000,true))&&badPaint.ToString().Contains("1,1000,1026",StringComparison.Ordinal));}
        var edgePaint=new StringWriter();CalibrationReferencePhase.WritePaint(edgePaint,1,1000,1025,1000,true);Check("qualified-exact25ms-boundary-remains-valid",edgePaint.ToString().Contains("1025",StringComparison.Ordinal));
        Check("qualified25ms-plus-one-tick-rejects",Rejected(()=>CalibrationReferencePhase.WritePaint(new StringWriter(),1,10000000,10250001,10000000,true)));
        Check("invalid-paint-clock-rejects",Rejected(()=>CalibrationReferencePhase.WritePaint(new StringWriter(),1,1000,999,1000,false)));
        Check("invalid-paint-frequency-rejects",Rejected(()=>CalibrationReferencePhase.WritePaint(new StringWriter(),1,1000,1025,0,false)));
        CalibrationReferencePhase.ValidateArm(CalibrationReferencePhase.Identity,100,110,120,130,new long[]{111,112},new double[]{111,112});Check("complete-request-arm-Ready-epoch-phase-valid",true);
        foreach(var boundary in new[]{(phase:"missing",request:100L,arm:110L,ready:120L,epoch:130d),(phase:CalibrationReferencePhase.Identity,request:0L,arm:110L,ready:120L,epoch:130d),
            (phase:CalibrationReferencePhase.Identity,request:100L,arm:99L,ready:120L,epoch:130d),(phase:CalibrationReferencePhase.Identity,request:100L,arm:110L,ready:109L,epoch:130d),
            (phase:CalibrationReferencePhase.Identity,request:100L,arm:110L,ready:120L,epoch:119d),(phase:CalibrationReferencePhase.Identity,request:100L,arm:110L,ready:120L,epoch:double.NaN)})
            Check("invalid-phase-arm-boundary-rejected-"+cases.Count,Rejected(()=>CalibrationReferencePhase.ValidateArm(boundary.phase,boundary.request,boundary.arm,boundary.ready,boundary.epoch,new long[]{111},new double[]{111})));
        Check("pre-arm-qualified-paint-cannot-be-filtered",Rejected(()=>CalibrationReferencePhase.ValidateArm(CalibrationReferencePhase.Identity,100,110,120,130,new long[]{109,111},new double[]{111})));
        Check("pre-arm-qualified-clock-cannot-be-filtered",Rejected(()=>CalibrationReferencePhase.ValidateArm(CalibrationReferencePhase.Identity,100,110,120,130,new long[]{111},new double[]{109,111})));
        Check("unknown-qualified-clock-rejects",Rejected(()=>CalibrationReferencePhase.ValidateArm(CalibrationReferencePhase.Identity,100,110,120,130,new long[]{111},new double[]{double.NaN})));
        Check("empty-qualified-phase-rejects",Rejected(()=>CalibrationReferencePhase.ValidateArm(CalibrationReferencePhase.Identity,100,110,120,130,Array.Empty<long>(),Array.Empty<double>())));
        int relativeCases=0;bool relativeExtrema=true;
        for(int v0=-4;v0<=4;v0++)for(int v1=v0;v1<=4;v1++)
        for(int a0=-4;a0<=4;a0++)for(int a1=a0;a1<=4;a1++)foreach(double u in new[]{0d,.25})
        {
            double observed=0;
            for(int v=v0;v<=v1;v++)for(int a=a0;a<=a1;a++)
                observed=Math.Max(observed,Math.Max(Math.Abs(v-a-u),Math.Abs(v-a+u)));
            relativeExtrema&=SameAvBound(v0,v1,a0,a1,u)==observed;relativeCases++;
        }
        Check("4050-independent-signed-range-Cartesian-controls",relativeExtrema&&relativeCases==4050);
        Check("integer-PTS-ignores-printed-time-rounding",SameFirstTimestamp("[Parsed_showinfo_1 @ 0x0] n:0 pts: 800 pts_time:0.016667",@"\[Parsed_showinfo_",48000)==1d/60);
        var same=ContextFixturePlan() with {FreshCandidateAdmission=SameCaptureContextAdmission,MaximumChildren=16,ValidationChildren=6,OfflineDeadlineSeconds=900};
        Check("v10-finite-C00-admission",ContextEnvelopeValid(same));Check("v9-role-ceiling-not-v10",!ContextEnvelopeValid(same with{MaximumChildren=13}));
        Check("v10-fixture615s",FixtureSeconds(same)==615&&CalibrationPattern.MaximumAnimatedFrame(615)<CalibrationPattern.ReferenceSourceIdRange);
        Check("oracle-argument-plan-no-producer",SameOracleArguments("controlled-concat","controlled-output",true,0).Contains("[pcm]"));
        Check("actual-sampler-phase-names-admitted",new[]{SameDecodePhase,SameSourcePhase,SameOutputPhase}.All(x=>System.Text.RegularExpressions.Regex.IsMatch(x,"^[a-z0-9-]{1,32}$")));
        string caseRoot=Path.GetFullPath("artifacts/issue83-same-capture-plan-20261114/live/C00/H03");
        var longPaths=Enumerable.Range(0,305).Select(n=>inputs[0] with {Path=Path.Combine(caseRoot,"buffer","session-"+new string('a',32),$"paired-input-{new string('b',32)}-{n:D6}.ts"),
            Start=n+.1234567890123,End=n+1.1234567890123}).ToArray();
        string concat=SameOracleConcat(longPaths,Path.Combine(caseRoot,"reference-oracle","source.ffconcat"));
        Check("actual305-GUID-source-concat-fits64KiB",System.Text.Encoding.UTF8.GetByteCount(concat)<=65536&&concat.Contains("../buffer/session-"));
        Require(watch.Elapsed.TotalSeconds<30,"Pure30s bound.");
        Write(output,new{result="passed-generated-same-C00-policy-clock-PCM-argument-checks",count=cases.Count,cases,seconds=watch.Elapsed.TotalSeconds,
            empiricalAvAcceptance=false,mediaRead=false,nativeCalls=false,processesLaunched=0});
        Console.WriteLine($"Passed {cases.Count} pure same-C00 rules/generated PCM checks; no empirical A/V acceptance.");return 0;
    }
    internal static int AcceptSameCapture(string dataPath,string dataHash,string confirmationPath,string confirmationHash,
        string manifestPath,string manifestHash,string reviewer,string output)
    {
        Require(!File.Exists(output)&&!string.IsNullOrWhiteSpace(reviewer)&&reviewer.Length<=256,"Fresh named independent acceptance required.");
        using var data=JsonDocument.Parse(ReadCoverageMetadata(dataPath,dataHash));var d=data.RootElement;
        var c=ReplayCalibrationCatalog.ReadEvidence<ReplayCoverageConfirmation>(ReadCoverageMetadata(confirmationPath,confirmationHash));
        var m=ReplayCalibrationCatalog.ReadEvidence<ReplaySameCaptureManifest>(ReadCoverageMetadata(manifestPath,manifestHash));
        string root=Path.GetDirectoryName(confirmationPath),oracle=Path.Combine(root,"H03","reference-oracle");
        var checks=d.GetProperty("outputs").Deserialize<ReplaySameCaptureOutputCheck[]>();
        using var report=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(oracle,"report.json")));
        var actualChecks=report.RootElement.GetProperty("outputs").Deserialize<ReplaySameCaptureOutputCheck[]>();
        Require(checks!=null&&actualChecks!=null&&checks.SequenceEqual(actualChecks)
            && checks.All(x=>x.SourceEvidenceHash==report.RootElement.GetProperty("EvidenceHash").GetString()),
            "Acceptance summaries differ from the concrete independent oracle report.");
        if(c.Role=="C00-current-5m-pair")ReplayCalibrationCatalog.ValidateSameManifest(m,c);
        Require(d.GetProperty("confirmationHash").GetString()==c.ConfirmationHash&&d.GetProperty("manifestHash").GetString()==m.ManifestHash
            &&d.GetProperty("oraclePolicy").GetString()==CalibrationSourceReference.Policy
            &&d.GetProperty("oracleEvidenceHash").GetString()==HashFile(Path.Combine(oracle,"report.json")),"Actual independent oracle/confirmation/manifest binding required.");
        for(int n=0;n<2;n++) Require(checks[n].DecodeEvidenceHash==HashFile(Path.Combine(oracle,"output-"+n,"process.json")),"Oracle decoder evidence changed.");
        var a=new ReplaySameCaptureAcceptance(c.ConfirmationHash,m.ManifestHash,CalibrationSourceReference.Policy,
            d.GetProperty("oracleEvidenceHash").GetString(),checks,reviewer,DateTime.UtcNow);
        ReplayCalibrationCatalog.ValidateSameAcceptance(a,c,m);Write(output,a);
        Console.WriteLine("Recorded named independent review of actual oracle evidence; no capture/catalog activation.");return 0;
    }
}
