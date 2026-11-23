using System.Text.Json;

namespace F10Recorder;

// A separate policy: correlated confirmations from ONE acquisition. Original
// v7/v8/v9 evidence and its serialization/validation are deliberately unchanged.
internal sealed record ReplayCompletedInputDigest(string Path, double Start, double End,
    ReplayFileExtent Extent, string Sha256);
internal sealed record ReplaySameCaptureManifest(string Revision, string C00ConfirmationHash,
    string AcquisitionIdentity, string SelectionHash, double Start, double End,
    string SourceClockHash, string SourceReferenceHash, ReplayCompletedInputDigest[] Inputs, string ManifestHash);
internal sealed record ReplaySameCaptureOutputCheck(string SampleHash, string DecodeEvidenceHash,
    string SourceEvidenceHash, long Pictures, long ComparedPictures, int AudioMarkers,
    double MaximumBoundaryErrorSeconds, double MaximumAvErrorSeconds, double ReferenceUncertaintySeconds);
internal sealed record ReplaySameCaptureAcceptance(string ConfirmationHash, string ManifestHash,
    string OraclePolicy, string OracleEvidenceHash, ReplaySameCaptureOutputCheck[] Outputs,
    string Reviewer, DateTime AcceptedUtc);
internal sealed record ReplaySameCaptureReceipt(string Revision, ReplayCoverageFreeze Frozen,
    ReplayCoverageConfirmation C00, ReplaySameCaptureManifest Manifest, ReplayCoverageConfirmation Retained,
    ReplaySameCaptureAcceptance[] Acceptances, string ReceiptHash);

internal static partial class ReplayCalibrationCatalog
{
    internal const string SameCaptureRevision = "reference-same-C00-coverage-v10";
    internal const string SameCaptureFreezeRevision = "reference-same-C00-freeze-v10";
    internal const string SameCaptureRole = "retained-same-C00-final-1m-pair";
    internal const string SameCaptureFileName = "reference-same-C00-v10.json";
    internal const string SameCaptureOracle = "unique-render-clock-compositor-source-reference-v1";
    internal static string SameManifestHash(ReplaySameCaptureManifest m) => ReplayReferenceModel.Hash(m with { ManifestHash = null });
    internal static string SameReceiptHash(ReplaySameCaptureReceipt r) => ReplayReferenceModel.Hash(r with { ReceiptHash = null });

    internal static void ValidateSameManifest(ReplaySameCaptureManifest m, ReplayCoverageConfirmation c00)
    {
        Require(c00 != null && m != null && m.Revision == SameCaptureRevision && Hash(m.ManifestHash)
            && m.ManifestHash == SameManifestHash(m) && m.C00ConfirmationHash == c00.ConfirmationHash
            && m.AcquisitionIdentity == c00.Packet.AcquisitionIdentity && m.SelectionHash == c00.Packet.SelectionHash
            && m.Start == c00.Packet.OutputStart && m.End == c00.Packet.OutputEnd
            && Hash(m.SourceClockHash) && Hash(m.SourceReferenceHash), "Genuine C00 manifest/clock/reference binding is missing or changed.");
        ValidateFiveMinuteInputs(m.Inputs, c00.Packet);
        _ = SameCaptureMinute(m);
    }

