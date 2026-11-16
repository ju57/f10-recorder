using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace F10Recorder;

internal sealed record PresentationMethod(string Method,uint OutputIndex,string DeviceName,int Left,int Top,int Width,int Height,string ProtectionScope,string MarkerTransitionScope,
    int MaximumRows=150000,long MaximumBytes=20971520,int ReadySeconds=10,int JoinMilliseconds=5000,int LifetimeSeconds=700)
{
    internal const string Identity="independent-desktop-presentation-interval-v5",Role="source-presentation-method";
    internal const string MarkerProtectionScope="fixture-marker-roi;global-mask-diagnostic-v1";
    internal const string MonotonicMarkerScope="fixed-H03-source-reference;forward-only-no-divisor-no-wrap;coalesced-same-id-only-v1";
    internal static PresentationMethod Read(string path,string hash)
    {
        byte[] bytes=CalibrationPresentationReference.ReadBounded(path,8192);
        if(bytes.Length>8192||Convert.ToHexString(SHA256.HashData(bytes))!=hash)throw new IOException("Pinned presentation method changed");
        var value=JsonSerializer.Deserialize<PresentationMethod>(bytes,new JsonSerializerOptions {UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow});
        Validate(value);return value;
    }
    internal static void Validate(PresentationMethod p)
    {
        if(p==null||p.Method!=Identity||p.ProtectionScope!=MarkerProtectionScope||p.MarkerTransitionScope!=MonotonicMarkerScope||p.OutputIndex!=0||string.IsNullOrWhiteSpace(p.DeviceName)||!p.DeviceName.StartsWith(@"\\.\DISPLAY",StringComparison.Ordinal)
            ||p.Left!=0||p.Top!=0||p.Width!=1920||p.Height!=1080||p.MaximumRows!=150000||p.MaximumBytes!=20971520||p.ReadySeconds!=10||p.JoinMilliseconds!=5000||p.LifetimeSeconds!=700)
            throw new IOException("Exact finite presentation method/output required");
    }
    internal static void ValidateFixture(string label,bool sourceReference,int lifetimeSeconds)
    {
        int expectedLifetime=CalibrationForeground.UserHandoffSeconds+420+15;
        if(label!="H03"||!sourceReference||lifetimeSeconds!=expectedLifetime
            ||CalibrationPattern.MaximumAnimatedFrame(lifetimeSeconds)>=CalibrationPattern.ReferenceSourceIdRange
            ||checked(24680*256+CalibrationPattern.MaximumAnimatedFrame(lifetimeSeconds))>=0x1000000)
            throw new IOException("V5 requires the fixed monotonic H03/no-divisor/no-wrap source-reference fixture");
    }
}
internal sealed record PresentationStart(string MethodHash,string Label,int Pid,long Hwnd,string Nonce,long RequestedQpc);
internal sealed record PresentationReady(string Method,string MethodHash,PresentationStart Request,long ReadyQpc,long Frequency,string DeviceName,int Left,int Top,int Width,int Height);
internal sealed record PresentationClosure(string Method,string MethodHash,PresentationStart Request,long ReadyQpc,long Frequency,
    string DeviceName,int Left,int Top,int Width,int Height,long Acquired,long Released,long Produced,long Committed,long Rows,long Bytes,string TraceHash,
    bool NativeOwnersClosed,bool WriterClosed,bool ThreadBodyCompleted,bool ThreadJoined,bool Successful,string Error,double Seconds,double? ProcessCpuMilliseconds,long? ProcessPeakWorkingSetBytes,string ResourceError,
    string ResourceMeaning="Whole fixture process includes GDI/audio/reference/observer;not isolated observer cost;GPU is unmeasured,not zero",string[] Errors=null);

