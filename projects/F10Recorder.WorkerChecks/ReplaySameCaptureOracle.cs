using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    internal const string SameDecodePhase="same-c00-reference-decode",SameSourcePhase="same-c00-source-analysis",SameOutputPhase="same-c00-output-analysis";
    internal const double SameReferenceSamplingSeconds=1d/8000+1d/48000+.000001;
    internal const string SameRelativeAvMethod="signed-independent-error-interval-v2";
    internal static double SameReferenceUncertainty(bool native,double bridge,double clockBracket,double gdiVisualHalf,double interpolationHalf,double frequency)
    {
        Require(new[]{bridge,clockBracket,gdiVisualHalf,interpolationHalf,frequency}.All(double.IsFinite)
            &&bridge>=0&&clockBracket>=0&&gdiVisualHalf>=0&&interpolationHalf>=0&&frequency>0,
            "Finite nonnegative reference uncertainties and positive QPC frequency required.");
        // GDI midpoint width applies only to its estimator. The independent
        // native point instead retains one full QPC quantization/order tick.
        double value=bridge+clockBracket+1d/60+(native?0:gdiVisualHalf)+interpolationHalf+SameReferenceSamplingSeconds+(native?1d/frequency:0);
        Require(double.IsFinite(value),"Reference uncertainty overflow.");return value;
    }
    internal static double SameAvBound(double visualMinimum,double visualMaximum,
        double audioMinimum,double audioMaximum,double uncertainty)
    {
        Require(double.IsFinite(visualMinimum)&&double.IsFinite(visualMaximum)
            &&double.IsFinite(audioMinimum)&&double.IsFinite(audioMaximum)&&double.IsFinite(uncertainty)
            &&visualMinimum<=visualMaximum&&audioMinimum<=audioMaximum&&uncertainty>=0,
            "Complete ordered finite signed A/V ranges and nonnegative uncertainty required.");
        // Every independent v-a lies in [vMin-aMax,vMax-aMin]. This retains
        // same-sign cancellation without fitting an offset or pairing events.
        return Math.Max(Math.Abs(visualMinimum-audioMaximum),Math.Abs(visualMaximum-audioMinimum))+uncertainty;
    }
    internal static double SameFirstTimestamp(string log,string marker,long units)
    {
        var matches=Regex.Matches(log,marker+@"[^\r\n]*\bpts:\s*(-?[0-9]+)\s+pts_time:");
        Require(matches.Count==1&&units>0,"Exactly one independently decoded integer timestamp/timebase is required.");
        return long.Parse(matches[0].Groups[1].Value,CultureInfo.InvariantCulture)/(double)units;
    }
    internal sealed record SameOracleRaw(string Root, string EvidenceHash, double FirstVideo, double FirstAudio,
        int[] Ids, string AudioPath, string ReceiptHash);
    internal sealed record SameOracleResult(string SourceRoot, string SourceEvidenceHash,
        ReplaySameCaptureOutputCheck[] Outputs, double Seconds, string ReportHash);
    internal static string[] SameOracleArguments(string input, string root, bool source, double origin)
    {
        string videoClock = source ? FormattableString.Invariant($"settb=1/48000,setpts=round((PTS*TB+{origin:R})*60)*800,") : "settb=1/48000,";
        string audioClock = source ? FormattableString.Invariant($"asetpts=PTS+{origin:R}/TB,") : "";
        string graph = "[0:v:0]" + videoClock + "split[ids][vt];[vt]select=eq(n\\,0),showinfo,nullsink;" +
            "[ids]crop=576:48:16:8,scale=24:2:flags=area,format=gray[marks];" +
            "[0:a:0]" + audioClock + "aresample=8000,asettb=1/8000,asplit[pcm][at];[at]aselect=eq(n\\,0),ashowinfo,anullsink";
        return ["-hide_banner", "-loglevel", "info", "-xerror", "-err_detect", "explode", "-nostdin", "-n", "-copyts",
            .. (source ? new[] { "-f", "concat", "-safe", "0", "-protocol_whitelist", "file" } : Array.Empty<string>()),
            "-i", input, "-filter_complex", graph, "-map", "[marks]", "-fps_mode", "passthrough", "-threads:v", "1", "-f", "rawvideo", Path.Combine(root, "ids.raw"),
            "-map", "[pcm]", "-ac", "2", "-c:a", "pcm_f32le", "-f", "f32le", Path.Combine(root, "audio.raw")];
    }
    internal static string SameOracleConcat(ReplayCompletedInputDigest[] mapping,string concat)
    {
        string Quote(string p)=>p.Replace("\\","/").Replace("'","'\\''");
        // Relative names keep the whole305-input source reference inside the
        // same64KiB metadata bound, even at the actual GUID/path lengths.
        string text="ffconcat version 1.0\n"+string.Join("",mapping.Select(i=>{
            string relative=Path.GetRelativePath(Path.GetDirectoryName(concat),i.Path);
            Require(!Path.IsPathFullyQualified(relative)&&relative.Replace('\\','/').StartsWith("../buffer/session-",StringComparison.Ordinal),
                "C00 oracle source must stay in its genuine sibling buffer/session namespace.");
            return FormattableString.Invariant($"file '{Quote(relative)}'\ninpoint {i.Start:R}\noutpoint {i.End:R}\nduration {i.End-i.Start:R}\n");
        }));
        Require(System.Text.Encoding.UTF8.GetByteCount(text)<=65536,"Source oracle concat64KiB bound.");return text;
    }
    static int[] SameIds(byte[] raw)
    {
        Require(raw.Length > 0 && raw.Length % 48 == 0 && raw.Length <= 48 * 19000, "Finite complete source-ID samples required.");
        var ids = new int[raw.Length / 48];
        for (int n = 0; n < ids.Length; n++) for (int bit = 0; bit < 24; bit++)
        {
            byte a = raw[n*48+bit], b = raw[n*48+24+bit];
            Require(a is <= 70 or >= 180 && b is <= 70 or >= 180 && (a >= 180) != (b >= 180), "Ambiguous source ID/complement.");
            if (a >= 180) ids[n] |= 1 << bit;
        }
        return ids;
    }
    static SameOracleRaw DecodeSameOracle(CalibrationPlan plan, string engine, string input, string root,
        bool source, double origin, int ordinal, Sampler sampler)
    {
        sampler.BeginOffline(SameDecodePhase, 32_000_000, 100);
        try
        {
            Directory.CreateDirectory(root); string[] args = SameOracleArguments(input, root, source, origin);
            string arguments = Path.Combine(root, "arguments.json"); Write(arguments, args);
            AdmitNativeChild("reference-oracle:H03");
            using var job = new Native.ProcessJob();
            var receipt = ReplayExportProcess.Run(engine, args, root, job, timeoutMilliseconds: 90000,
                checkStorage: () => sampler.Check());
            Require(receipt is { Success: true, ResourcesFinal: true, ResourceError: null }, "Independent source/reference decoder failed; no retry.");
            Write(Path.Combine(root, "process.json"), SaveProcess(root, "reference-decode", receipt));
            PublishSameOracleCreator(plan, root, arguments, args, receipt, ordinal);
            var ids = SameIds(File.ReadAllBytes(Path.Combine(root, "ids.raw")));
            string audio = Path.Combine(root, "audio.raw");
            Require(new FileInfo(audio).Length is > 0 and <= 21_000_000 && new FileInfo(audio).Length % 8 == 0,
                "Reference PCM21MB/float-stereo bound.");
            var raw = new SameOracleRaw(root, "pending", SameFirstTimestamp(receipt.StandardError,@"\[Parsed_showinfo_",48000), SameFirstTimestamp(receipt.StandardError,@"\[Parsed_ashowinfo_",8000), ids,
                audio, HashFile(Path.Combine(root, "process.json")));
            var evidence = new { raw.Root, raw.FirstVideo, raw.FirstAudio, pictures = ids.Length,
                ids = HashFile(Path.Combine(root, "ids.raw")), audio = HashFile(audio), process = raw.ReceiptHash, arguments = HashFile(arguments) };
            Write(Path.Combine(root, "evidence.json"), evidence);
            return raw with { EvidenceHash = HashFile(Path.Combine(root, "evidence.json")) };
        }
        finally { sampler.EndOffline(); }
    }
    static void PublishSameOracleCreator(CalibrationPlan plan, string root, string argumentsFile,
        string[] arguments, ReplayExportProcessReceipt child, int ordinal)
    {
        using var parent = Process.GetCurrentProcess(); string cell = Path.Combine(plan.Root,"H03");
        string final=Path.Combine(cell,$"creator-exit-packet-reference-oracle-{ordinal}.json"),temporary=final+".tmp";
        Write(temporary, new {
            kind = "qualification-child-creator-v1", planSha256 = CreatorPlanHash(plan),
            admittedPlanSha256 = HashFile(Path.Combine(plan.Root,"admitted-plan.json")),
            helperSha256 = HashFile(typeof(ReplayCalibrationRun).Assembly.Location), parentPid = parent.Id,
            parentStartedUtc = parent.StartTime.ToUniversalTime(), label = "H03", variant = "packet", role = "reference-oracle", stage = ordinal,
            admissionOrdinal = CreatorAdmission(plan,"H03","reference-oracle",ordinal,child.ProcessStartedUtc.Value),
            enginePath = MediaDependency.ResolveExecutable(), engineSha256 = MediaDependency.ExecutableSha256,
            ownership = "direct-held-joined-independent-reference", job = (object)null,
            argumentsFile, argumentsSha256 = HashFile(argumentsFile), arguments, receipt = SaveProcess(root,"reference-creator",child) });
        File.Move(temporary,final);
    }
    internal static (int Sample, int Id, double Correlation) LocateSameMarker(float[] stereo, int expected)
    {
        int frames = stereo.Length / 2;
        Require(expected >= 1600 && expected + 15000 < frames, "Marker must have complete context; no padding.");
        double best = double.NegativeInfinity; int at = -1;
        // Independently generated sync PCM, not a template taken from an output.
        var reference = Enumerable.Range(0, 1200).Select(n => (double)CalibrationSourceReference.Signal(n*6)).ToArray();
        double energy = reference.Sum(v => v*v);
        for (int lag = -400; lag <= 400; lag++)
        {
            int begin = expected + lag; double xy = 0, yy = 0;
            for (int n = 0; n < reference.Length; n++)
            { double y = (stereo[(begin+n)*2] + stereo[(begin+n)*2+1]) * .5; xy += reference[n]*y; yy += y*y; }
            double score = yy <= 1e-12 ? 0 : xy / Math.Sqrt(energy * yy);
            if (score > best) { best = score; at = begin; }
        }
        Require(best >= .8 && at >= 0, "Missing or ambiguous unique audio sync.");
        double Tone(int offset, int frequency)
        {
            double re = 0, im = 0;
            for (int n = 32; n < 368; n++)
            { double sample = (stereo[(offset+n)*2] + stereo[(offset+n)*2+1]) * .5, phase = 2*Math.PI*frequency*n/8000; re += sample*Math.Cos(phase); im += sample*Math.Sin(phase); }
            return re*re + im*im;
        }
        int id = 0;
        for (int bit = 0; bit < 16; bit++)
        {
            int a = at + 1200 + 800*bit, b = a + 400;
            double a0 = Tone(a,900), a1 = Tone(a,1500), b0 = Tone(b,900), b1 = Tone(b,1500);
            bool one = a1 > 4*a0, zero = a0 > 4*a1;
            Require(one != zero && (one ? b0 > 4*b1 : b1 > 4*b0), "Ambiguous/non-Manchester audio ID.");
            if (one) id |= 1 << bit;
        }
        Require(id <= 511, "Source audio ID outside prospective finite lifetime."); return (at,id,best);
    }
    static float[] SamePcm(string path)
    {
        var bytes = File.ReadAllBytes(path); Require(bytes.Length is > 0 and <= 21_000_000 && bytes.Length % 8 == 0, "Reference PCM bound.");
        var pcm = new float[bytes.Length/4]; Buffer.BlockCopy(bytes,0,pcm,0,bytes.Length);
        Require(pcm.All(float.IsFinite), "Nonfinite reference PCM."); return pcm;
    }
    static SameOracleResult CollectSameOracle(CalibrationPlan plan, string engine, ReplayCalibrationSourceReceipt[] outputs,
        Sample[] samples, Sampler sampler, string sourceCell, bool createSource,ReplayQualificationContext context)
    {
        var watch = Stopwatch.StartNew(); string cell = Path.Combine(plan.Root,"H03"), oracle = Path.Combine(cell,"reference-oracle");
        Directory.CreateDirectory(oracle);
        SameOracleRaw source;
        if (createSource)
        {
            var mapping = JsonSerializer.Deserialize<ReplayCompletedInputDigest[]>(File.ReadAllBytes(Path.Combine(sourceCell,"completed-input-mapping.json")));
            Require(mapping.Length is >0 and <=305 && mapping.Sum(i=>i.Extent.Length)<=1_000_000_000, "Full C00 source oracle domain.");
            string concat = Path.Combine(oracle,"source.ffconcat");
            string text=SameOracleConcat(mapping,concat);File.WriteAllText(concat,text);
            source = DecodeSameOracle(plan,engine,concat,Path.Combine(oracle,"source"),true,mapping[0].Start,0,sampler);
            Write(Path.Combine(oracle,"source-data.json"),source,524288);
        }
        else
        {
            string data = Path.Combine(sourceCell,"reference-oracle","source-data.json");
            source = JsonSerializer.Deserialize<SameOracleRaw>(File.ReadAllBytes(data));
            Require(source.EvidenceHash == HashFile(Path.Combine(source.Root,"evidence.json")),"Immutable accepted C00 source oracle changed.");
            using var evidence = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source.Root,"evidence.json")));
            var bound=evidence.RootElement;
            Require(bound.GetProperty("Root").GetString()==source.Root && bound.GetProperty("FirstVideo").GetDouble()==source.FirstVideo
                && bound.GetProperty("FirstAudio").GetDouble()==source.FirstAudio && source.AudioPath==Path.Combine(source.Root,"audio.raw")
                && Path.GetFullPath(source.Root)==Path.Combine(Path.GetFullPath(sourceCell),"reference-oracle","source"),"C00 source timing/owned paths changed.");
            Require(HashFile(source.AudioPath)==evidence.RootElement.GetProperty("audio").GetString()
                && HashFile(Path.Combine(source.Root,"ids.raw"))==evidence.RootElement.GetProperty("ids").GetString(), "C00 source oracle raw bytes changed.");
            Require(source.Ids.SequenceEqual(SameIds(File.ReadAllBytes(Path.Combine(source.Root,"ids.raw")))),"C00 source oracle IDs changed.");
        }
        sampler.BeginOffline(SameSourcePhase,0,60);bool sourcePhase=true;
        try {
        var allRows = File.ReadLines(Path.Combine(sourceCell,"source-render-clock.csv")).Skip(1).Select(s=>s.Split(',').Select(v=>double.Parse(v,CultureInfo.InvariantCulture)).ToArray()).ToArray();
        int leadingZero=allRows.TakeWhile(r=>r.Length==7&&r[1]==0).Count();
        var rows=allRows.Skip(leadingZero).ToArray();
        Require(allRows.Length<=140000&&rows.Length>2 && rows.All(r=>r.Length==7&&r[6]==0&&r.All(double.IsFinite)),"Reference clock bounds.");
        using var clock = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sourceCell,"source-clock.json"))); var c=clock.RootElement;
        double frequency=c.GetProperty("QpcFrequency").GetDouble(), epoch=c.GetProperty("QpcEpoch").GetDouble();
        using var policyDoc=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sourceCell,"source-reference-policy.json")));var policy=policyDoc.RootElement;
        Require(policy.GetProperty("Policy").GetString()==CalibrationSourceReference.Policy&&policy.GetProperty("Endpoint").GetString()==context.Endpoint
            &&policy.GetProperty("Rate").GetInt32()==48000&&policy.GetProperty("channels").GetInt32()==2
            &&policy.GetProperty("blockSamples").GetInt32()==96000&&policy.GetProperty("symbolSamples").GetInt32()==2400
            &&policy.GetProperty("syncSamples").GetInt32()==7200&&policy.GetProperty("qpcFrequency").GetDouble()==frequency,
            "Independent reference endpoint/format/marker/clock differs from actual frozen capture context.");
        double bridge=c.GetProperty("BridgeUncertaintyTicks").GetDouble()/frequency;
        Require(frequency>0 && bridge is >=0 and <=.002,"Acquisition clock uncertainty.");
        Require(leadingZero==0 || leadingZero<=40 && allRows[leadingZero-1][5]<epoch
            && (allRows[leadingZero-1][5]-allRows[0][4])/frequency<=.2,"Only the bounded pre-acquisition zero-position prefix may be excluded.");
        double clockBracket=rows.Max(r=>(r[5]-r[4])/frequency)+.0000001;
        Require(clockBracket is >0 and <=.002001 && rows.All(r=>r[3]>=r[4]*1e7/frequency-1 && r[3]<=r[5]*1e7/frequency+1),
            "Reference position timestamp is outside its actual measured QPC call bracket.");
        double nativeTimestampUncertainty=0;
        double uncertainty = bridge + clockBracket + 1d/60+SameReferenceSamplingSeconds;
        for(int n=1;n<rows.Length;n++) Require(rows[n][1]>=rows[n-1][1]&&rows[n][2]==rows[0][2]&&rows[n][3]>rows[n-1][3]
            && rows[n][0]>=rows[n-1][0] && rows[n][1]/rows[n][2]<=rows[n][0]/48000+.002
            && (rows[n][3]-rows[n-1][3])/1e7<=.025
            && Math.Abs((rows[n][1]-rows[n-1][1])/rows[n][2]-(rows[n][3]-rows[n-1][3])/1e7)<=.002,
            "Discontinuous/stale render clock/stream position.");
        var paints = File.ReadLines(Path.Combine(sourceCell,"source-paints.csv")).Skip(1).Select(s=>s.Split(',').Select(v=>long.Parse(v,CultureInfo.InvariantCulture)).ToArray()).ToArray();
        Require(paints.Length is >2 and <=40000 && paints.All(r=>r.Length==3&&r[2]>=r[1]&&(r[2]-r[1])/frequency<=.025)
            && paints.Select(r=>r[0]).Distinct().Count()==paints.Length,"Unique visual publication brackets required.");
        var paintMap=paints.ToDictionary(r=>(int)r[0]);
        var presentationBinding=PresentationPlanBinding.For(plan);
        if(presentationBinding!=null&&createSource)
        {
            Require(sameCapturePresentationOwner==null,"Only one C00 native-reference metadata owner may transfer through manifest closure");
            var paths=CalibrationPresentationIntegration.Files.Select(n=>Path.Combine(sourceCell,n)).ToArray();
            sameCapturePresentationOwner=new FrozenCalibrationHistory([],paths.Length,64,selectedFiles:paths);
            Require(sameCapturePresentationOwner.AllocatedBytes<=32_000_000,"Native reference metadata reserve32MB");
        }
        var presentation=presentationBinding==null?null:CalibrationPresentationReference.ReadClosed(sourceCell,epoch,frequency,presentationBinding.Hash,presentationBinding.Spec);
        if(presentation!=null)
        {
            var requested=CalibrationPresentationReference.ReadJson<PresentationStart>(Path.Combine(sourceCell,CalibrationPresentationReference.RequestName));
            var ready=CalibrationPresentationReference.ReadJson<PresentationReady>(Path.Combine(sourceCell,CalibrationPresentationReference.ReadyName));
            CalibrationReferencePhase.ValidateArm(policy.GetProperty("qualificationPhase").GetString(),requested.RequestedQpc,policy.GetProperty("qualificationArmQpc").GetInt64(),ready.ReadyQpc,epoch,
                paints.Select(p=>p[1]),allRows.Select(r=>r[4]));
        }
        if(presentation!=null)nativeTimestampUncertainty=1d/frequency;
        var gdiProxyFailedFrames=new List<int>();
        double sourceVisualError=0,visualHalf=0,interpolationHalf=0;
        double visualMinimum=double.PositiveInfinity,visualMaximum=double.NegativeInfinity;
        for(int n=0;n<source.Ids.Length;n++)
        {
            bool haveShown=paintMap.TryGetValue(source.Ids[n],out var shown),haveNext=paintMap.TryGetValue(source.Ids[n]+1,out var next);
            Require(haveShown&&haveNext,
                "Every source frame needs its unique current/next compositor interval.");
            double actual=source.FirstVideo+n/60d;
            bool gdiValid=actual>=(shown[1]-epoch)/frequency-1d/60-bridge&&actual<=(next[2]-epoch)/frequency+1d/60+bridge;
            if(!gdiValid)gdiProxyFailedFrames.Add(n);
            if(presentation==null)Require(gdiValid,"Stale/early source picture outside independent compositor bounds.");
            else presentation.CheckFrame(source.Ids[n],epoch+actual*frequency,bridge);
        }
        for(int n=1;n<source.Ids.Length;n++) if(source.Ids[n]!=source.Ids[n-1])
        {
            Require(paintMap.TryGetValue(source.Ids[n],out var paint),"Unknown source visual ID.");
            double actual=source.FirstVideo+n/60d, low=(paint[1]-epoch)/frequency, high=(paint[2]-epoch)/frequency;
            visualHalf=Math.Max(visualHalf,(high-low)/2);
            uncertainty=SameReferenceUncertainty(presentation!=null,bridge,clockBracket,visualHalf,interpolationHalf,frequency);
            double residual=actual-(presentation==null?(low+high)/2:(presentation.Birth(source.Ids[n]).Present-epoch)/frequency);
            sourceVisualError=Math.Max(sourceVisualError,Math.Abs(residual));
            visualMinimum=Math.Min(visualMinimum,residual);visualMaximum=Math.Max(visualMaximum,residual);
        }
        Write(Path.Combine(oracle,"source-presentation-diagnostic.json"),new{primaryMethod=presentation==null?"GDI-Refresh-DwmFlush-v1":PresentationMethod.Identity,
            gdiProxyFailedFrames,meaning="Original GDI proxy results retained;native presentation is a new independent content reference,not recorder acquire-time attribution or old-C00 acceptance."});
        var sourcePcm=SamePcm(source.AudioPath); var checks=new List<ReplaySameCaptureOutputCheck>();
        sampler.EndOffline();sourcePhase=false;
        for(int output=0;output<outputs.Length;output++)
        {
            var q=outputs[output]; var sample=samples[output];
            using var owner=new ReplayPublicationLease(sample.File);
            Require(owner.CheckedIdentity==q.QualificationOutput,"Oracle output identity differs from actual independent publication/inspection.");
            var raw=DecodeSameOracle(plan,engine,sample.File,Path.Combine(oracle,"output-"+output),false,0,createSource?output+1:output,sampler);
            sampler.BeginOffline(SameOutputPhase,0,90);
            try {
            int first=(int)Math.Ceiling((q.OutputStart-source.FirstVideo)*60-.000001), count=checked((int)Math.Round(q.ExportSeconds*60));
            Require(first>=0&&first+count<=source.Ids.Length&&raw.Ids.Length==count&&raw.Ids.SequenceEqual(source.Ids.Skip(first).Take(count)),
                "Every output picture must match independently decoded C00 source in the fixed window.");
            var pcm=SamePcm(raw.AudioPath); double audioError=0; int markers=0;var eligible=new List<int>();
            double audioMinimum=double.PositiveInfinity,audioMaximum=double.NegativeInfinity;
            for(int id=0;id<512;id++)
            {
                double position=id*2d;
                int index=Array.FindIndex(rows,r=>r[1]/r[2]>=position);
                if(position<=rows[0][1]/rows[0][2] || position>rows[^1][1]/rows[^1][2])continue;
                Require(index>0,"Interior absolute marker has no reference interpolation support.");
                var before=rows[index-1];var after=rows[index];
                double left=before[1]/before[2],right=after[1]/after[2];
                Require(right>left&&position>=left&&right-position<=.025&&position-left<=.025,"Interior marker reference support is ambiguous.");
                double ratio=(position-left)/(right-left), qpc100ns=before[3]+ratio*(after[3]-before[3]);
                double fromLeft=before[3]+(position-left)*1e7,fromRight=after[3]-(right-position)*1e7;
                interpolationHalf=Math.Max(interpolationHalf,Math.Abs(fromLeft-fromRight)/2e7);
                uncertainty=SameReferenceUncertainty(presentation!=null,bridge,clockBracket,visualHalf,interpolationHalf,frequency);
                double expected=(qpc100ns/1e7-epoch/frequency);
                if(expected<q.OutputStart+2 || expected+1.9>q.OutputEnd)continue;
                eligible.Add(id);
                sampler.Check();
                var sourceMarker=LocateSameMarker(sourcePcm,(int)Math.Round((expected-source.FirstAudio)*8000));
                var outputMarker=LocateSameMarker(pcm,(int)Math.Round((expected-q.OutputStart-raw.FirstAudio)*8000));
                Require(sourceMarker.Id==id&&outputMarker.Id==id,"Wrong absolute audio ID/integer-period substitution.");
                double sourceResidual=source.FirstAudio+sourceMarker.Sample/8000d-expected;
                double outputResidual=q.OutputStart+raw.FirstAudio+outputMarker.Sample/8000d-expected;
                audioError=Math.Max(audioError,Math.Max(Math.Abs(sourceResidual),Math.Abs(outputResidual)));
                audioMinimum=Math.Min(audioMinimum,Math.Min(sourceResidual,outputResidual));
                audioMaximum=Math.Max(audioMaximum,Math.Max(sourceResidual,outputResidual)); markers++;
                Require(watch.Elapsed.TotalSeconds<(createSource?600:480),"Whole reference oracle finite bound.");
            }
            double boundary=Math.Max(Math.Abs(source.FirstVideo+first/60d-q.OutputStart),Math.Abs(raw.FirstVideo));
            double error=SameAvBound(visualMinimum,visualMaximum,audioMinimum,audioMaximum,0);
            var check=new ReplaySameCaptureOutputCheck(q.SampleHash,raw.ReceiptHash,source.EvidenceHash,count,count,markers,boundary,error,uncertainty);
            Require(markers>=20&&boundary<=1d/60+.000001&&error+uncertainty<=.05,"Independent one-frame/50ms source/reference gate failed.");
            checks.Add(check);
            Write(Path.Combine(raw.Root,"eligible-audio-markers.json"),new{eligible,matched=markers,sourceVisualError,audioError,
                visualMinimum,visualMaximum,audioMinimum,audioMaximum,uncertainty,nativeTimestampUncertainty,diagnosticGdiVisualHalf=visualHalf,primaryVisualReferenceUncertainty=presentation==null?visualHalf:nativeTimestampUncertainty,relativeAvMethod=SameRelativeAvMethod,
                legacyAbsoluteErrorSum=sourceVisualError+audioError,
                conservativeRelativeAvBound=SameAvBound(visualMinimum,visualMaximum,audioMinimum,audioMaximum,uncertainty)});
            } finally { sampler.EndOffline(); }
        }
        Write(Path.Combine(oracle,"report.json"),new{policy=CalibrationSourceReference.Policy,relativeAvMethod=SameRelativeAvMethod,presentationMethod=presentation==null?"GDI-Refresh-DwmFlush-v1":PresentationMethod.Identity,source.Root,source.EvidenceHash,leadingZeroPositionRows=leadingZero,outputs=checks,seconds=watch.Elapsed.TotalSeconds,
            meaning="Measured source render-clock and compositor relation, then all-output source identity and absolute unique audio IDs. No fitted output offset, physical DAC/scanout, soak, game or ordinary Saved acceptance."});
        return new(source.Root,source.EvidenceHash,checks.ToArray(),watch.Elapsed.TotalSeconds,HashFile(Path.Combine(oracle,"report.json")));
        } finally { if(sourcePhase)sampler.EndOffline(); }
    }
}