    internal static void ValidateFiveMinuteInputs(ReplayCompletedInputDigest[] inputs, ReplayCalibrationSourceReceipt packet)
    {
        Require(inputs is { Length: > 0 and <= 305 } && inputs.Length == packet.SegmentCount
            && inputs.All(i => i?.Extent != null && Hash(i.Sha256) && Path.IsPathFullyQualified(i.Path)
                && double.IsFinite(i.Start) && double.IsFinite(i.End) && i.End > i.Start && i.Extent.Length > 0)
            && inputs.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == inputs.Length
            && inputs.Select(i => i.Extent.Volume + "/" + i.Extent.Identity).Distinct().Count() == inputs.Length
            && inputs.Sum(i => i.Extent.Length) == packet.InputBytes, "Complete protected C00 input digests/identities are required.");
        for (int n = 1; n < inputs.Length; n++)
            Require(Math.Abs(inputs[n - 1].End - inputs[n].Start) <= .000001, "C00 complete input continuity changed.");
        string selection = ReplayReferenceModel.Hash(new {
            scope = packet.Scope, Interval = new ReplayInterval(packet.OutputStart, packet.OutputEnd, 300), FramesPerSecond = 60,
            Codec = "h264_nvenc", RecordAudio = true,
            inputs = inputs.Select(i => new { i.Start, i.End, i.Extent.Volume, i.Extent.Identity, i.Extent.Length, i.Extent.AllocationUnit }).ToArray()
        });
        Require(selection == packet.SelectionHash, "C00 manifest ordered native objects do not match its actual acquisition selection.");
    }
    internal static ReplayCompletedInputDigest[] SameCaptureMinute(ReplaySameCaptureManifest m)
    {
        Require(m != null && double.IsFinite(m.End) && m.End - m.Start >= 300 - .000001 && m.Inputs != null,
            "The final minute requires a complete accepted five-minute C00.");
        // Same existing head-inclusive selector/preroll, with no choice of window.
        var segments = m.Inputs.Select(i => new Segment { Path = i.Path, Start = i.Start, End = i.End }).ToList();
        var info = new ReplaySessionInfo("metadata", "metadata", 60, 20, 60, "h264_nvenc", ".ts", true, true);
        var chosen = ReplaySelection.Select(segments, m.End, 60 + ReplaySaveExport.Preroll(info));
        var result = chosen.Select(s => m.Inputs.Single(i => i.Path == s.Path)).ToArray();
        Require(result.Length is > 0 and <= 65 && result.Sum(i => i.Extent.Length) <= 200_000_000
            && result[0].Start <= m.End - 60 && result[^1].End >= m.End
            && m.End - result[0].Start <= 61.1, "Fixed C00 final minute lacks complete head/preroll/tail or exceeds its tier.");
        return result;
    }
    internal static void ValidateSameAcceptance(ReplaySameCaptureAcceptance a, ReplayCoverageConfirmation c,
        ReplaySameCaptureManifest m)
    {
        Require(a != null && c != null && m != null && a.ConfirmationHash == c.ConfirmationHash
            && a.ManifestHash == m.ManifestHash && a.OraclePolicy == SameCaptureOracle && Hash(a.OracleEvidenceHash)
            && !string.IsNullOrWhiteSpace(a.Reviewer) && a.Reviewer.Length <= 256 && a.AcceptedUtc.Kind == DateTimeKind.Utc
            && a.AcceptedUtc >= c.CompletedUtc && a.AcceptedUtc <= DateTime.UtcNow && a.Outputs is { Length: 2 },
            "Concrete independent source/reference acceptance is missing.");
        Require(a.Outputs.Select(o=>o.DecodeEvidenceHash).Distinct().Count()==2
            && a.Outputs.Select(o=>o.SourceEvidenceHash).Distinct().Count()==1,
            "Distinct fresh output oracle decoders must share the same independent C00 source evidence.");
        foreach (var output in new[] { c.Packet, c.Exact })
        {
            var checks = a.Outputs.Where(o => o?.SampleHash == output.SampleHash).ToArray();
            Require(checks.Length == 1, "Every distinct output needs its own oracle result.");
            var x = checks[0];
            Require(Hash(x.DecodeEvidenceHash) && Hash(x.SourceEvidenceHash) && x.Pictures > 0
                && x.Pictures == x.ComparedPictures && x.Pictures == (long)Math.Round(output.ExportSeconds * 60)
                && x.AudioMarkers >= 20 && double.IsFinite(x.MaximumBoundaryErrorSeconds)
                && x.MaximumBoundaryErrorSeconds is >= 0 and <= 1d / 60 + .000001
                && double.IsFinite(x.MaximumAvErrorSeconds) && x.MaximumAvErrorSeconds >= 0
                && double.IsFinite(x.ReferenceUncertaintySeconds) && x.ReferenceUncertaintySeconds is > 0 and < .05
                && x.MaximumAvErrorSeconds + x.ReferenceUncertaintySeconds <= .05,
                "One-frame/50ms gates, unique audio markers, complete picture comparison or uncertainty failed.");
        }
    }
    internal static void ValidateSameCapture(ReplaySameCaptureReceipt r, string hostIdentity)
    {
        Require(r != null && r.Revision == SameCaptureRevision && r.ReceiptHash == SameReceiptHash(r)
            && r.Frozen?.Revision == SameCaptureFreezeRevision, "Separate v10 same-acquisition policy/hash required.");
        var parents = ValidateFreeze(r.Frozen, hostIdentity);
        ValidateConfirmation(r.C00, r.Frozen, parents[1], "C00-current-5m-pair");
        ValidateSameManifest(r.Manifest, r.C00);
        ValidateConfirmation(r.Retained, r.Frozen, parents[1], SameCaptureRole, r.C00);
        var minute=SameCaptureMinute(r.Manifest);
        Require(r.Retained.Packet.InputBytes==minute.Sum(i=>i.Extent.Length)&&r.Retained.Packet.SegmentCount==minute.Length,
            "Retained source count/bytes differ from the actual immutable C00 final minute.");
        Require(r.Retained.StartedUtc > r.C00.CompletedUtc && r.Acceptances is { Length: 2 }, "C00 must complete and pass acceptance before retained work.");
        foreach (var c in new[] { r.C00, r.Retained })
        {
            var a = r.Acceptances.Where(a => a?.ConfirmationHash == c.ConfirmationHash).ToArray();
            Require(a.Length == 1, "Both same-acquisition confirmations need distinct acceptance.");
            ValidateSameAcceptance(a[0], c, r.Manifest);
            if (c == r.C00) Require(a[0].AcceptedUtc < r.Retained.StartedUtc, "C00 acceptance must precede retained execution.");
        }
    }
    internal static ReplayCalibrationAvailability LoadSameCaptureFrom(string directory, string hostIdentity, int minutes)
    {
        try
        {
            using var owner = TrustedStorage.Acquire(directory);
            Require(ReplayReferenceModel.CoveredMinutes(minutes) && !File.Exists(Path.Combine(directory, CoverageFileName)),
                "Conflicting v9/v10 qualification policy or invalid profile.");
            string path = Path.Combine(directory, SameCaptureFileName);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            TrustedStorage.ValidateFile(path);
            Require(input.Length is > 0 and <= CoverageMaximumBytes, "v10 receipt512KiB bound.");
            byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
            var receipt = ReadEvidence<ReplaySameCaptureReceipt>(bytes);
            ValidateSameCapture(receipt, hostIdentity ?? HostIdentity);
            return new(ResolveFrozen(receipt.Frozen, minutes, receipt.ReceiptHash, Path.GetFullPath(directory)), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException
            or ArgumentException or InvalidOperationException or OverflowException or NotSupportedException)
        { return new(null, ReplayOutputDemand.Bounded("Same-C00 qualification rejected: " + e.Message, 160)); }
    }
}