internal interface IPresentationSink:IDisposable
{
    void Write(PresentationRow row);
    void FlushDurable();
    long Rows {get;} long Bytes {get;} string Hash {get;} bool Closed {get;}
}
internal sealed class FilePresentationSink:IPresentationSink
{
    readonly ReplayGuardDirectory root;
    readonly ReplayGuardDirectory.FileTicket ticket;
    readonly FileStream file;
    readonly long maximum;
    public long Rows {get;private set;} public long Bytes {get;private set;} public string Hash {get;private set;}
    public bool Closed {get;private set;}
    internal FilePresentationSink(string path,long maximum)
    {
        this.maximum=maximum;string directory=Path.GetDirectoryName(path);
        root=new(directory,Native.DirectoryIdentity(directory),ReplayStorage.ObserveCapacity(directory).Volume);
        try {ticket=root.CreateNew(Path.GetFileName(path));file=ticket.Data();Append("ordinal,id,present,before,after,accumulated,coalesced,protected,released\n");}
        catch {ticket?.Dispose();root.Dispose();throw;}
    }
    void Append(string text)
    {byte[] b=Encoding.UTF8.GetBytes(text);if(Bytes+b.Length>maximum)throw new IOException("Presentation trace byte ceiling");file.Write(b);Bytes+=b.Length;}
    public void Write(PresentationRow r)
    {if(Closed)throw new IOException("Trace closed");Append(FormattableString.Invariant($"{r.Ordinal},{r.Id},{r.Present},{r.Before},{r.After},{r.Accumulated},{(r.Coalesced?1:0)},{(r.Protected?1:0)},{(r.Released?1:0)}\n"));Rows++;}
    public void FlushDurable()
    {file.Flush(true);long at=file.Position;file.Position=0;Hash=Convert.ToHexString(SHA256.HashData(file));file.Position=at;}
    public void Dispose()
    {if(Closed)return;file.Dispose();ticket.Dispose();root.Dispose();Closed=true;}
}

