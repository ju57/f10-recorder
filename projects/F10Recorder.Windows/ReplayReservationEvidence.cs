namespace F10Recorder;

// Borrowed only during one synchronous Update. Never an inter-callback cache.
internal sealed class ReplayReservationEvidence(string owner,Func<ReplayVolumeClaim,string,string,ReplayReservationFact> observe):IDisposable
{
    internal readonly string Owner=owner;
    readonly int thread=Environment.CurrentManagedThreadId;
    bool active=true;
    internal ReplayReservationFact Observe(ReplayVolumeClaim claim,string anchor,string anchorIdentity)
    {
        if(!active||Environment.CurrentManagedThreadId!=thread)throw new IOException("Reservation evidence escaped its callback lifetime/thread.");
        return observe(claim,anchor,anchorIdentity);
    }
    public void Dispose()=>active=false;
}
internal sealed record ReplayReservationFact(ReplayFileExtent Extent,string Anchor,string AnchorIdentity);
