using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace F10Recorder;

// Only a qualified, matching model supplies these growth bounds. Missing model
// evidence is represented by null, never by an assumed zero growth allowance.
internal sealed record ReplayCaptureHeadroom(string Scope, string Volume, long AllocationUnit, long PollGrowthBytes, long StopGrowthBytes);
internal sealed record ReplayUnreservedCapacity(string Volume, long AllocationUnit, long Bytes);
internal enum ReplayStorageSafetyKind { Unmeasured, Safe, Warning, Stop }
internal enum ReplayStorageAssessmentSource { Unmeasured, Qualified }
internal sealed class ReplayStorageAssessment
{
    internal ReplayStorageSafetyKind Kind { get; }
    internal bool ShouldWarn { get; }
    internal bool ShouldStop => Kind is ReplayStorageSafetyKind.Stop or ReplayStorageSafetyKind.Unmeasured;
    internal bool SafeToResume { get; }
    internal string Reason { get; }
    internal bool HeadroomKnown => SourceKind == ReplayStorageAssessmentSource.Qualified;
    internal ReplayStorageAssessmentSource SourceKind { get; }
    internal ReplayUnreservedCapacity Capacity { get; }
    internal ReplayCaptureHeadroom Headroom { get; }
    internal ReplayStorageAssessment(ReplayStorageSafetyKind kind, bool warn, bool resume, string reason, ReplayUnreservedCapacity capacity, ReplayCaptureHeadroom headroom, bool qualified)
    {
        Kind = kind; ShouldWarn = warn; SafeToResume = resume; Reason = reason;
        Capacity = capacity; Headroom = headroom;
        SourceKind = qualified ? ReplayStorageAssessmentSource.Qualified : ReplayStorageAssessmentSource.Unmeasured;
    }
}