// One owner thread performs native acquire/read/release AND bounded trace writes.
// Normal stop is checked only between whole reads; it never cancels an owned frame.
internal sealed class CalibrationPresentationReference:IDisposable
{
    internal const string ResourceMeaning="Whole fixture process includes GDI/audio/reference/observer;not isolated observer cost;GPU is unmeasured,not zero";
    internal const string TraceName="source-presentation.csv",ClosureName="source-presentation.json",ReadyName="presentation-ready.json",RequestName="presentation-start.json";
    readonly PresentationMethod spec;readonly PresentationStart request;readonly string methodHash;
    readonly Func<NativePresentationReader> factory;readonly Func<IPresentationSink> sinkFactory;
    readonly Action<PresentationReady> publishReady;readonly Action<PresentationClosure> publishClosure;
    readonly Func<(double Cpu,long Peak)> metrics;
    readonly Func<double> seconds;
    readonly Func<Thread,int,bool> join;
    readonly List<string> errors=[];
    readonly Thread thread;readonly ManualResetEventSlim ready=new();readonly Stopwatch watch=Stopwatch.StartNew();
    static readonly List<CalibrationPresentationReference> uncertain=[];
    static readonly List<object> retainedResources=[];
    volatile bool stop;Exception error;PresentationClosure closure;bool disposed;
    void RecordError(Exception value){Interlocked.CompareExchange(ref error,value,null);lock(errors)errors.Add(value.ToString());}
    internal bool IsReady=>ready.IsSet&&error==null&&closure==null;
    string[] ErrorSnapshot(){lock(errors)return errors.ToArray();}
    internal PresentationClosure Closure=>closure==null?null:closure with{Error=Volatile.Read(ref error)?.ToString(),Errors=ErrorSnapshot(),Successful=closure.Successful&&Volatile.Read(ref error)==null};
    internal CalibrationPresentationReference(PresentationMethod spec,string hash,PresentationStart request,Func<NativePresentationReader> factory,
        Func<IPresentationSink> sink,Action<PresentationReady> ready,Action<PresentationClosure> completed,Func<(double Cpu,long Peak)> metrics=null,Func<double> elapsedForChecks=null,Func<Thread,int,bool> joinForChecks=null)
    {
        PresentationMethod.Validate(spec);this.spec=spec;methodHash=hash;this.request=request;this.factory=factory;sinkFactory=sink;publishReady=ready;publishClosure=completed;
        this.metrics=metrics??(()=>{using var p=Process.GetCurrentProcess();return(p.TotalProcessorTime.TotalMilliseconds,p.PeakWorkingSet64);});
        seconds=elapsedForChecks??(()=>watch.Elapsed.TotalSeconds);
        join=joinForChecks??((t,ms)=>t.Join(ms));
        thread=new Thread(Run){IsBackground=true,Name="F10 independent presentation reference"};thread.Start();
    }
    void Run()
    {
        NativePresentationReader reader=null;IPresentationSink sink=null;long readyAt=0;bool nativeClosed=false,writerClosed=false;
        double initialCpu=0;bool initialMetrics=false;
        try
        {
            initialCpu=metrics().Cpu;if(!double.IsFinite(initialCpu)||initialCpu<0)throw new IOException("Initial fixture CPU counter invalid");initialMetrics=true;
            reader=factory();sink=sinkFactory();var timeline=new PresentationTimeline(Stopwatch.Frequency);
            while(!stop)
            {
                if(seconds()>spec.LifetimeSeconds)throw new IOException("Presentation reference lifetime exceeded");
                if(readyAt==0&&seconds()>spec.ReadySeconds)throw new IOException("Presentation reference readiness deadline");
                var row=reader.Read(CancellationToken.None);if(row==null)continue;
                if(sink.Rows>=spec.MaximumRows)throw new IOException("Presentation reference row ceiling");
                sink.Write(row);timeline.Add(row); // Preserve even a rejected observed row before propagating the failure.
                if(readyAt==0&&timeline.Ready)
                {
                    readyAt=Stopwatch.GetTimestamp();var d=reader.Output;
                    publishReady(new(spec.Method,methodHash,request,readyAt,Stopwatch.Frequency,d.Device??spec.DeviceName,spec.Left,spec.Top,spec.Width,spec.Height));ready.Set();
                }
                if(readyAt==0&&seconds()>spec.ReadySeconds)throw new IOException("Presentation reference readiness deadline");
            }
            if(readyAt==0)throw new IOException("Presentation observer stopped without Ready");
        }
        catch(Exception e){RecordError(e);ready.Set();}
        finally
        {
            try {if(sink!=null){sink.FlushDurable();reader?.CommitThrough(sink.Rows);}}
            catch(Exception e){RecordError(e);}
            try {reader?.Dispose();nativeClosed=reader!=null;}
            catch(Exception e){RecordError(e);}
            try {sink?.Dispose();writerClosed=sink?.Closed==true;}
            catch(Exception e){RecordError(e);}
            if(!nativeClosed||!writerClosed)lock(retainedResources)retainedResources.Add(new object[]{reader,sink});
            (double Cpu,long Peak) cost=(0,0);string resourceError=null;try{cost=metrics();if(!double.IsFinite(cost.Cpu)||cost.Cpu<initialCpu||cost.Peak<0)throw new IOException("Invalid fixture resource counters");}catch(Exception e){RecordError(e);resourceError=e.ToString();}
            if(!initialMetrics)resourceError??="Initial fixture process resource counters unavailable";
            bool success=error==null&&reader?.CanSeal==true&&writerClosed&&readyAt>0;
            closure=new(spec.Method,methodHash,request,readyAt,Stopwatch.Frequency,spec.DeviceName,spec.Left,spec.Top,spec.Width,spec.Height,
                reader?.AcquiredRows??0,reader?.ReleasedRows??0,reader?.ProducedRows??0,reader?.CommittedRows??0,sink?.Rows??0,sink?.Bytes??0,sink?.Hash,
                nativeClosed,writerClosed,true,false,success,error?.ToString(),watch.Elapsed.TotalSeconds,resourceError==null?cost.Cpu-initialCpu:null,resourceError==null?cost.Peak:null,resourceError,
                Errors:ErrorSnapshot());
            ready.Set();
        }
    }
    internal void Check()
    {var failure=Volatile.Read(ref error);if(failure!=null)throw new IOException("Independent presentation reference failed",failure);if(watch.Elapsed.TotalSeconds>spec.LifetimeSeconds)throw new IOException("Presentation lifetime bound");}
    internal bool JoinGeneratedThreadForChecks(int milliseconds)=>thread.Join(milliseconds);
    public void Dispose()
    {
        if(disposed){Check();return;}disposed=true;
        stop=true;
        if(!join(thread,spec.JoinMilliseconds))
        {lock(uncertain)uncertain.Add(this);var failure=new IOException("Presentation thread join unknown; all thread-owned native/file resources retained until fixture exit");RecordError(failure);throw failure;}
        ready.Dispose();
        if(closure!=null){closure=Closure with{ThreadJoined=true};try{publishClosure(closure);}catch(Exception e){RecordError(e);}}
        Check();
        if(closure?.Successful!=true)throw new IOException("Presentation reference closure incomplete");
    }
    internal static void WriteJson(string path,object value,Action<string,string> whilePending=null)
    {byte[] b=JsonSerializer.SerializeToUtf8Bytes(value);if(b.Length>16384)throw new IOException("Presentation metadata16KiB");string pending=path+".pending";using(var f=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){int half=b.Length/2;f.Write(b.AsSpan(0,half));whilePending?.Invoke(pending,path);f.Write(b.AsSpan(half));f.Flush(true);}File.Move(pending,path);}
    internal static bool IsVolatile(string path)=>new[]{ReadyName,RequestName,ClosureName}.Any(n=>Path.GetFileName(path).Equals(n,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(path).Equals(n+".pending",StringComparison.OrdinalIgnoreCase));
    internal static byte[] ReadBounded(string path,int maximum)
    {using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(f.Length<=0||f.Length>maximum)throw new IOException("Bounded immutable presentation metadata");byte[] b=new byte[(int)f.Length];f.ReadExactly(b);return b;}
    internal static T ReadJson<T>(string path,int maximum=16384)
    {return JsonSerializer.Deserialize<T>(ReadBounded(path,maximum),new JsonSerializerOptions {
        UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,RespectRequiredConstructorParameters=true });}
    internal static PresentationTimeline ReadClosed(string sourceCell,double epoch,double frequency,string expectedMethodHash,PresentationMethod spec)
    {
        PresentationMethod.Validate(spec);
        if(!double.IsFinite(epoch)||epoch<=0||!double.IsFinite(frequency)||frequency<=0||frequency!=Math.Truncate(frequency)
            ||expectedMethodHash?.Length!=64||!expectedMethodHash.All(Uri.IsHexDigit))throw new IOException("Finite source epoch/frequency and pinned native method required");
        var c=ReadJson<PresentationClosure>(Path.Combine(sourceCell,ClosureName));
        var start=ReadJson<PresentationStart>(Path.Combine(sourceCell,RequestName));var ready=ReadJson<PresentationReady>(Path.Combine(sourceCell,ReadyName));
        var foreground=ReadJson<CalibrationForegroundRelease>(Path.Combine(sourceCell,CalibrationForegroundRelease.FileName));
        using var fixture=JsonDocument.Parse(ReadBounded(Path.Combine(sourceCell,"fixture-ready.json"),16384));
        using var admission=JsonDocument.Parse(ReadBounded(Path.Combine(sourceCell,"foreground-admission.json"),16384));
        using var handoff=JsonDocument.Parse(ReadBounded(Path.Combine(sourceCell,"foreground-handoff-complete.json"),16384));
        using var exited=JsonDocument.Parse(ReadBounded(Path.Combine(sourceCell,"fixture-exit.json"),16384));
        var fixtureRoot=fixture.RootElement;
        ValidateFixtureEvidence(fixtureRoot);
        var actualRelease=handoff.RootElement.GetProperty("release").Deserialize<CalibrationForegroundRelease>();
        if(c.MethodHash!=expectedMethodHash||start.MethodHash!=expectedMethodHash||c.Request!=start||ready.Request!=start||ready.MethodHash!=expectedMethodHash
            ||start.Pid!=foreground.Pid||start.Hwnd!=foreground.Hwnd||start.Label!=foreground.Label||start.Nonce!=foreground.Nonce||start.Label!="H03"||string.IsNullOrWhiteSpace(start.Nonce)
            ||ready.Method!=PresentationMethod.Identity||start.Pid<=0||start.Hwnd<=0||start.RequestedQpc<=0||start.RequestedQpc>ready.ReadyQpc||c.ReadyQpc!=ready.ReadyQpc||ready.Frequency!=frequency||ready.DeviceName!=spec.DeviceName||c.DeviceName!=spec.DeviceName
            ||c.Left!=spec.Left||c.Top!=spec.Top||c.Width!=spec.Width||c.Height!=spec.Height||ready.Left!=spec.Left||ready.Top!=spec.Top||ready.Width!=spec.Width||ready.Height!=spec.Height
            ||fixture.RootElement.GetProperty("pid").GetInt32()!=start.Pid||fixture.RootElement.GetProperty("hwnd").GetInt64()!=start.Hwnd||actualRelease!=foreground
            ||admission.RootElement.GetProperty("fixturePid").GetInt32()!=start.Pid||admission.RootElement.GetProperty("hwnd").GetInt64()!=start.Hwnd
            ||!admission.RootElement.GetProperty("visible").GetBoolean()||!admission.RootElement.GetProperty("foreground").GetBoolean()
            ||exited.RootElement.GetProperty("pid").GetInt32()!=start.Pid||!exited.RootElement.GetProperty("exited").GetBoolean()||exited.RootElement.GetProperty("forced").GetBoolean()||exited.RootElement.GetProperty("exit").GetInt32()!=0)
            throw new IOException("Pinned presentation method/output/foreground/Ready identity changed");
        if(c.Method!=PresentationMethod.Identity||!c.Successful||c.Error!=null||c.Errors is not {Length:0}||c.ResourceError!=null||c.ResourceMeaning!=ResourceMeaning||c.ProcessCpuMilliseconds is not >=0||!double.IsFinite(c.ProcessCpuMilliseconds.Value)||c.ProcessPeakWorkingSetBytes is not >0||!double.IsFinite(c.Seconds)||c.Seconds<0||c.Seconds>700||!c.NativeOwnersClosed||!c.WriterClosed||!c.ThreadBodyCompleted||!c.ThreadJoined||c.Frequency!=frequency||c.ReadyQpc>epoch
            ||c.Acquired!=c.Released||c.Acquired!=c.Produced||c.Produced!=c.Committed||c.Committed!=c.Rows||c.Rows<=2||c.Rows>150000||c.Bytes<=0||c.Bytes>20971520)
            throw new IOException("Actual presentation closure/clock/row completeness required");
        string path=Path.Combine(sourceCell,TraceName);byte[] bytes=ReadBounded(path,20971520);
        if(bytes.Length!=c.Bytes||Convert.ToHexString(SHA256.HashData(bytes))!=c.TraceHash)throw new IOException("Independent presentation trace bytes changed");
        var t=new PresentationTimeline(c.Frequency);var lines=Encoding.UTF8.GetString(bytes).Split('\n',StringSplitOptions.RemoveEmptyEntries);
        if(lines.Length!=c.Rows+1||lines[0]!="ordinal,id,present,before,after,accumulated,coalesced,protected,released")throw new IOException("Exact native trace rowset");
        foreach(var line in lines.Skip(1))
        {
            var x=line.Split(',').Select(s=>long.Parse(s,CultureInfo.InvariantCulture)).ToArray();
            if(x.Length!=9||x[6] is <0 or >1||x[7] is <0 or >1||x[8] is <0 or >1)throw new IOException("Exact native trace shape");
            // Include the row that first proves Ready in its unqualified prefix.
            // Replaying the same bootstrap logic cannot move a later fault before Ready.
            if(!t.Ready?x[4]>c.ReadyQpc:x[3]<c.ReadyQpc)throw new IOException("Computed presentation Ready boundary differs from declared native Ready");
            t.Add(new(x[0],checked((int)x[1]),x[2],x[3],x[4],checked((uint)x[5]),x[6]==1,x[7]==1,x[8]==1));
        }
        t.Complete(true,true,true);return t;
    }
    static void ValidateFixtureEvidence(JsonElement fixture)
    {
        int lifetime=fixture.GetProperty("lifetimeSeconds").GetInt32();
        PresentationMethod.ValidateFixture(fixture.GetProperty("label").GetString(),
            fixture.GetProperty("recipe").GetString()==CalibrationSourceReference.Policy,lifetime);
        if(fixture.GetProperty("seed").GetInt32()!=24680||fixture.GetProperty("barcodeFrameDivisor").GetInt32()!=1
            ||fixture.GetProperty("sourceIdRange").GetInt32()!=CalibrationPattern.ReferenceSourceIdRange
            ||fixture.GetProperty("maximumAnimatedFrame").GetInt32()!=CalibrationPattern.MaximumAnimatedFrame(lifetime))
            throw new IOException("Actual fixture marker monotonic/no-divisor/no-wrap evidence changed");
    }
}
