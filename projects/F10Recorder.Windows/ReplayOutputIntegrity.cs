using System.Diagnostics;
using System.Text.Json.Serialization;

namespace F10Recorder;

// Namespace/producer identity failures retain evidence even for a bounded
// unmeasured experiment without an ordinary numeric output reservation.
internal sealed class ReplayOutputIdentityException(string message, Exception inner = null) : IOException(message, inner);

// Diagnostic scopes only. Reader and lease sub-counters are nested within
// their whole-stage times; do not add both to an inclusive total.
internal sealed class ReplayIntegrityGateTiming
{
    [JsonInclude] public int Attempts, LeaseTransfers, LeaseDisposals;
    [JsonInclude] public double? InitialGuardMilliseconds, AttributesMilliseconds, LeaseMilliseconds,
        InitialExtentMilliseconds, ReaderMilliseconds, ExpectationMilliseconds, FinalExtentMilliseconds, FinalGuardMilliseconds,
        TransferMilliseconds, DisposeMilliseconds, TotalMilliseconds;
    [JsonInclude] public ReplayPublicationLease.OpenTiming LeaseOpen = new();
    internal static T Measure<T>(ref double? counter, Func<T> work)
    {
        long at = Stopwatch.GetTimestamp();
        try { return work(); } finally { counter = (counter ?? 0) + Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
    }
    internal static void Measure(ref double? counter, Action work)
    {
        long at = Stopwatch.GetTimestamp();
        try { work(); } finally { counter = (counter ?? 0) + Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
    }
}

// This is a coded-source integrity check, not a decoder. Its expectation must
// have been frozen from protected inputs before muxing. A recognized mismatch
// fails the request; it must never retry with a weaker gate or a new oracle.
internal static class ReplayOutputIntegrity
{
    internal const string Kind = "SourcePacketIntegrity";

    internal static void RequireReportedOutput(ReplayManagedMuxReceipt producer, ReplayFileExtent inspected)
    {
        var reported = producer?.FinalOutput;
        if (producer is not { Closed: true, InputHashesComplete: true, State: "Completed", Error: null, CloseError: null, ObservationError: null }
            || reported == null || inspected == null || reported.Volume != inspected.Volume || reported.Identity != inspected.Identity
            || reported.Length != inspected.Length || reported.AllocationUnit != inspected.AllocationUnit)
            throw new ReplayOutputIdentityException("Replay checked output differs from the completed producer's reported file identity or extent.");
        // Allocation can trim after close; it is capacity evidence, not identity.
        // This binds the producer's observation to the gate, not an earlier
        // original-writer-handle observation that this receipt does not contain.
    }

    internal static ReplayIntegrityResult Read(ReplayExportRequest request, Action checkStorage, bool retainForPublication = false)
    {
        if (request.Integrity == null || !ReplaySaveExport.IsHybridRecipe(request.Recipe))
            throw new IOException("Replay output lacks a frozen source-byte expectation for its recipe.");
        long started = Stopwatch.GetTimestamp();
        var timing = request.IntegrityGateTiming; timing.Attempts++;
        double deadline = Math.Max(5000, request.Interval.Seconds * 100);
        void Check()
        {
            checkStorage?.Invoke();
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > deadline)
                throw new IOException("Replay packet-integrity validation deadline exceeded.");
        }
        ReplayPublicationLease lease = null; bool transferred = false;
        try
        {
        ReplayIntegrityGateTiming.Measure(ref timing.InitialGuardMilliseconds, Check);
        var attributes = ReplayIntegrityGateTiming.Measure(ref timing.AttributesMilliseconds, () => File.GetAttributes(request.Partial));
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Replay output is not an ordinary closed file.");
        lease = ReplayIntegrityGateTiming.Measure(ref timing.LeaseMilliseconds, () => request.OpenPublication(timing.LeaseOpen));
        if (request.ManagedMux != null) RequireReportedOutput(request.ManagedMux, lease.CheckedIdentity);
        var output = lease.Stream;
        if(request.PacketPipelineSelected)request.PacketRead=lease.OpenPacketReadWork();
        long length = ReplayIntegrityGateTiming.Measure(ref timing.InitialExtentMilliseconds, () => output.Length);
        var index = ReplayIntegrityGateTiming.Measure(ref timing.ReaderMilliseconds, () => ReplayMp4Reader.Read(output, length, request.Interval,
            request.Info.FramesPerSecond, request.Info.RecordAudio, Check, request.IntegrityReadTiming, includeVideoPayloadDigest: false,packetWork:request.PacketRead));
        if(request.PacketRead is {Joined:false})throw new IOException("Independent checked reads have not joined.");
        var result = ReplayIntegrityGateTiming.Measure(ref timing.ExpectationMilliseconds, () => request.Integrity.Verify(index, Check));
        ReplayIntegrityGateTiming.Measure(ref timing.FinalExtentMilliseconds, () =>
        {
            if (output.Length != length) throw new IOException("Replay output extent changed during packet validation.");
        });
        ReplayIntegrityGateTiming.Measure(ref timing.FinalGuardMilliseconds, Check);
        if (retainForPublication)
        {
            ReplayIntegrityGateTiming.Measure(ref timing.TransferMilliseconds, () => request.FreezeValidatedOutput(lease));
            transferred = true; timing.LeaseTransfers++;
        }
        return result;
        }
        finally
        {
            try
            {
                if (!transferred && lease != null)
                { ReplayIntegrityGateTiming.Measure(ref timing.DisposeMilliseconds, lease.Dispose); timing.LeaseDisposals++; }
            }
            finally { timing.TotalMilliseconds = (timing.TotalMilliseconds ?? 0) + Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
        }
    }
}