internal static class ReplayStorageSafety
{
    internal const long WarningBytes = 5_000_000_000;
    internal const long MinimumBytes = 2_000_000_000;
    internal static ReplayStorageAssessment Assess(ReplayUnreservedCapacity capacity, ReplayCaptureHeadroom headroom)
    {
        ReplayStorageAssessment Stop(string reason) => new(ReplayStorageSafetyKind.Stop, false, false, reason, capacity, headroom, false);
        if (capacity == null || !ValidVolume(capacity.Volume) || !ValidUnit(capacity.AllocationUnit) || capacity.Bytes < 0)
            return Stop("Unreserved storage capacity or its volume identity is unavailable or invalid.");
        bool warn = capacity.Bytes < WarningBytes;
        if (headroom == null)
        {
            if (capacity.Bytes <= MinimumBytes)
                return new(ReplayStorageSafetyKind.Stop, warn, false, "Capture headroom is unmeasured and unreserved storage is at or below the necessary two decimal GB floor.", capacity, null, false);
            return new(ReplayStorageSafetyKind.Unmeasured, warn, false, "Capture poll and stop growth have not been qualified for this scope and volume.", capacity, null, false);
        }
        if (!ValidScope(headroom.Scope) || !ValidVolume(headroom.Volume) || !ValidUnit(headroom.AllocationUnit)
            || !string.Equals(capacity.Volume, headroom.Volume, StringComparison.OrdinalIgnoreCase)
            || capacity.AllocationUnit != headroom.AllocationUnit || headroom.PollGrowthBytes < 0 || headroom.StopGrowthBytes < 0)
            return Stop("Capture headroom is invalid or does not match the observed volume and allocation unit.");
        long threshold;
        try { threshold = checked(MinimumBytes + headroom.PollGrowthBytes + headroom.StopGrowthBytes); }
        catch (OverflowException) { return Stop("Qualified capture headroom exceeds the supported byte range."); }
        if (capacity.Bytes <= threshold)
            return new(ReplayStorageSafetyKind.Stop, warn, false, "Unreserved storage has reached the qualified capture stop boundary.", capacity, headroom, true);
        return new(warn ? ReplayStorageSafetyKind.Warning : ReplayStorageSafetyKind.Safe, warn, true,
            warn ? "Unreserved storage is below five decimal GB." : "Unreserved storage exceeds the qualified capture stop boundary.", capacity, headroom, true);
    }
    static bool ValidScope(string scope) => scope != null && scope.Length == 64 && scope.All(Uri.IsHexDigit);
    static bool ValidUnit(long unit) => unit > 0 && unit <= (1L << 32) && (unit & (unit - 1)) == 0;
    static bool ValidVolume(string volume) => volume != null && volume.Length == 49
        && volume.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && volume.EndsWith(@"}\", StringComparison.Ordinal)
        && Guid.TryParseExact(volume.Substring(11, 36), "D", out _);
}

internal sealed record ReplayStorageHold(bool Held, string Reason, bool Durable, string HoldId = null);

// This hold is independent of recording intent/settings and Windows sessions.
// Capture supervision persists Stop before ceasing acquisition, reads it before
// every launch/recovery, and calls ClearAfterSafeResume only for explicit Resume.
internal sealed class ReplayStorageStopLatch
{
    const string Purpose = "replay-storage-stop";
    const string UnconfirmedCaptureReason = "Previous capture storage safety closure is unconfirmed; Resume explicitly.";
    const int MaximumDocumentBytes = 4096;
    readonly string profileDirectory, securityRoot;
    readonly object gate = new();
    ReplayStorageHold observedFailure;
    sealed record HoldDocument(int Version, string ProfileIdentity, string HoldId, string Reason, string StoppedAtUtc, string Mode = null,
        ReplayProducerProof Producer = null, bool ProducerAbsent = false);
    sealed class ReadLease : IDisposable
    {
        internal IDisposable Profile, Storage, Parents;
        internal FileStream Lock;
        internal string BasePath, Identity;
        public void Dispose() { Lock?.Dispose(); Parents?.Dispose(); Storage?.Dispose(); Profile?.Dispose(); }
    }
    internal ReplayStorageStopLatch(string profileDirectory, string securityRoot = null)
    {
        this.profileDirectory = Path.GetFullPath(profileDirectory);
        this.securityRoot = Path.GetFullPath(securityRoot ?? ProfileOwnership.RootDirectory);
    }
    internal ReplayStorageHold Read()
    {
        lock (gate)
        {
            try
            {
                using var lease = AcquireExisting();
                var result = ReadHold(lease.BasePath, lease.Identity, out _);
                if (result.Held) return result;
                return observedFailure ?? result;
            }
            catch (Exception error) when (StorageFailure(error)) { return Fail("Cannot verify the storage stop hold: " + error.Message); }
        }
    }
    internal ReplayStorageHold Stop(string reason)
    {
        lock (gate)
        {
            try
            {
                if (!ValidReason(reason)) throw new IOException("A storage stop needs a bounded, nonempty reason.");
                using var lease = TrustedStorage.AcquireLock(securityRoot, Purpose, profileDirectory, out string bound);
                string identity = Native.DirectoryIdentity(bound), path = BasePath(identity);
                var previous = ReadHold(path, identity, out var document);
                if (previous.Held && (document == null || document.Mode != "guard")) return previous;
                // Converting an owned capture guard to a stop may fail on a full
                // volume. Its existing durable record still prevents restart.
                return Persist(path, identity, document?.HoldId ?? Guid.NewGuid().ToString("N"), reason, "stop", document?.Producer, document?.ProducerAbsent == true);
            }
            catch (Exception error) when (StorageFailure(error)) { return Fail("Could not persist the storage stop hold: " + error.Message); }
        }
    }
    internal ReplayStorageCaptureGuardResult BeginCaptureGuard()
    {
        lock (gate)
        {
            if (observedFailure != null) return new(null, observedFailure);
            try
            {
                using var lease = TrustedStorage.AcquireLock(securityRoot, Purpose, profileDirectory, out string bound);
                string identity = Native.DirectoryIdentity(bound), path = BasePath(identity);
                var previous = ReadHold(path, identity, out _);
                if (previous.Held) return new(null, previous);
                string id = Guid.NewGuid().ToString("N");
                var held = Persist(path, identity, id, UnconfirmedCaptureReason, "guard");
                return new(new ReplayStorageCaptureGuard(this, id), held);
            }
            catch (Exception error) when (StorageFailure(error)) { return new(null, Fail("Could not arm the capture storage guard: " + error.Message)); }
        }
    }
    // The caller must already prove absence of every previous owned producer.
    // Replace the validated stop/abandoned guard directly; never delete the old
    // durable record before the next capture guard has been published.
    internal ReplayStorageCaptureGuardResult BeginCaptureGuardAfterSafeResume(ReplayStorageAssessment assessment)
    {
        lock (gate)
        {
            var checkedAssessment = assessment == null ? null : ReplayStorageSafety.Assess(assessment.Capacity, assessment.Headroom);
            if (checkedAssessment == null || !checkedAssessment.HeadroomKnown || !checkedAssessment.SafeToResume)
                return new(null, new(true, "Explicit Resume requires current, matching qualified capacity above the stop boundary.", false));
            try
            {
                using var lease = AcquireExisting();
                var held = ReadHold(lease.BasePath, lease.Identity, out var document);
                if (document == null)
                    return new(null, held.Held ? held : new(true, "Explicit Resume replacement requires a valid existing durable hold.", false));
                string id = Guid.NewGuid().ToString("N");
                var guarded = Persist(lease.BasePath, lease.Identity, id, UnconfirmedCaptureReason, "guard");
                return new(new ReplayStorageCaptureGuard(this, id), guarded);
            }
            catch (Exception error) when (StorageFailure(error)) { return new(null, Fail("Could not replace the storage hold with a Resume guard: " + error.Message)); }
        }
    }
    internal ReplayStorageHold CompleteCaptureGuard(string holdId)
    {
        lock (gate)
        {
            try
            {
                using var lease = AcquireExisting();
                var held = ReadHold(lease.BasePath, lease.Identity, out var document);
                if (document == null || document.Mode != "guard" || document.HoldId != holdId)
                    return new(true, "The current hold is not this capture's valid owned guard.", held.Durable, held.HoldId);
                DeleteHold(lease.BasePath);
                observedFailure = null;
                return new(false, null, true);
            }
            catch (Exception error) when (StorageFailure(error)) { return Fail("Could not complete the capture storage guard: " + error.Message); }
        }
    }
    internal void RecordProducer(string holdId, ReplayProducerProof producer, bool absent = false)
    {
        lock (gate)
        {
            if (producer != null && !ReplayProducerProof.Valid(producer)) throw new IOException("Capture producer identity is invalid.");
            using var lease = AcquireExisting();
            ReadHold(lease.BasePath, lease.Identity, out var document);
            if (document == null || document.HoldId != holdId) throw new IOException("Capture producer evidence no longer owns the durable hold.");
            Persist(lease.BasePath, lease.Identity, holdId, document.Reason, document.Mode ?? "stop", producer, absent);
        }
    }
    internal bool ProducerAbsenceConfirmed()
    {
        lock (gate)
        {
            try
            {
                using var lease = AcquireExisting();
                ReadHold(lease.BasePath, lease.Identity, out var document);
                return document != null && (document.ProducerAbsent || document.Producer?.ConfirmedAbsent() == true);
            }
            catch (Exception error) when (StorageFailure(error)) { return false; }
        }
    }
    ReplayStorageHold Persist(string path, string identity, string holdId, string reason, string mode,
        ReplayProducerProof producer = null, bool absent = false)
    {
        var document = new HoldDocument(3, identity, holdId, reason, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), mode, producer, absent);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        if (bytes.Length > MaximumDocumentBytes) throw new IOException("The storage stop hold exceeds its byte limit.");
        // A crash or failure before atomic publication leaves this named pending
        // file. Existing guards survive a failed stop update or a full volume.
        using (var file = new FileStream(path + ".pending", FileMode.CreateNew, FileAccess.Write, FileShare.None, MaximumDocumentBytes, FileOptions.WriteThrough))
        {
            TrustedStorage.ValidateFile(path + ".pending");
            file.Write(bytes); file.Flush(true);
        }
        File.Move(path + ".pending", path + ".json", true);
        observedFailure = null;
        return new(true, reason, true, document.HoldId);
    }
    static void DeleteHold(string path)
    {
        File.Delete(path + ".json");
        if (Exists(path + ".json")) throw new IOException("The storage stop hold was not removed.");
    }
    internal ReplayStorageHold ClearAfterSafeResume(ReplayStorageAssessment assessment)
    {
        lock (gate)
        {
            var checkedAssessment = assessment == null ? null : ReplayStorageSafety.Assess(assessment.Capacity, assessment.Headroom);
            if (checkedAssessment == null || !checkedAssessment.HeadroomKnown || !checkedAssessment.SafeToResume)
                return new(true, "Explicit Resume requires current, matching qualified capacity above the stop boundary.", false);
            try
            {
                using var lease = AcquireExisting();
                var held = ReadHold(lease.BasePath, lease.Identity, out var document);
                if (held.Held && document == null) return held;
                if (document != null)
                {
                    // Only the exact, validated named hold is removed. Unknown,
                    // pending and malformed state is retained for explicit repair.
                    File.Delete(lease.BasePath + ".json");
                    if (Exists(lease.BasePath + ".json")) throw new IOException("The storage stop hold was not removed.");
                }
                observedFailure = null;
                return new(false, null, true);
            }
            catch (Exception error) when (StorageFailure(error)) { return Fail("Could not clear the storage stop hold: " + error.Message); }
        }
    }
    ReadLease AcquireExisting()
    {
        var lease = new ReadLease();
        try
        {
            var profile = new TrustedStorage.ProfileDirectoryLease(profileDirectory); lease.Profile = profile;
            lease.Identity = Native.DirectoryIdentity(profile.DirectoryPath);
            if (!Exists(securityRoot)) return lease;
            lease.Storage = TrustedStorage.Acquire(securityRoot);
            string profiles = Path.Combine(securityRoot, "profiles");
            if (!Exists(profiles)) return lease;
            lease.Parents = TrustedStorage.PinDirectories(profiles);
            lease.BasePath = BasePath(lease.Identity);
            string lockPath = lease.BasePath + ".lock";
            if (Exists(lockPath))
            {
                lease.Lock = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
                TrustedStorage.ValidateFile(lockPath);
            }
            else if (Exists(lease.BasePath + ".json") || Exists(lease.BasePath + ".pending"))
                throw new IOException("The storage stop hold has no corresponding protected lock.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    string BasePath(string identity)
    {
        using var account = WindowsIdentity.GetCurrent();
        string key = account.User.Value + ":" + Purpose + ":" + identity;
        return Path.Combine(securityRoot, "profiles", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
    }
    static ReplayStorageHold ReadHold(string path, string identity, out HoldDocument document)
    {
        document = null;
        if (path == null) return new(false, null, true);
        if (Exists(path + ".pending")) return new(true, "A storage stop write is pending or incomplete; the hold requires repair.", true);
        if (!Exists(path + ".json")) return new(false, null, true);
        TrustedStorage.ValidateFile(path + ".json");
        using var file = new FileStream(path + ".json", FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length <= 0 || file.Length > MaximumDocumentBytes) return new(true, "The storage stop hold has an invalid document length.", true);
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = json.RootElement;
        bool versionOne = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Version", out var version) && version.TryGetInt32(out int number) && number == 1;
        bool versionThree = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Version", out var v3) && v3.TryGetInt32(out int n3) && n3 == 3;
        string[] names = versionOne ? ["Version", "ProfileIdentity", "HoldId", "Reason", "StoppedAtUtc"] : versionThree
            ? ["Version", "ProfileIdentity", "HoldId", "Reason", "StoppedAtUtc", "Mode", "Producer", "ProducerAbsent"]
            : ["Version", "ProfileIdentity", "HoldId", "Reason", "StoppedAtUtc", "Mode"];
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != names.Length
            || names.Any(name => root.EnumerateObject().Count(property => property.Name == name) != 1))
            return new(true, "The storage stop hold has an unknown or malformed schema.", true);
        var candidate = JsonSerializer.Deserialize<HoldDocument>(bytes);
        if (candidate == null || (candidate.Version != 1 && candidate.Version != 2 && candidate.Version != 3) || candidate.ProfileIdentity != identity
            || (candidate.Version >= 2 && candidate.Mode != "guard" && candidate.Mode != "stop")
            || candidate.Producer != null && !ReplayProducerProof.Valid(candidate.Producer)
            || !Guid.TryParseExact(candidate.HoldId, "N", out _) || !ValidReason(candidate.Reason)
            || !DateTimeOffset.TryParseExact(candidate.StoppedAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return new(true, "The storage stop hold has invalid or mismatched fields.", true);
        document = candidate;
        return new(true, candidate.Reason, true, candidate.HoldId);
    }
    ReplayStorageHold Fail(string reason) => observedFailure = new(true, reason, false);
    static bool ValidReason(string reason) => !string.IsNullOrWhiteSpace(reason) && reason.Length <= 1024 && !reason.Any(char.IsControl) && Encoding.UTF8.GetByteCount(reason) <= 2048;
    static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
        or System.Security.SecurityException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException;
}

internal sealed record ReplayStorageCaptureGuardResult(ReplayStorageCaptureGuard Guard, ReplayStorageHold Hold);
internal sealed class ReplayStorageCaptureGuard
{
    readonly ReplayStorageStopLatch latch;
    readonly object gate = new();
    bool stopped, completed;
    internal string HoldId { get; }
    internal bool Stopped { get { lock (gate) return stopped; } }
    internal ReplayStorageCaptureGuard(ReplayStorageStopLatch latch, string holdId) { this.latch = latch; HoldId = holdId; }
    internal ReplayStorageHold Stop(string reason)
    {
        lock (gate)
        {
            if (completed) return new(true, "This capture guard has already completed.", false);
            stopped = true; // Set before any I/O; failed persistence cannot permit normal completion.
            return latch.Stop(reason);
        }
    }
    internal ReplayStorageHold CompleteNormally(bool producerAbsent)
    {
        lock (gate)
        {
            if (stopped) return new(true, "A stopped capture guard requires explicit qualified Resume.", false, HoldId);
            if (completed) return latch.Read();
            if (!producerAbsent) return new(true, "Capture producer absence is unconfirmed; the guard is retained.", true, HoldId);
            var result = latch.CompleteCaptureGuard(HoldId);
            if (!result.Held) completed = true;
            return result;
        }
    }
}



