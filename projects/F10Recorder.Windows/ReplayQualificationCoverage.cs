using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F10Recorder;

// Exact operands of the existing host algorithm, including ordered display
// names/keys and driver versions. A path to a helper is not a host operand.
internal sealed record ReplayHostFacts(string CanonicalJson, string Identity)
{
    internal static ReplayHostFacts Create<T>(T operands)
    {
        string json = JsonSerializer.Serialize(operands);
        return new(json, Digest(json));
    }
    internal static string Digest(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    internal bool Valid()
    {
        if (CanonicalJson == null || Encoding.UTF8.GetByteCount(CanonicalJson) > 32768 || Identity != Digest(CanonicalJson)) return false;
        using var doc = JsonDocument.Parse(CanonicalJson);
        ReplayCalibrationCatalog.CheckUniqueProperties(doc.RootElement);
        var root = doc.RootElement;
        if (!Names(root, "os", "machine", "account", "architecture", "adapters")) return false;
        foreach (string name in new[] { "os", "machine", "account", "architecture" })
            if (!Text(root.GetProperty(name), 256)) return false;
        var adapters = root.GetProperty("adapters");
        if (adapters.ValueKind != JsonValueKind.Array || adapters.GetArrayLength() is < 1 or > 32) return false;
        foreach (var adapter in adapters.EnumerateArray())
        {
            if (!Names(adapter, "Name", "Identity", "Key", "version")) return false;
            foreach (string name in new[] { "Name", "Identity", "Key", "version" })
                if (!Text(adapter.GetProperty(name), 256)) return false;
            if (!adapter.GetProperty("Identity").GetString().StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)
                || !adapter.GetProperty("Key").GetString().StartsWith(@"\Registry\Machine\", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    static bool Text(JsonElement e, int max) => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()) && e.GetString().Length <= max;
    static bool Names(JsonElement e, params string[] names) => e.ValueKind == JsonValueKind.Object
        && e.EnumerateObject().Select(p => p.Name).SequenceEqual(names);
}

// This is the exact scope construction used by CaptureSession. The normalized
// argument array stays intact; duration only changes the existing profile operand.
internal sealed record ReplayQualificationContext(string[] Arguments, int Width, int Height,
    string Endpoint, int SampleRate, int Channels, string FfmpegFormat)
{
    internal string Scope(string exporter, int minutes) => ReplayReferenceModel.Hash(new {
        arguments = Arguments, width = Width, height = Height, endpoint = Endpoint,
        SampleRate, Channels, FfmpegFormat, exporter, ReplayMinutes = minutes });
    internal bool Valid => Arguments is { Length: > 0 and <= 256 } && Arguments.All(a => a != null && a.Length <= 4096)
        && Width == 1920 && Height == 1080 && !string.IsNullOrWhiteSpace(Endpoint) && Endpoint.Length <= 1024
        && SampleRate == 48000 && Channels == 2 && FfmpegFormat == "f32le";
}

// Original bytes are embedded, not reissued under the target identity. Parent
// process-field omissions remain explicitly reviewed historical limitations.
internal sealed record ReplayQualificationParent(string Role, byte[] ReceiptBytes, string Sha256,
    string OriginalModelHash, string MissingProcessIdentityReviewHash);
internal sealed record ReplayApplicabilityReview(string EvidenceHash, string SourceDeltaHash, string Reviewer,
    DateTime AcceptedUtc, bool UnchangedCaptureExportClockAndStorageMechanisms, string[] ParentReceiptHashes);
internal sealed record ReplayFrozenEnvelope(long Residual, long FinalizationExtra, long Poll, long Stop);
internal sealed record ReplayCoverageFreeze(string Revision, ReplayQualificationParent[] Parents,
    string TargetExporter, string TargetRecipe, ReplayHostFacts Host, ReplayQualificationContext Context,
    string Volume, long AllocationUnit, string DomainPolicy, ReplayFrozenEnvelope Envelope,
    ReplayApplicabilityReview Applicability, DateTime FrozenUtc, string FreezeHash);
internal sealed record ReplayCoverageConfirmation(string Role, string FreezeHash, string TargetExporter,
    ReplayHostFacts Host, DateTime StartedUtc, DateTime CompletedUtc, string AdmissionHash, string ObservationHash,
    string ParentReceiptHash, string ParentAcquisitionIdentity, ReplayCalibrationSourceReceipt Packet,
    ReplayCalibrationSourceReceipt Exact, string ConfirmationHash);
internal sealed record ReplayConfirmationAcceptance(string ConfirmationHash, string EvidenceHash, string SourceClockEndpointEvidenceHash,
    bool SourceClockEndpointsVerified, string Reviewer, DateTime AcceptedUtc);
internal sealed record ReplayCoverageReceipt(string Revision, ReplayCoverageFreeze Frozen,
    ReplayCoverageConfirmation CurrentContext, ReplayCoverageConfirmation RetainedMinute,
    ReplayConfirmationAcceptance[] Acceptances, string ReceiptHash);
internal sealed record ReplayCoverageBinding(string ReceiptHash, ReplayCoverageFreeze Frozen, string CatalogDirectory);
internal sealed record ReplayMeasurementIdentity(string Revision, string Recipe, string Exporter);

internal static partial class ReplayCalibrationCatalog
{
    internal const string LegacyReceiptRevision = "reference-owned-completed-history-qualification-v7";
    internal const string DurationReceiptRevision = "reference-duration-owned-completed-history-qualification-v8";
    internal const string CoverageRevision = "reference-coverage-bridge-v9";
    internal const string FreezeRevision = "reference-coverage-freeze-v9";
    internal const string CoverageFileName = "reference-coverage-v9.json";
    internal const int CoverageMaximumBytes = 512 * 1024;
    internal const string DomainPolicy = "1..30|1m:200000000|2..5m:1000000000|6..30m:5000000000|count:60m+5|concat:65536<=5m,1048576>5m|head:output..output+1.1|whole-tier-open-tail|unchanged-scratch-and-guards|no-refit";
    const string LegacyRecipeHash = "03854E6173D5AA153162BF912DDA5B70FEC65EF4F9AD079F5A8588D70BFE33A7";

    internal static void CheckUniqueProperties(JsonElement e) => UniqueProperties(e);
    internal static T ReadEvidence<T>(byte[] bytes)
    {
        Require(bytes is { Length: > 0 and <= CoverageMaximumBytes }, "Coverage evidence size is invalid.");
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        Require(doc.RootElement.ValueKind == JsonValueKind.Object, "Evidence must be a JSON object.");
        UniqueProperties(doc.RootElement);
        var result = JsonSerializer.Deserialize<T>(bytes, CoverageJson);
        Require(result != null, "Evidence is absent.");
        return result;
    }
    static readonly JsonSerializerOptions CoverageJson = new(Json) { MaxDepth = 32 };
    internal static string ByteHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string FreezeHash(ReplayCoverageFreeze f) => ReplayReferenceModel.Hash(f with { FreezeHash = null });
    internal static string ConfirmationHash(ReplayCoverageConfirmation c) => ReplayReferenceModel.Hash(c with { ConfirmationHash = null });
    internal static string CoverageHash(ReplayCoverageReceipt c) => ReplayReferenceModel.Hash(c with { ReceiptHash = null });

    // Hash using the ORIGINAL serialized shape. In particular v7 never gains a
    // ReplayMinutes member or v9 raw operands during historical verification.
    internal static ReplayCalibrationReceipt ReadMeasurement(ReplayQualificationParent parent)
    {
        Require(parent != null && Hash(parent.Sha256) && parent.ReceiptBytes is { Length: > 0 and <= MaximumBytes }
            && ByteHash(parent.ReceiptBytes) == parent.Sha256, "Original parent bytes changed.");
        var receipt = ReadEvidence<ReplayCalibrationReceipt>(parent.ReceiptBytes);
        Require(receipt.Model != null, "Original parent model is absent.");
        var raw = JsonNode.Parse(parent.ReceiptBytes).AsObject();
        raw["ReceiptHash"] = null;
        Require(receipt.ReceiptHash == ReplayReferenceModel.Hash(raw), "Original parent receipt hash changed.");
        bool v7 = receipt.Revision == LegacyReceiptRevision;
        Require(v7 || receipt.Revision == DurationReceiptRevision || receipt.Revision == ReceiptRevision, "Unsupported parent measurement revision.");
        Require(receipt.RecipeHash == (v7 ? LegacyRecipeHash : RecipeHash), "Parent capture/export recipe changed.");
        Require(parent.OriginalModelHash == OriginalModelHash(parent.ReceiptBytes), "Original model bytes changed.");
        if (v7) Require(!JsonNode.Parse(parent.ReceiptBytes)["Model"].AsObject().ContainsKey("ReplayMinutes"), "v7 original schema changed.");
        Validate(receipt, receipt.HostIdentity, new(receipt.Revision, receipt.RecipeHash, receipt.ExporterIdentity));
        bool missing = receipt.Sources.Concat(receipt.LegacyOutputs).Any(s => !IdentifiedProcess(s.Capture) || !IdentifiedProcess(s.Source));
        Require(!missing || Hash(parent.MissingProcessIdentityReviewHash), "Historical process identity omissions need an explicit review; never invent identities.");
        return receipt;
    }
    internal static string OriginalModelHash(byte[] receipt) => ReplayReferenceModel.Hash(JsonNode.Parse(receipt)["Model"]);
    static string MeasurementModelRevision(ReplayReferenceModel m, string recipe, string exporter, string host, bool v7)
        => v7 ? ReplayReferenceModel.Hash(new { recipe, exporter, host, scope = m.Scope, volume = m.Volume, allocationUnit = m.AllocationUnit,
            residual = m.FinalResidual, extra = m.SampledFinalizationExtra, trainingReceipts = m.TrainingReceipts, captureHeadroom = m.CaptureHeadroom })
        : ReplayReferenceModel.Hash(new { recipe, exporter, host, scope = m.Scope, volume = m.Volume, allocationUnit = m.AllocationUnit,
            residual = m.FinalResidual, extra = m.SampledFinalizationExtra, trainingReceipts = m.TrainingReceipts, captureHeadroom = m.CaptureHeadroom,
            replayMinutes = m.ReplayMinutes, inputLimit = ReplayReferenceModel.InputLimit(m.ReplayMinutes),
            inputCountLimit = ReplayReferenceModel.InputCountLimit(m.ReplayMinutes), concatLimit = ReplayReferenceModel.ConcatLimit(m.ReplayMinutes) });

    internal static ReplayFrozenEnvelope TrainingUnion(ReplayCalibrationReceipt[] parents)
    {
        Require(parents is { Length: 2 } && parents.All(p => p?.Model?.CaptureHeadroom != null), "Two original training models with growth evidence are required.");
        // ReadMeasurement/Validate has already recomputed each coefficient from
        // its original first THREE pairs, never from heldouts or confirmations.
        return new(parents.Max(p => p.Model.FinalResidual), parents.Max(p => p.Model.SampledFinalizationExtra),
            parents.Max(p => p.Model.CaptureHeadroom.PollGrowthBytes), parents.Max(p => p.Model.CaptureHeadroom.StopGrowthBytes));
    }
    internal static ReplayCoverageFreeze CreateCoverageFreeze(ReplayQualificationParent[] parents,
        ReplayQualificationContext context, ReplayHostFacts host, ReplayApplicabilityReview review, DateTime frozenUtc)
    {
        var originals = parents.Select(ReadMeasurement).ToArray();
        var first = originals[0].Model;
        var f = new ReplayCoverageFreeze(FreezeRevision, parents, ReplayReferenceModel.ExporterIdentity, RecipeHash, host, context,
            first.Volume, first.AllocationUnit, DomainPolicy, TrainingUnion(originals), review, frozenUtc, null);
        f = f with { FreezeHash = FreezeHash(f) }; ValidateFreeze(f, host.Identity); return f;
    }
    internal static ReplayCalibrationReceipt[] ValidateFreeze(ReplayCoverageFreeze f, string hostIdentity)
    {
        Require(f != null && f.Revision is FreezeRevision or SameCaptureFreezeRevision && Hash(f.FreezeHash) && f.FreezeHash == FreezeHash(f), "Coverage freeze changed.");
        Require(JsonSerializer.SerializeToUtf8Bytes(f).Length <= CoverageMaximumBytes, "Coverage freeze size is invalid.");
        Require(f.TargetExporter == ReplayReferenceModel.ExporterIdentity && f.TargetRecipe == RecipeHash, "Coverage target bytes or recipe changed.");
        Require(f.Host != null && f.Host.Valid() && f.Host.Identity == hostIdentity, "Coverage host identity mismatch (OS/account/device/driver context).");
        Require(f.Context?.Valid == true && f.DomainPolicy == DomainPolicy && f.FrozenUtc.Kind == DateTimeKind.Utc
            && f.FrozenUtc > DateTime.UnixEpoch && f.FrozenUtc <= DateTime.UtcNow, "Coverage context, domain or freeze time is invalid.");
        Require(f.Parents is { Length: 2 } && f.Parents.Select(p => p?.Role).SequenceEqual(new[] { "original-5m-3T3H", "original-30m-3T3H" })
            && f.Parents.Select(p => p.Sha256).Distinct().Count() == 2, "Immutable original parent roles are required.");
        var parents = f.Parents.Select(ReadMeasurement).ToArray();
        Require(parents[0].Model.ReplayMinutes == 5 && parents[1].Model.ReplayMinutes == 30
            && parents.All(p => p.Model.Volume == f.Volume && p.Model.AllocationUnit == f.AllocationUnit
                && p.ExporterIdentity.Split('/').Length == 4
                && p.ExporterIdentity.Split('/')[0] == f.TargetExporter.Split('/')[0]
                && p.ExporterIdentity.Split('/')[3] == ReplaySaveExport.PolicyIdentity), "Parent volume/unit/engine/export policy changed.");
        Require(f.Envelope == TrainingUnion(parents), "Coverage union must use training parents only; no heldout/C00 refit or scaling.");
        var review = f.Applicability;
        Require(review != null && Hash(review.EvidenceHash) && Hash(review.SourceDeltaHash) && !string.IsNullOrWhiteSpace(review.Reviewer)
            && review.Reviewer.Length <= 256 && review.UnchangedCaptureExportClockAndStorageMechanisms
            && review.AcceptedUtc.Kind == DateTimeKind.Utc && review.AcceptedUtc > DateTime.UnixEpoch && review.AcceptedUtc <= f.FrozenUtc
            && review.ParentReceiptHashes != null && review.ParentReceiptHashes.SequenceEqual(f.Parents.Select(p => p.Sha256)),
            "An explicit source-change applicability review must precede the frozen union.");
        foreach (int minutes in Enumerable.Range(1, 30)) _ = ResolveFrozen(f, minutes, null, null).PeakBytes(ReplayReferenceModel.InputLimit(minutes));
        return parents;
    }
    internal static ReplayReferenceModel ResolveFrozen(ReplayCoverageFreeze f, int minutes, string receiptHash, string catalogDirectory)
    {
        Require(ReplayReferenceModel.CoveredMinutes(minutes), "Requested profile is outside covered 1–30 minutes.");
        string scope = f.Context.Scope(f.TargetExporter, minutes);
        var headroom = new ReplayCaptureHeadroom(scope, f.Volume, f.AllocationUnit, f.Envelope.Poll, f.Envelope.Stop, ReplayCapacityPolicy.CoverageMilliseconds);
        string revision = ReplayReferenceModel.Hash(new { f.FreezeHash, receiptHash, minutes, scope, f.Envelope });
        return new(revision, scope, f.Volume, f.AllocationUnit, f.Envelope.Residual, f.Envelope.FinalizationExtra, [], headroom, minutes)
            { Coverage = new(receiptHash, f, catalogDirectory) };
    }
    internal static void ValidateConfirmation(ReplayCoverageConfirmation c, ReplayCoverageFreeze f, ReplayCalibrationReceipt thirty, string role,
        ReplayCoverageConfirmation c00 = null)
    {
        Require(c != null, role + " confirmation is pending.");
        Require(c.Role == role && c.FreezeHash == f.FreezeHash && c.TargetExporter == f.TargetExporter
            && c.Host == f.Host && c.ConfirmationHash == ConfirmationHash(c) && Hash(c.AdmissionHash) && Hash(c.ObservationHash)
            && c.StartedUtc.Kind == DateTimeKind.Utc && c.CompletedUtc.Kind == DateTimeKind.Utc && c.StartedUtc > f.FrozenUtc
            && c.CompletedUtc > c.StartedUtc && c.CompletedUtc <= DateTime.UtcNow, "Confirmation context, freeze, provenance or order changed.");
        bool current = role == "C00-current-5m-pair";
        bool same = role == SameCaptureRole;
        Require(role is "C00-current-5m-pair" or "retained-30m-H03-final-1m-pair" or SameCaptureRole
            && (!same || f.Revision == SameCaptureFreezeRevision && c00?.Role == "C00-current-5m-pair")
            && (f.Revision != SameCaptureFreezeRevision || current || same), "Confirmation role/policy/source changed.");
        var source = thirty.Sources[^1];
        Require(c.Packet != null && c.Exact != null && c.Packet.Label == "H03" && c.Exact.Label == "H03", "A single dynamic H03-shaped paired confirmation is required.");
        var model = ResolveFrozen(f, current ? 5 : 1, null, null);
        if (current)
        {
            Require(c.ParentReceiptHash == null && c.ParentAcquisitionIdentity == null && IdentifiedProcess(c.Packet.Capture)
                && IdentifiedProcess(c.Packet.Source)
                && Fresh(c.Packet.Capture) && Fresh(c.Packet.Source)
                && !f.Parents.Select(ReadMeasurement).SelectMany(p => p.Sources).Any(s =>
                    s.AcquisitionIdentity == c.Packet.AcquisitionIdentity || s.Capture.EvidenceHash == c.Packet.Capture.EvidenceHash
                    || s.Source.EvidenceHash == c.Packet.Source.EvidenceHash), "C00 needs a new current acquisition and fixture after the freeze.");
        }
        else if (same)
        {
            source = c00.Packet;
            Require(c.ParentReceiptHash == c00.ConfirmationHash && c.ParentAcquisitionIdentity == source.AcquisitionIdentity
                && c.Packet.AcquisitionIdentity == source.AcquisitionIdentity && c.Packet.Capture == source.Capture && c.Packet.Source == source.Source
                && c.Packet.Scope == source.Scope && c.Packet.CaptureArgumentsHash == source.CaptureArgumentsHash
                && Math.Abs(c.Packet.OutputEnd - source.OutputEnd) <= .000001
                && Math.Abs(c.Packet.OutputStart - (source.OutputEnd - 60)) <= .000001,
                "v10 retained minute must derive from the SAME genuine C00 final [b-60,b].");
            model = model with { Scope = source.Scope };
        }
        else
        {
            Require(c.ParentReceiptHash == f.Parents[1].Sha256 && c.ParentAcquisitionIdentity == source.AcquisitionIdentity
                && c.Packet.AcquisitionIdentity == source.AcquisitionIdentity && c.Packet.Capture == source.Capture && c.Packet.Source == source.Source
                && c.Packet.Scope == source.Scope && c.Packet.CaptureArgumentsHash == source.CaptureArgumentsHash
                && Math.Abs(c.Packet.OutputEnd - source.OutputEnd) <= 0.000001
                && Math.Abs(c.Packet.OutputStart - (source.OutputEnd - 60)) <= 0.000001,
                "Retained confirmation must use original successful 30m H03's fixed final [b−60,b] selection.");
            model = model with { Scope = source.Scope }; // output provenance stays in the ORIGINAL 30m context
        }
        ValidatePair(c.Packet, c.Exact);
        foreach (var output in new[] { c.Packet, c.Exact })
        {
            ValidateOutput(output, model, Math.Max(output.MaximumActiveGapMs, 1));
            Require(Fresh(output.Decode) && output.ExportProcesses.All(Fresh)
                && (output.RuntimeValidation == null || Fresh(output.RuntimeValidation))
                && (output.ManagedExport == null || output.ManagedExport.ParentStartedUtcTicks > f.FrozenUtc.Ticks
                    && output.ManagedExport.ParentStartedUtcTicks <= c.CompletedUtc.Ticks),
                "Confirmation output creators must be genuine new work after the freeze.");
            Require(Gap(output.MaximumCapacityGapMs) && output.CaptureObservationMilliseconds == ReplayCapacityPolicy.CoverageMilliseconds
                && output.CaptureVolume == f.Volume && output.CapturePollGrowthBytes is >= 0 && output.CaptureStopGrowthBytes is >= 0
                && output.CapturePollGrowthBytes <= f.Envelope.Poll && output.CaptureStopGrowthBytes <= f.Envelope.Stop
                && output.CombinedPeakAllocation.HasValue && model.CoversObservations(output.InputBytes, output.FinalBytes,
                    output.FinalAllocation, output.CombinedPeakAllocation.Value), "Confirmation exceeds the frozen envelope; stop, never refit.");
        }
        var children = new[] { c.Packet, c.Exact }.SelectMany(s => s.ExportProcesses.Append(s.Decode)
            .Concat(s.RuntimeValidation == null ? [] : new[] { s.RuntimeValidation })).ToArray();
        Require(children.Select(p => (p.ProcessId, p.StartedUtcTicks)).Distinct().Count() == children.Length
            && children.Select(p => p.EvidenceHash).Distinct().Count() == children.Length,
            "Paired confirmation outputs need distinct normal/qualification producer and decoder receipts.");
        bool Fresh(ReplayCalibrationProcessReceipt p) => IdentifiedProcess(p)
            && p.StartedUtcTicks >= c.StartedUtc.Ticks && p.StartedUtcTicks <= c.CompletedUtc.Ticks;
    }
    internal static void ValidatePair(ReplayCalibrationSourceReceipt packet, ReplayCalibrationSourceReceipt exact)
    {
        Require(packet != null && exact != null && packet.NormalValidation?.ExportRecipe == ReplaySaveExport.PacketRecipe
            && exact.NormalValidation?.ExportRecipe == ReplayExactExport.Recipe && packet.Label == exact.Label
            && packet.AcquisitionIdentity == exact.AcquisitionIdentity && packet.Capture == exact.Capture && packet.Source == exact.Source
            && packet.SelectionHash == exact.SelectionHash && packet.CaptureArgumentsHash == exact.CaptureArgumentsHash
            && packet.InputBytes == exact.InputBytes && packet.OutputStart == exact.OutputStart && packet.OutputEnd == exact.OutputEnd
            && packet.CapturePollGrowthBytes == exact.CapturePollGrowthBytes && packet.CaptureStopGrowthBytes == exact.CaptureStopGrowthBytes
            && packet.CaptureVolume == exact.CaptureVolume && packet.SampleHash != exact.SampleHash
            && packet.RequestSelectionHash != exact.RequestSelectionHash && packet.Decode?.EvidenceHash != exact.Decode?.EvidenceHash
            && packet.QualificationOutput?.Identity != exact.QualificationOutput?.Identity && packet.NativeExport?.EvidenceHash != exact.NativeExport?.EvidenceHash,
            "Confirmation variants must share protected inputs, with independent output evidence.");
    }
    internal static ReplayCoverageReceipt CreateCoverageReceipt(ReplayCoverageFreeze frozen, ReplayCoverageConfirmation current,
        ReplayCoverageConfirmation retained, ReplayConfirmationAcceptance[] acceptances)
    {
        var r = new ReplayCoverageReceipt(CoverageRevision, frozen, current, retained, acceptances, null);
        r = r with { ReceiptHash = CoverageHash(r) }; ValidateCoverage(r, frozen.Host.Identity); return r;
    }
    internal static void ValidateCoverage(ReplayCoverageReceipt r, string hostIdentity)
    {
        Require(r != null && r.Revision == CoverageRevision && r.Frozen?.Revision == FreezeRevision && Hash(r.ReceiptHash) && r.ReceiptHash == CoverageHash(r), "Coverage receipt schema/hash changed.");
        Require(JsonSerializer.SerializeToUtf8Bytes(r).Length <= CoverageMaximumBytes, "Coverage receipt size is invalid.");
        var parents = ValidateFreeze(r.Frozen, hostIdentity);
        ValidateConfirmation(r.CurrentContext, r.Frozen, parents[1], "C00-current-5m-pair");
        ValidateConfirmation(r.RetainedMinute, r.Frozen, parents[1], "retained-30m-H03-final-1m-pair");
        Require(r.Acceptances is { Length: 2 }, "Both genuine confirmations require explicit independent acceptance.");
        foreach (var c in new[] { r.CurrentContext, r.RetainedMinute })
            Require(r.Acceptances.Count(a => a != null && a.ConfirmationHash == c.ConfirmationHash && Hash(a.EvidenceHash)
                && Hash(a.SourceClockEndpointEvidenceHash) && a.SourceClockEndpointsVerified
                && !string.IsNullOrWhiteSpace(a.Reviewer) && a.Reviewer.Length <= 256 && a.AcceptedUtc.Kind == DateTimeKind.Utc
                && a.AcceptedUtc >= c.CompletedUtc && a.AcceptedUtc <= DateTime.UtcNow) == 1, "Confirmation acceptance is missing or changed.");
    }
    internal static ReplayCalibrationAvailability LoadCoverageFrom(string directory, string hostIdentity = null, int replayMinutes = 5)
    {
        try
        {
            Require(ReplayReferenceModel.CoveredMinutes(replayMinutes), "Requested profile is outside covered 1–30 minutes.");
            using var owner = TrustedStorage.Acquire(directory);
            string path = Path.Combine(directory, CoverageFileName);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            TrustedStorage.ValidateFile(path);
            Require(input.Length is > 0 and <= CoverageMaximumBytes, "Coverage receipt size is invalid.");
            byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
            var receipt = ReadEvidence<ReplayCoverageReceipt>(bytes);
            ValidateCoverage(receipt, hostIdentity ?? HostIdentity);
            return new(ResolveFrozen(receipt.Frozen, replayMinutes, receipt.ReceiptHash, Path.GetFullPath(directory)), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException
            or ArgumentException or InvalidOperationException or OverflowException or NotSupportedException)
        { return new(null, ReplayOutputDemand.Bounded("Coverage qualification rejected: " + e.Message, 160)); }
    }
    static bool CoverageApplicable(ReplayReferenceModel model, string destination, string hostIdentity)
    {
        var binding = model.Coverage;
        if (string.IsNullOrWhiteSpace(binding.CatalogDirectory) || binding.ReceiptHash == null) return false;
        var current = LoadFrom(binding.CatalogDirectory, hostIdentity, model.ReplayMinutes).Model;
        if (current?.Revision != model.Revision) return false;
        var capacity = ReplayStorage.ObserveCapacity(destination);
        if (capacity.Volume != model.Volume || capacity.AllocationUnit != model.AllocationUnit || GetDriveType(capacity.Volume) != 3) return false;
        string existing = Path.GetFullPath(destination);
        while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing) ?? throw new IOException("Destination ancestor unavailable.");
        if ((File.GetAttributes(existing) & (FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.ReparsePoint)) != 0) return false;
        var format = new StringBuilder(32);
        return GetVolumeInformation(capacity.Volume, null, 0, out _, out _, out _, format, (uint)format.Capacity)
            && format.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    }
}
