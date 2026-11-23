using System.Security.Cryptography;

namespace F10Recorder;

internal sealed record PrivateFiveMinuteBinding(string Directory, string FileHash);

internal static partial class ReplayCalibrationCatalog
{
    internal const string PrivateFiveMinuteFile = "reference-private-5m-v1.json";
    const string PrivateIssuerResource = "F10Recorder.PrivateFiveMinuteIssuer";

    sealed record SignedReceipt(byte[] Payload, byte[] Signature);
    sealed record VerifiedPrivateReceipt(string Host, PrivateFiveMinuteBinding Binding, ReplayCalibrationAvailability Availability);
    static VerifiedPrivateReceipt lastPrivateReceipt;
    sealed record FiveMinutePayload(string Revision, int Minutes, string ConsumerExporter, string MeasuredExporter,
        string EvidenceSha256, byte[] ParentBytes, byte[] ContractBytes, byte[] PacketBytes, byte[] ExactBytes);
    sealed record FiveMinuteContract(string Revision, string Exporter, ReplayQualificationContext Context, ReplayHostFacts Host,
        string Volume, long AllocationUnit, long PollGrowthBytes, long StopGrowthBytes, long FinalResidualBytes,
        long FinalizationExtraBytes, int ObservationMilliseconds);

    static bool HasSharedCoverage(string directory) => File.Exists(Path.Combine(directory, CoverageFileName))
        || File.Exists(Path.Combine(directory, SameCaptureFileName));

