using System.Buffers;
namespace F10Recorder;

internal sealed record ReplayPacketPrepareInput(Stream Stream,ReplayTransportIndex Index,bool Video,bool Audio);
internal readonly record struct ReplayPreparedPacketEvent(ReplayTransportPicture Picture,ReplayTransportAudioFrame Audio,int Offset,int Length);

// Owned coded bodies, not borrowed parser spans. The original owner consumes
// synchronously, then returns this single bounded result exactly once.
internal sealed class ReplayPreparedPackets : IDisposable
{
    byte[] body;
    readonly ReplayPreparedPacketEvent[] events;
    int used,count;
    internal bool SourceHashComplete { get; private set; }
    internal long SourceBytes { get; }
    internal long CapacityBytes => body==null?0:body.Length+(long)events.Length*64+64;
    internal int Count=>count;
    internal ReplayPreparedPackets(ReplayPacketPrepareInput input)
    {
        SourceBytes=input.Index.Bytes;
        int samples=(input.Video?input.Index.Pictures.Count:0)+(input.Audio?input.Index.AudioFrames?.Count??0:0);
        int limit=ContentBound(input.Index);
        try{body=ArrayPool<byte>.Shared.Rent(Math.Max(1,limit));events=new ReplayPreparedPacketEvent[samples];if(CapacityBytes>16777216)throw new IOException("Rented packet result exceeds bounded slot.");}
        catch{if(body!=null){ArrayPool<byte>.Shared.Return(body);body=null;}throw;}
    }
    internal static int ContentBound(ReplayTransportIndex index)=>checked((int)index.Bytes+64*index.Pictures.Count);
    internal static bool Eligible(ReplayTransportIndex index)
    {
        if(index==null||index.Bytes is <188 or >8388608||index.Pictures.Count is <1 or >4096||index.AudioFrames?.Count>4096)return false;
        return ContentBound(index)<=8388608;
    }
    internal void Add(ReplayTransportPicture picture,ReplayTransportAudioFrame audio,ReadOnlySpan<byte> payload)
    {
        if(body==null||SourceHashComplete||count>=events.Length||payload.Length<=0||payload.Length>body.Length-used)throw new IOException("Prepared packet body extent/ownership changed.");
        payload.CopyTo(body.AsSpan(used));events[count++]=new(picture,audio,used,payload.Length);used+=payload.Length;
    }
    internal int AddVideo(ReplayTransportPicture picture,ReadOnlySpan<byte> annex,Action check)
    {
        if(body==null||SourceHashComplete||count>=events.Length||picture==null||annex.IsEmpty)throw new IOException("Prepared video packet extent/ownership changed.");
        int length=ReplayTransportPacketSamples.WriteAvcc(annex,body.AsSpan(used),check);
        events[count++]=new(picture,null,used,length);used=checked(used+length);return length;
    }
    internal void Complete(){if(body==null||count!=events.Length)throw new IOException("Prepared packet sequence is incomplete.");SourceHashComplete=true;}
    internal ReplayPreparedPacketEvent Event(int ordinal)
    {if(body==null||!SourceHashComplete||ordinal<0||ordinal>=count)throw new IOException("Prepared packet result is unavailable.");return events[ordinal];}
    internal ReadOnlySpan<byte> Payload(ReplayPreparedPacketEvent item)
    {if(body==null||!SourceHashComplete||item.Offset<0||item.Length<=0||item.Offset>used-item.Length)throw new IOException("Expired prepared packet body.");return body.AsSpan(item.Offset,item.Length);}
    public void Dispose(){var owned=Interlocked.Exchange(ref body,null);if(owned!=null)ArrayPool<byte>.Shared.Return(owned);}
}

internal sealed class ReplayPacketPrepareWorker : IDisposable
{
    readonly ReplayPacketSampleWorkspace workspace=new();
    internal long PeakWorkspaceBytes { get; private set; }
    internal int PeakIntermediateVideoBytes { get; private set; }
    internal ReplayPacketExtractionTiming Extraction { get; }=new();
    internal ReplayPreparedPackets Prepare(ReplayPacketPrepareInput input,ReplayPacketWork.Context context)
    {
        if(!ReplayPreparedPackets.Eligible(input.Index))throw new IOException("Packet preparation was not prequalified for this bounded source.");
        ReplayPreparedPackets result=null;
        try
        {
            result=new(input);context.ReportOwnedCapacity(result.CapacityBytes);
            using var access=new ReplayPacketCancellableStream(input.Stream,context.Cancellation);
            ReplayTransportPacketSamples.Read(access,input.Index,null,
                input.Audio?(frame,bytes)=>result.Add(null,frame,bytes):null,context.Check,workspace,Extraction,
                input.Video?(picture,annex,throttledCheck)=>Extraction.DirectAvccBytes=checked(Extraction.DirectAvccBytes+result.AddVideo(picture,annex,throttledCheck)):null);
            if(workspace.CapacityBytes>16842752)throw new IOException("Packet worker workspace exceeded its16MiB+64KiB allowance.");
            result.Complete();var returned=result;result=null;return returned;
        }
        finally
        {
            PeakWorkspaceBytes=Math.Max(PeakWorkspaceBytes,workspace.CapacityBytes);
            PeakIntermediateVideoBytes=Math.Max(PeakIntermediateVideoBytes,workspace.CodedCapacityBytes);
            Extraction.IntermediateVideoBufferBytes=Math.Max(Extraction.IntermediateVideoBufferBytes,workspace.CodedCapacityBytes);
            result?.Dispose();
        }
    }
    public void Dispose()=>workspace.Dispose();
}

// A synchronous parser borrows actual cancellable asynchronous reads. This
// adapter never closes its caller's verified stream, and never abandons IO.
internal sealed class ReplayPacketCancellableStream(Stream source,CancellationToken token):Stream
{
    public override bool CanRead=>source.CanRead;public override bool CanSeek=>source.CanSeek;public override bool CanWrite=>false;
    public override long Length=>source.Length;public override long Position{get=>source.Position;set=>source.Position=value;}
    public override int Read(byte[] buffer,int offset,int count){var read=source.ReadAsync(buffer.AsMemory(offset,count),token);return read.IsCompleted?read.GetAwaiter().GetResult():read.AsTask().GetAwaiter().GetResult();}
    public override long Seek(long offset,SeekOrigin origin)=>source.Seek(offset,origin);
    public override void Flush()=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
