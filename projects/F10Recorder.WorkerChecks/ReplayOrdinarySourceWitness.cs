using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace F10Recorder;

// Qualification witness only, outside the recorder's cache/work namespaces.
// No source read handles survive a call. Links are made before client-send;
// ordinary pruning/session disposal may unlink their own names normally.
internal sealed class ReplayOrdinarySourceWitness
{
    internal sealed record Alias(string Original,string Path,long CreatedQpc,ReplayFileExtent InitialExtent);
    sealed record Request(string Label,long ClientRequestQpc,ReplayOutputSelection Selection);
    readonly string root;
    readonly int maximum;
    readonly Dictionary<string,Alias> aliases=new(StringComparer.OrdinalIgnoreCase);
    readonly List<Request> requests=[];
    readonly List<(int First,int Last)> skipped=[];
    string source;
    int cursor;
    bool initialized;
    long calls,created;
    double milliseconds;
    internal IReadOnlyList<Alias> Aliases=>aliases.Values.ToArray();
    internal ReplayOrdinarySourceWitness(string root,int maximum)
    {
        this.root=Path.GetFullPath(root);this.maximum=maximum;
        if(maximum is <1 or >4096 || Directory.Exists(root) || File.Exists(root))throw new IOException("Fresh bounded source witness root required.");
        Directory.CreateDirectory(root);
    }
    static string Key(ReplayFileExtent x)=>x.Volume+"/"+x.Identity;
    static int Number(string path)
    {
        string leaf=Path.GetFileName(path);
        return leaf.Length==18 && leaf.StartsWith("chunk-",StringComparison.Ordinal) && leaf.EndsWith(".ts",StringComparison.Ordinal)
            && int.TryParse(leaf.AsSpan(6,9),NumberStyles.None,CultureInfo.InvariantCulture,out int n)?n:-1;
    }
    // Call only during warmup/target wait and immediately BEFORE request CPU/QPC.
    internal void Mirror(string directory,bool allowGap=false,Action check=null)
    {
        long started=Stopwatch.GetTimestamp();calls++;
        try
        {
            string actual=Path.GetFullPath(directory);
            if(source!=null && source!=actual)throw new IOException("Source witness changed capture session.");
            source=actual;
            if(!initialized)
            {
                var existing=Directory.GetFiles(source,"chunk-*.ts").Select(Number).Where(n=>n>=0).ToArray();
                cursor=existing.Length==0?0:existing.Min();initialized=true;
            }
            while(true)
            {
                if((created&31)==0)check?.Invoke();
                string original=Path.Combine(source,$"chunk-{cursor:D9}.ts");
                if(!File.Exists(original))
                {
                    if(!allowGap)break;
                    var existing=Directory.GetFiles(source,"chunk-*.ts").Select(Number).Where(n=>n>cursor).ToArray();
                    if(existing.Length==0)break;
                    int next=existing.Min();skipped.Add((cursor,next-1));cursor=next;continue;
                }
                if(aliases.Count>=maximum)throw new IOException("Pre-request source witness count exceeded its admitted limit.");
                string copy=Path.Combine(root,Path.GetFileName(original));
                if(File.Exists(copy) || !Native.CreateHardLink(copy,original,IntPtr.Zero))
                    throw new IOException("Cannot create pre-request source witness link.",new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                long at=Stopwatch.GetTimestamp();
                var extent=ReplayStorage.Observe(copy);var current=ReplayStorage.Observe(original);
                if(extent.Volume!=current.Volume || extent.Identity!=current.Identity || extent.AllocationUnit!=current.AllocationUnit)
                    throw new IOException("Source witness link changed the original native object.");
                if(!aliases.TryAdd(Key(extent),new(original,copy,at,extent)))throw new IOException("Source witness repeated a native object.");
                created++;cursor++;
            }
        }
        finally{milliseconds+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;}
    }
    internal void Record(string label,long clientRequestQpc,ReplayOutputSelection selection)
    {
        if(selection is not{Closed:true} || requests.Count>=2 || selection.Inputs.Count is <1 or >1805)
            throw new IOException("Source witness needs the actual completed ordinary selection.");
        foreach(var input in selection.Inputs)
            if(!aliases.TryGetValue(Key(input.Extent),out var alias) || alias.CreatedQpc>=clientRequestQpc)
                throw new IOException("An actual selected source has no pre-request alias; never backfill an audit witness.");
        requests.Add(new(label,clientRequestQpc,selection));
    }
    internal object VerifyClosed(int minutes)
    {
        if(requests.Count!=2)throw new IOException("Two completed source witness requests required.");
        var final=aliases.Values.Select(a=>new{alias=a,extent=ReplayStorage.Observe(a.Path)}).ToArray();
        long bytes=0;
        foreach(var x in final)
        {
            if(x.extent.Volume!=x.alias.InitialExtent.Volume || x.extent.Identity!=x.alias.InitialExtent.Identity
                || x.extent.AllocationUnit!=x.alias.InitialExtent.AllocationUnit)throw new IOException("Source audit native identity changed.");
            bytes=checked(bytes+x.extent.Length);
        }
        long unit=final.Length==0?4096:final[0].extent.AllocationUnit;
        if(bytes>checked(2*ReplayReferenceModel.SnapshotEnvelope(unit,minutes)))throw new IOException("Source witness retained bytes exceed its two-snapshot allowance.");
        foreach(var request in requests)
            foreach(var input in request.Selection.Inputs)
            {
                var actual=final.Single(x=>Key(x.extent)==Key(input.Extent));
                if(actual.extent.Length!=input.Extent.Length || actual.extent.AllocationUnit!=input.Extent.AllocationUnit)
                    throw new IOException("Closed source witness EOF/unit differs from the actual protected selection.");
            }
        return new{kind="ordinary-pre-request-source-witness-v1",source,root,calls,created,milliseconds,maximum,bytes,
            qpcFrequency=Stopwatch.Frequency,aliases=final,requests,skipped=skipped.Select(x=>new{x.First,x.Last}),
            claim="Native identity/EOF coverage for later independent source decoding only. No decoded-content, continuous publication ownership or Saved assertion. No source links created inside either measured save."};
    }
}
