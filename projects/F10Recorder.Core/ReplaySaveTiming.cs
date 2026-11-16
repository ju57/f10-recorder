namespace F10Recorder;

// Shared diagnostics only. These timestamps never select footage, reserve storage
// or decide success. Null means the request did not reach the corresponding stage.
public sealed class ReplaySaveTiming
{
    public long Frequency;
    public long Requested;
    public long? Accepted, WorkStarted, TailClosed, ProtectionStarted, Protected,
        ExportStarted, ExportCompleted, ValidationStarted, ValidationCompleted,
        ReleaseStarted, Released, PublicationStarted, Published, CompletionReady,
        ValidationLeaseCloseStarted, ValidationLeaseCloseCompleted;
    public long? IndexStarted, IndexCompleted, BoundaryStarted, BoundaryCompleted,
        AssemblyStarted, AssemblyCompleted, ProofStarted, ProofCompleted, MuxStarted, MuxCompleted;
    public string ExportRecipe, ExportConstruction;
    public long? SourceCloseStarted, SourceCloseCompleted;
    public long? OutputObserverCloseStarted, OutputObserverCloseCompleted;
    public long? FinalObservationStarted, FinalObservationCompleted, ManagedReceiptStarted, ManagedReceiptCompleted;
    // Initial admission synchronization belongs to request-to-Saved latency,
    // before Accepted. It is never queue waiting or capture work.
    public long? AdmissionReadinessStarted, AdmissionReadinessCompleted;
    public string SourceCloseError, ManagedHandoffError;
    public bool? SourceHandlesClosed;
    public int HardLinkedInputs, CopiedInputs;
    public int PreparedOwnedInputs, PreparedIndexHits, PreparedIndexMisses;
    public bool IndexAcquisitionComplete, IndexReadersStarted, IndexReadersJoined;
    public int PreparedFallbackInputs;
    public double PreparedFallbackMilliseconds;
    public int AsynchronousInputs;
    public double HardLinkMilliseconds, CopyMilliseconds;
    // Closed, reserved prefix inputs may be protected while the final segment
    // is still closing. These counters are a subset of the totals above; they
    // make that overlap explicit instead of moving work outside Saved timing.
    public int PreTailProtectedInputs;
    public long PreTailProtectedBytes;
    public double PreTailProtectionMilliseconds, PreTailHardLinkMilliseconds, PreTailCopyMilliseconds;
    public long? WriterCheckStarted, WriterCheckCompleted, WorkspaceCheckStarted,
        WorkspaceObservationCompleted, WorkspaceCheckCompleted;
    public double WriterReadMilliseconds, WriterWriteMilliseconds, WriterHashMilliseconds,
        MaximumWriterReadMilliseconds, MaximumWriterWriteMilliseconds, MaximumWriterHashMilliseconds,
        WorkspaceObservationMilliseconds, MaximumWorkspaceObservationMilliseconds;
    public int WorkspaceObservations;
    public ReplayStorageCheckTiming StorageChecks;
    // The current product has no waiting queue. Issue15 must measure its waiting
    // interval separately, rather than relabel task scheduling as queue time.
    public double QueueMilliseconds;
}
