using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

internal enum ReplayOutputEvidenceKind { Unmeasured, CalibratedEstimate, SourceBound }
internal sealed record ReplayOutputInput(string Path, double Start, double End, ReplayFileExtent Extent);
internal sealed record ReplayOutputSelection(string Identity, string ExporterIdentity, string Destination,
    double End, bool Closed, IReadOnlyList<ReplayOutputInput> Inputs)
{
    internal static ReplayOutputSelection Create(ReplaySessionInfo info, string exporterIdentity, string destination,
        double end, bool closed, IReadOnlyList<Segment> selection, ReplayFiles files)
    {
        var inputs = Array.AsReadOnly(selection.Select(s => new ReplayOutputInput(s.Path, s.Start, s.End,
            ReplayStorage.Validate(files.Observe(s.Path)))).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { info, exporterIdentity, destination, end, closed, inputs });
        return new(Convert.ToHexString(SHA256.HashData(bytes)), exporterIdentity, destination, end, closed, inputs);
    }
}

// PeakAllocatedBytes is the total coexisting output/finalization allocation on
// the named volume, not remaining growth or another full faststart copy. A
// provider must qualify scratch and filesystem applicability before returning
// numeric evidence. Production stays explicitly unmeasured until calibration.
internal sealed record ReplayOutputDemand(ReplayOutputEvidenceKind Kind, string Revision, string SelectionIdentity,
    string ExporterIdentity, string Volume, long AllocationUnit, long PeakAllocatedBytes, bool CoversOpenTail, string Reason,
    long? SnapshotPeakAllocatedBytes = null)
{
    internal void WriteMetrics(RecorderMetrics metrics)
    {
        metrics.ReplayOutputDemandKind = Kind.ToString();
        metrics.ReplayOutputDemandRevision = Kind == ReplayOutputEvidenceKind.Unmeasured ? null : Bounded(Revision, 128);
        metrics.ReplayOutputDemandReason = Kind == ReplayOutputEvidenceKind.Unmeasured
            ? Bounded(string.IsNullOrWhiteSpace(Reason) ? "Replay output demand is unmeasured." : Reason, 512) : null;
    }
    internal static string Bounded(string value, int maximum) => value == null || value.Length <= maximum ? value : value[..maximum];
    internal static ReplayOutputDemand Unmeasured(string reason) => new(ReplayOutputEvidenceKind.Unmeasured,
        null, null, null, null, 0, 0, false, reason);
}