    static FileStream OpenPrivateReceipt(string directory)
    {
        Require(!HasSharedCoverage(directory) && !File.Exists(Path.Combine(directory, FileName)),
            "Private five-minute receipt requires an unambiguous catalog.");
        var path = Path.Combine(directory, PrivateFiveMinuteFile);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            TrustedStorage.ValidateFile(path);
            Require(stream.Length is > 0 and <= CoverageMaximumBytes, "Private receipt size exceeds its bound.");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    static bool PrivateReceiptStillPresent(PrivateFiveMinuteBinding binding)
    {
        using var owner = TrustedStorage.Acquire(binding.Directory);
        using var input = OpenPrivateReceipt(binding.Directory);
        return Convert.ToHexString(SHA256.HashData(input)) == binding.FileHash;
    }

    static string PrivateRevision(string modelRevision, string fileHash) =>
        ReplayReferenceModel.Hash(new { modelRevision, fileHash });

    static ReplayCalibrationAvailability LoadPrivateFiveMinute(string directory, string hostIdentity)
    {
        directory = Path.GetFullPath(directory);
        using var owner = TrustedStorage.Acquire(directory);
        using var input = OpenPrivateReceipt(directory);
        // Capture polls re-read the protected bytes to detect withdrawal. Only a
        // changed receipt needs RSA verification and reconstruction of its model.
        string fileHash = Convert.ToHexString(SHA256.HashData(input));
        var cached = Volatile.Read(ref lastPrivateReceipt);
        if (cached?.Host == hostIdentity && cached.Binding.FileHash == fileHash
            && string.Equals(cached.Binding.Directory, directory, StringComparison.OrdinalIgnoreCase))
            return cached.Availability;
        using var key = typeof(ReplayCalibrationCatalog).Assembly.GetManifestResourceStream(PrivateIssuerResource);
        Require(key != null && key.Length is > 0 and <= 1024, "This build has no private five-minute issuer.");
        input.Position = 0;
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        var signed = ReadEvidence<SignedReceipt>(bytes);
        Require(signed.Payload is { Length: > 0 and <= CoverageMaximumBytes } && signed.Signature is { Length: 384 },
            "Incomplete private receipt.");
        byte[] publicKey = new byte[(int)key.Length];
        key.ReadExactly(publicKey);
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            Require(consumed == publicKey.Length && rsa.KeySize == 3072 &&
                rsa.VerifyData(signed.Payload, signed.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                "Private receipt signature rejected.");
        }
        catch (CryptographicException error) { throw new IOException("Invalid private receipt signature or key.", error); }

        var model = ProjectPrivateFiveMinute(signed.Payload, hostIdentity, directory, fileHash);
        var availability = new ReplayCalibrationAvailability(model, null);
        Volatile.Write(ref lastPrivateReceipt, new(hostIdentity, model.PrivateReceipt, availability));
        return availability;
    }

    // The catalog authenticates the raw payload before projecting its measurements.
    internal static ReplayReferenceModel ProjectPrivateFiveMinute(byte[] payload, string hostIdentity, string directory, string fileHash)
    {
        Require(Hash(fileHash), "Invalid receipt digest.");
        var proof = ReadEvidence<FiveMinutePayload>(payload);
        var consumer = ReplayReferenceModel.ExporterIdentity;
        Require(proof.Revision == "private-reviewed-five-minute-v1" && proof.Minutes == 5
            && proof.ConsumerExporter == consumer && Hash(proof.EvidenceSha256), "Private receipt consumer or scope changed.");
        var contract = ReadEvidence<FiveMinuteContract>(proof.ContractBytes);
        var measured = proof.MeasuredExporter?.Split('/');
        var current = consumer.Split('/');
        Require(contract.Revision == "issue83-C00-five-minute-shipping-delay-v1" && contract.Exporter == proof.MeasuredExporter
            && measured is { Length: 4 } && current.Length == 4 && measured[0] == current[0] && measured[2] == current[2]
            && measured[3] == current[3] && Hash(measured[1]) && contract.Context?.Valid == true
            && contract.Host?.Valid() == true && contract.Host.Identity == hostIdentity, "Measured package or host changed.");

        Require(proof.ParentBytes is { Length: > 0 and <= MaximumBytes }, "Missing original five-minute parent.");
        var parent = ReadMeasurement(new("original-5m-3T3H", proof.ParentBytes, ByteHash(proof.ParentBytes),
            OriginalModelHash(proof.ParentBytes), null));
        var original = parent.Model;
        var growth = original.CaptureHeadroom;
        Require(parent.Revision == ReceiptRevision && parent.HostIdentity == hostIdentity && parent.RecipeHash == RecipeHash
            && original.ReplayMinutes == 5 && original.Volume == contract.Volume && original.AllocationUnit == contract.AllocationUnit
            && growth != null && growth.PollGrowthBytes == contract.PollGrowthBytes && growth.StopGrowthBytes == contract.StopGrowthBytes
            && growth.ObservationMilliseconds == contract.ObservationMilliseconds && original.FinalResidual == contract.FinalResidualBytes
            && original.SampledFinalizationExtra == contract.FinalizationExtraBytes, "Original five-minute storage limits changed.");

        var packet = ReadEvidence<ReplayCalibrationSourceReceipt>(proof.PacketBytes);
        var exact = ReadEvidence<ReplayCalibrationSourceReceipt>(proof.ExactBytes);
        ValidatePair(packet, exact);
        var measuredModel = original with { Scope = contract.Context.Scope(proof.MeasuredExporter, 5) };
        foreach (var output in new[] { packet, exact })
        {
            ValidateOutput(output, measuredModel, output.MaximumActiveGapMs);
            Require(output.Label == "H03" && output.CaptureVolume == contract.Volume && Gap(output.MaximumActiveGapMs)
                && Gap(output.MaximumCapacityGapMs) && output.CaptureObservationMilliseconds == contract.ObservationMilliseconds
                && output.CapturePollGrowthBytes is >= 0 && output.CapturePollGrowthBytes <= contract.PollGrowthBytes
                && output.CaptureStopGrowthBytes is >= 0 && output.CaptureStopGrowthBytes <= contract.StopGrowthBytes
                && output.CombinedPeakAllocation is long peak
                && original.CoversObservations(output.InputBytes, output.FinalBytes, output.FinalAllocation, peak),
                "Paired C00 observations do not fit the predeclared storage limits.");
        }

        var scope = contract.Context.Scope(consumer, 5);
        var headroom = growth with { Scope = scope };
        string revision = ModelRevision(scope, original.Volume, original.AllocationUnit, original.FinalResidual,
            original.SampledFinalizationExtra, original.TrainingReceipts, hostIdentity, headroom, 5);
        return original with { Revision = PrivateRevision(revision, fileHash), Scope = scope, CaptureHeadroom = headroom,
            PrivateReceipt = new(directory, fileHash) };
    }
}
