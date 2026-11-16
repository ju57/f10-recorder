using System.Runtime.CompilerServices;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
namespace F10Recorder;

internal delegate ValueTask<int> ReplayPacketReadAt(Memory<byte> destination,long offset,CancellationToken token);
internal sealed record ReplayPacketReadSample(int Track,int Ordinal,ReplayMp4Sample Template,bool Video,byte[] Sps,byte[] Pps);
internal sealed record ReplayPacketReadOutcome(ReplayPacketReadSample Source,ReplayMp4Sample Sample,byte[] PictureHeader);
internal sealed record ReplayPacketReadJob(long Start,int Bytes,ReplayPacketReadSample[] Samples);
internal sealed class ReplayPacketReadResult(ReplayPacketReadOutcome[] rows):IDisposable
{
    internal ReplayPacketReadOutcome[] Rows=rows;
    internal long CapacityBytes=checked(rows.LongLength*768+64);
    public void Dispose()=>Rows=null; // Immutable evidence copies transfer to the owner; no pooled body escapes.
}

// Windows can supply this only from the actual immutable checked-read object.
// Core has no pathname/open/publication capability. All offsets remain bounded
// by already parsed sample extents and complete contiguous mdat coverage.
internal sealed class ReplayPacketReadWork(ReplayPacketReadAt readAt)
{
    int active;
    internal bool Joined=>Volatile.Read(ref active)==0;
    internal bool Used {get;private set;}
    internal readonly List<ReplayPacketWorkTiming> Batches=[];
    internal long ReadBytes,PeakReadBufferBytes,PlannedCapacityBytes,PeakAccountedCapacityBytes;
    long liveReadBytes;
    internal double ReadMilliseconds,NalHashMilliseconds,SampleHashMilliseconds;
    internal object Report()=>new{Used,Joined,ReadBytes,PeakReadBufferBytes,PlannedCapacityBytes,PeakAccountedCapacityBytes,ReadMilliseconds,NalHashMilliseconds,SampleHashMilliseconds,Batches};
    internal static bool Eligible(IReadOnlyList<ReplayPacketReadSample> samples)=>samples.Count is >0 and <=32768
        &&samples.All(s=>s.Template.Bytes is >0 and <=1048576);
    sealed class Totals{internal long Bytes,Peak;internal double Read,Nal,Sample;}
    internal void Run(IReadOnlyList<ReplayPacketReadSample> samples,Action<ReplayPacketReadOutcome> accept,Action ownerGuard,bool includeVideoDigest)
    {
        if(!Eligible(samples)||!Joined)throw new IOException("Parallel checked reads were not prequalified or are already active.");
        var ordered=samples.OrderBy(s=>s.Template.Offset).ToArray();var jobs=new List<ReplayPacketReadJob>();int first=0;
        while(first<ordered.Length)
        {
            int end=first+1;long start=ordered[first].Template.Offset,finish=checked(start+ordered[first].Template.Bytes);
            while(end<ordered.Length&&end-first<1024&&ordered[end].Template.Offset==finish
                &&ordered[end].Template.Offset+ordered[end].Template.Bytes-start<=4194304)
            {finish=checked(ordered[end].Template.Offset+ordered[end].Template.Bytes);end++;}
            jobs.Add(new(start,checked((int)(finish-start)),ordered[first..end]));first=end;
        }
        long metadataAllowance=checked(samples.Count*1280L+8388608);
        PlannedCapacityBytes=checked(metadataAllowance+Math.Min(4,jobs.Count)*4194304L);
        if(PlannedCapacityBytes>67108864)throw new IOException("Checked packet work exceeds64MiB total allowance.");
        var totals=Enumerable.Range(0,Math.Min(4,jobs.Count)).Select(_=>new Totals()).ToArray();var timing=new ReplayPacketWorkTiming();var lifetime=new ReplayWorkerLifetime();
        Used=true;Volatile.Write(ref active,1);Batches.Add(timing);
        try
        {
            ReplayPacketWork.Run(jobs,totals.Length,16777216,[MethodImpl(MethodImplOptions.AggressiveOptimization)] (job,context)=>
            {
                byte[] buffer=ArrayPool<byte>.Shared.Rent(job.Bytes);var own=totals[context.Worker];bool charged=false;
                try
                {
                    long meta=checked(job.Samples.LongLength*768+64);
                    if(buffer.Length>4194304)throw new IOException("Checked-read buffer exceeded4MiB.");
                    long total=checked(metadataAllowance+Interlocked.Add(ref liveReadBytes,buffer.Length));charged=true;
                    long peak;do{peak=Volatile.Read(ref PeakAccountedCapacityBytes);if(peak>=total)break;}while(Interlocked.CompareExchange(ref PeakAccountedCapacityBytes,total,peak)!=peak);
                    if(total>67108864)throw new IOException("Actual checked packet buffers exceeded64MiB combined allowance.");
                    context.ReportOwnedCapacity(checked(buffer.Length+meta));own.Peak=Math.Max(own.Peak,buffer.Length);
                    int filled=0;
                    while(filled<job.Bytes)
                    {
                        context.Check();long at=Stopwatch.GetTimestamp();int count;
                        try{var read=readAt(buffer.AsMemory(filled,job.Bytes-filled),checked(job.Start+filled),context.Cancellation);count=read.IsCompleted?read.GetAwaiter().GetResult():read.AsTask().GetAwaiter().GetResult();}
                        finally{own.Read+=Stopwatch.GetElapsedTime(at).TotalMilliseconds;}
                        if(count<=0||count>job.Bytes-filled)throw new IOException("Closed checked read was truncated or exceeded its request.");
                        filled+=count;own.Bytes+=count;
                    }
                    var result=new ReplayPacketReadOutcome[job.Samples.Length];
                    using var packet=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long checkedAt=Stopwatch.GetTimestamp();
                    void Check(bool force=false){if(force||Stopwatch.GetElapsedTime(checkedAt).TotalMilliseconds>=100){context.Check();checkedAt=Stopwatch.GetTimestamp();}}
                    for(int n=0;n<job.Samples.Length;n++)
                    {
                        context.Cancellation.ThrowIfCancellationRequested();var source=job.Samples[n];
                        result[n]=Inspect(source,buffer.AsSpan(checked((int)(source.Template.Offset-job.Start)),source.Template.Bytes),packet,includeVideoDigest,Check,own);
                    }
                    context.Check();return new ReplayPacketReadResult(result);
                }
                finally{ArrayPool<byte>.Shared.Return(buffer);if(charged)Interlocked.Add(ref liveReadBytes,-buffer.Length);}
            },result=>result.CapacityBytes,(result,pump)=>{foreach(var row in result.Rows){pump();accept(row);}},ownerGuard,timing,TimeSpan.FromSeconds(30),lifetime);
        }
        finally
        {
            ReadBytes=totals.Sum(t=>t.Bytes);PeakReadBufferBytes=totals.Sum(t=>t.Peak);ReadMilliseconds=totals.Sum(t=>t.Read);
            NalHashMilliseconds=totals.Sum(t=>t.Nal);SampleHashMilliseconds=totals.Sum(t=>t.Sample);
            if(lifetime.Joined)Volatile.Write(ref active,0);
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static ReplayPacketReadOutcome Inspect(ReplayPacketReadSample source,ReadOnlySpan<byte> bytes,IncrementalHash packetHash,
        bool rawVideo,Action<bool> check,Totals timing)
    {
        var template=source.Template;bool idr=false;string hash,packet;byte[] header=null;
        byte[] Hash(ReadOnlySpan<byte> input,bool nal){long at=Stopwatch.GetTimestamp();var digest=SHA256.HashData(input);double ms=Stopwatch.GetElapsedTime(at).TotalMilliseconds;if(nal)timing.Nal+=ms;else timing.Sample+=ms;return digest;}
        if(source.Video)
        {
            int at=0,pictures=0,nals=0,auds=0;hash=null;
            while(at<bytes.Length)
            {
                check(false);
                if(++nals>64)throw new NotSupportedException("MP4 AVC NAL count requires a different checker.");
                if(bytes.Length-at<5)throw new IOException("Incomplete MP4 AVC length/header.");
                int size=checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]));at+=4;
                if(size<=0||size>bytes.Length-at)throw new IOException("MP4 AVC NAL extent.");
                var nal=bytes.Slice(at,size);at+=size;int type=nal[0]&31;
                if((nal[0]&128)!=0||type is not(1 or 5 or 6 or 7 or 8 or 9 or 12))throw new IOException("Unsupported MP4 AVC NAL.");
                if(type is 6 or 9 or 12&&(nal[0]&96)!=0)throw new IOException("MP4 AVC metadata has invalid reference flags.");
                if(pictures!=0&&type is 6 or 7 or 8 or 9)throw new IOException("MP4 AVC metadata follows its coded picture.");
                if(type==9&&(++auds!=1||nals!=1||nal.Length!=2||(nal[1]&31)!=16))throw new IOException("Invalid MP4 AVC access-unit delimiter.");
                if(type is 1 or 5){if(++pictures!=1)throw new IOException("MP4 sample contains more than one picture.");header=nal[..Math.Min(64,nal.Length)].ToArray();idr=type==5;}
                else if(type is 7 or 8&&!nal.SequenceEqual(type==7?source.Sps:source.Pps))throw new IOException("In-band MP4 AVC configuration changed.");
                int trimmed=0;while(!nal.IsEmpty&&nal[^1]==0){nal=nal[..^1];if((++trimmed&65535)==0)check(true);}
                byte[] digest=Hash(nal,true);ReplayCodedPacketHash.Append(packetHash,nal.Length,digest);if(type is 1 or 5)hash=Convert.ToHexString(digest);
            }
            packet=Convert.ToHexString(packetHash.GetHashAndReset());
            if(pictures!=1||idr!=template.Idr)throw new IOException("MP4 sync flags disagree with physical AVC pictures.");
        }
        else{hash=Convert.ToHexString(Hash(bytes,false));packet=hash;}
        var sample=template with{Idr=idr,CodedSha256=hash,PayloadSha256=source.Video?rawVideo?Convert.ToHexString(Hash(bytes,false)):null:hash,PacketSha256=packet};
        return new(source,sample,header);
    }
}
