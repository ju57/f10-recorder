using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal enum ReplayVolumeClaimKind { FilePeak, UnmaterializedGrowth }

internal sealed record ReplayVolumeClaim(string Path, string Directory, string Volume, long AllocationUnit, long PeakAllocatedBytes,
    ReplayVolumeClaimKind Kind = ReplayVolumeClaimKind.FilePeak);

internal sealed class ReplayVolumeReservationBusyException(string message, Exception inner) : IOException(message, inner);

// Account-wide promises concern future growth only. Already allocated bytes are
// reflected in the fresh volume free-space sample. No PID or age expires a promise:
// sealed owners are recovered only after their exact worker and producer job
// have both exited. Legacy and interrupted-launch promises remain conservative.
internal sealed class ReplayVolumeReservations
{
    const int MaximumBytes = 8 * 1024 * 1024, MaximumOwners = 128, MaximumClaims = 8192;
    const string LockPurpose = "replay-volume-reservations";
    internal const int AcceptedWorkLockWaitMilliseconds = 200;
    readonly string root, ledgerPath;
    readonly long minimumUnreservedBytes;
    readonly ReplayReservationObservations observations;
    TrustedStorage.LockDirectoryLease requestDirectory;
    internal Action BeforeReleaseForChecks,AfterUpdatePersistForChecks,BeforeRequestCloseForChecks;
    internal Action BeforeSetProducerForChecks;
    internal Action BeforeReleasePersistForChecks;
    internal IDisposable BeginRequestSession()
    {
        if(requestDirectory!=null)throw new IOException("An authority request session is already owned.");
        requestDirectory=new(root,LockPurpose);return new RequestSession(this,requestDirectory);
    }
    sealed class RequestSession(ReplayVolumeReservations owner,TrustedStorage.LockDirectoryLease directory):IDisposable
    {
        bool closed;
        public void Dispose(){if(closed)return;owner.BeforeRequestCloseForChecks?.Invoke();directory.Dispose();if(owner.requestDirectory!=directory)throw new IOException("Authority request session changed owner.");owner.requestDirectory=null;closed=true;}
    }
    static readonly JsonSerializerOptions Json = new() { MaxDepth = 16 };

    sealed record HeldClaim(ReplayVolumeClaim Claim, string Identity, string Anchor, string AnchorIdentity);
    sealed record Holder(string Owner, List<HeldClaim> Claims, ReplayProducerProof Producer = null);
    sealed record Ledger(int Version, List<Holder> Owners, string Revision = null);
    sealed record Observation(HeldClaim Held, long Allocated);

    // Claims in one snapshot share directory ancestry. Keep one set of native
    // directory pins per observed path for this locked update, rather than one
    // set per segment. Identity stays stable while those pins deny replacement.
    sealed class DirectoryObservations : IDisposable
    {
        readonly Dictionary<string, (string Identity, IDisposable Pins)> directories = new(StringComparer.OrdinalIgnoreCase);
        internal string Pin(string path)
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (directories.TryGetValue(path, out var prior)) return prior.Identity;
            var pins = TrustedStorage.PinDirectories(path);
            try
            {
                string identity = Native.DirectoryIdentity(path);
                directories.Add(path, (identity, pins));
                return identity;
            }
            catch { pins.Dispose(); throw; }
        }
        public void Dispose()
        {
            foreach (var directory in directories.Values.Reverse()) directory.Pins.Dispose();
            directories.Clear();
        }
    }
    internal ReplayVolumeReservations(string storageRoot, long minimumUnreservedBytes = 0)
        : this(storageRoot, minimumUnreservedBytes, new ReplayReservationObservations()) { }
    ReplayVolumeReservations(string storageRoot, long minimumUnreservedBytes,
        ReplayReservationObservations observations)
    {
        if (minimumUnreservedBytes < 0) throw new ArgumentOutOfRangeException(nameof(minimumUnreservedBytes));
        this.minimumUnreservedBytes = minimumUnreservedBytes;
        this.observations = observations;
        root = Path.GetFullPath(storageRoot);
        ledgerPath = Path.Combine(root, "replay-volume-reservations.json");
    }
    internal ReplayVolumeReservations CreatePeer() => new(root, minimumUnreservedBytes, observations);
    internal ReplayReservationObservations.Registration RegisterObservation(string owner,
        Func<ReplayReservationObservations.Registration, ReplayVolumeClaim, string, string, ReplayReservationFact> observe)
        => observations.Register(owner, observe);

    IDisposable Acquire(bool acceptedWork = false)
    {
        var wait = Stopwatch.StartNew();
        while (true)
        {
            try { return requestDirectory?.AcquireOperation()??TrustedStorage.AcquireLock(root, LockPurpose); }
            catch (Exception error) when (error is IOException io&&TrustedStorage.IsSharingViolation(io)
                ||requestDirectory!=null&&error is System.ComponentModel.Win32Exception native&&native.NativeErrorCode is 32 or 33)
            {
                var ex=error as IOException??new IOException("Native authority lock is busy.",error);
                // New admission and polling stay nonblocking. Already accepted
                // snapshot/export work may wait one polling interval for a
                // competing reader/writer; callers still enforce their500ms
                // observation budget, including all lock and filesystem time.
                long remaining = AcceptedWorkLockWaitMilliseconds - wait.ElapsedMilliseconds;
                if (!acceptedWork || remaining <= 0)
                    throw new ReplayVolumeReservationBusyException("Replay volume reservation lock is busy.", ex);
                Thread.Sleep((int)Math.Min(5, remaining));
            }
        }
    }
    static void ValidateOwner(string owner)
    {
        if (!Guid.TryParseExact(owner, "N", out _)) throw new IOException("Replay volume reservation owner must be a bounded random identifier.");
    }

    static ReplayVolumeClaim ValidateClaim(ReplayVolumeClaim claim)
    {
        if (claim == null || string.IsNullOrWhiteSpace(claim.Path) || string.IsNullOrWhiteSpace(claim.Directory)
            || string.IsNullOrWhiteSpace(claim.Volume) || claim.Volume.Length > 256 || claim.AllocationUnit <= 0
            || claim.PeakAllocatedBytes < 0 || claim.Kind == ReplayVolumeClaimKind.FilePeak && claim.PeakAllocatedBytes % claim.AllocationUnit != 0 || !Enum.IsDefined(claim.Kind))
            throw new IOException("Replay volume reservation claim is incomplete.");
        string path = Path.GetFullPath(claim.Path), directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(claim.Directory));
        if (path.Length > 32767 || directory.Length > 32767 || path.StartsWith(@"\\", StringComparison.Ordinal)
            || directory.StartsWith(@"\\", StringComparison.Ordinal) || path[2..].Contains(':') || directory[2..].Contains(':')
            || !path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Replay volume reservation needs an ordinary local descendant path.");
        return claim with { Path = path, Directory = directory };
    }

    Ledger Read() => Read(out _);
    Ledger Read(out byte[] readBytes)
    {
        readBytes = null;
        try
        {
            TrustedStorage.ValidateFile(ledgerPath);
            using var stream = new FileStream(ledgerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaximumBytes) throw new IOException("Replay volume reservation ledger size is invalid.");
            byte[] bytes = readBytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            var ledger = JsonSerializer.Deserialize<Ledger>(bytes, Json);
            ValidateLedger(ledger);
            return ledger;
        }
        catch (FileNotFoundException) { return new(2, []); }
        catch (JsonException ex) { throw new IOException("Replay volume reservation ledger is unreadable; existing obligations are retained.", ex); }
    }

    static void ValidateLedger(Ledger ledger)
    {
        if (ledger == null || ledger.Version is not (1 or 2 or 3) || ledger.Owners == null || ledger.Owners.Count > MaximumOwners
            || ledger.Version == 3 && !Guid.TryParseExact(ledger.Revision, "N", out _))
            throw new IOException("Replay volume reservation ledger is invalid.");
        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (var holder in ledger.Owners)
        {
            if (holder == null || holder.Claims == null) throw new IOException("Replay volume reservation holder is invalid.");
            ValidateOwner(holder.Owner);
            if (holder.Producer != null && !ReplayProducerProof.Valid(holder.Producer))
                throw new IOException("Replay reservation producer identity is invalid.");
            if (!owners.Add(holder.Owner) || (count = checked(count + holder.Claims.Count)) > MaximumClaims)
                throw new IOException("Replay volume reservation ledger bound exceeded.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var held in holder.Claims)
            {
                if (held == null || string.IsNullOrWhiteSpace(held.Anchor) || held.Anchor.Length > 32767
                    || string.IsNullOrWhiteSpace(held.AnchorIdentity) || held.AnchorIdentity.Length > 256
                    || held.Identity != null && (string.IsNullOrWhiteSpace(held.Identity) || held.Identity.Length > 256)
                    || held.Claim?.Kind == ReplayVolumeClaimKind.UnmaterializedGrowth && held.Identity != null)
                    throw new IOException("Replay volume reservation identity is incomplete.");
                var claim = ValidateClaim(held.Claim);
                if (!paths.Add(claim.Path)) throw new IOException("Replay volume reservation repeats a path.");
                string anchor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(held.Anchor));
                if (!claim.Path.StartsWith(Path.EndsInDirectorySeparator(anchor) ? anchor : anchor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Replay volume reservation anchor is unrelated to its claim.");
            }
        }
    }

    void Persist(Ledger ledger)
    {
        // Every committed mutation gets a distinct epoch, including a return to
        // identical claims. Older writers reject this version instead of writing
        // an unversioned round trip during an unlocked free-space query.
        ledger = ledger with { Version = 3, Revision = Guid.NewGuid().ToString("N") };
        ValidateLedger(ledger);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(ledger, Json);
        if (bytes.Length > MaximumBytes) throw new IOException("Replay volume reservation ledger byte bound exceeded.");
        // The caller owns the shared ledger lock through durable publication.
        string temporary = Path.Combine(root, "replay-volume-reservations-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                TrustedStorage.ValidateFile(temporary);
                stream.Write(bytes);
                stream.Flush(true);
            }
            // Same protected directory and volume: replacement never truncates the
            // accepted ledger. A failed write/move leaves the old promises intact.
            File.Move(temporary, ledgerPath, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static string ExistingDirectory(string directory)
    {
        while (true)
        {
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0) throw new IOException("Replay reservation ancestor is not a directory.");
                return directory;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            directory = Path.GetDirectoryName(directory) ?? throw new IOException("Replay reservation has no observable ancestor.");
        }
    }

    static Observation Observe(ReplayVolumeClaim input, HeldClaim prior, ReplayFiles files, DirectoryObservations pins,ReplayReservationEvidence evidence=null,
        ReplayReservationObservations observations=null, string owner=null)
    {
        var claim = ValidateClaim(input);
        var fact=evidence?.Observe(claim,prior?.Anchor,prior?.AnchorIdentity)
            ?? observations?.Observe(owner,claim,prior?.Anchor,prior?.AnchorIdentity);
        if(fact!=null)
        {
            var extent=ReplayStorage.Validate(fact.Extent);
            if(prior!=null&&claim.Kind!=prior.Claim.Kind||claim.Kind!=ReplayVolumeClaimKind.FilePeak
                ||extent.Volume!=claim.Volume||extent.AllocationUnit!=claim.AllocationUnit
                ||prior?.Identity!=null&&prior.Identity!=extent.Identity || extent.AllocatedBytes>claim.PeakAllocatedBytes)
                throw new IOException("Owned reservation native evidence changed identity/kind/volume/unit.");
            return new(new(claim,extent.Identity,fact.Anchor,fact.AnchorIdentity),extent.AllocatedBytes);
        }
        if (prior != null)
        {
            if (claim.Kind != prior.Claim.Kind) throw new IOException("Replay reservation kind changed without explicit release.");
            if (!string.Equals(pins.Pin(prior.Anchor), prior.AnchorIdentity, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Replay reservation directory identity changed; obligations are retained.");
        }
        string anchor = ExistingDirectory(Path.GetDirectoryName(claim.Path));
        string anchorIdentity = pins.Pin(anchor);
        // A group key names a caller-computed remaining-growth obligation, not
        // a file whose allocation could discharge it. The caller updates its
        // scalar only after native snapshot inputs have actually materialized.
        // Old values overreserve conservatively until that explicit update.
        if (claim.Kind == ReplayVolumeClaimKind.UnmaterializedGrowth)
            return new(new(claim, null, prior?.Anchor ?? anchor, prior?.AnchorIdentity ?? anchorIdentity), 0);
        string identity = prior?.Identity;
        long allocated = 0;
        FileAttributes? attributes = null;
        try { attributes = File.GetAttributes(claim.Path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        if (attributes == null)
        {
            if (identity != null) throw new IOException("Materialized replay reservation disappeared; obligations are retained.");
        }
        else
        {
            if ((attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Replay reservation leaf is not an ordinary file.");
            // A metadata-open failure after observing a leaf is uncertainty, not
            // proof that a never-materialized claim is still absent.
            var extent = ReplayStorage.Validate(files.Observe(claim.Path));
            if (!string.Equals(extent.Volume, claim.Volume, StringComparison.OrdinalIgnoreCase) || extent.AllocationUnit != claim.AllocationUnit
                || identity != null && !string.Equals(identity, extent.Identity, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Replay reservation file identity or volume changed; obligations are retained.");
            allocated = extent.AllocatedBytes;
            identity = extent.Identity;
        }
        return new(new(claim, identity, prior?.Anchor ?? anchor, prior?.AnchorIdentity ?? anchorIdentity), allocated);
    }

    List<(string Owner, List<Observation> Claims)> Refresh(Ledger ledger, ReplayFiles files, DirectoryObservations pins,string evidenceOwner=null,ReplayReservationEvidence evidence=null)
    {
        // Refresh every holder before sampling capacity, including holders in
        // other worker processes. Unknown metadata never releases a promise.
        var refreshed = new List<(string Owner, List<Observation> Claims)>();
        foreach (var holder in ledger.Owners)
            refreshed.Add((holder.Owner, holder.Claims.Select(h => Observe(h.Claim, h, files, pins,holder.Owner==evidenceOwner?evidence:null,
                observations,holder.Owner)).ToList()));
        // First appearance is durable evidence even when the incoming
        // proposal later fails. Checkpoint only existing holders, preserving
        // every obligation; proposed owners never enter this checkpoint.
        bool materialized = false;
        for (int i = 0; i < ledger.Owners.Count; i++)
            for (int j = 0; j < ledger.Owners[i].Claims.Count; j++)
                materialized |= ledger.Owners[i].Claims[j].Identity == null && refreshed[i].Claims[j].Held.Identity != null;
        if (materialized)
            Persist(new(1, refreshed.Select(h => new Holder(h.Owner, h.Claims.Select(o => o.Held).ToList(), ledger.Owners.First(o => o.Owner == h.Owner).Producer)).ToList()));
        return refreshed;
    }

    static Dictionary<string, long> RemainingGrowth(IEnumerable<Observation> all)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, Observation>(StringComparer.OrdinalIgnoreCase);
        var growth = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var observation in all)
        {
            var held = observation.Held; var claim = held.Claim;
            if (!paths.Add(claim.Path)) throw new IOException("Replay reservation paths overlap between owners.");
            long remaining = claim.Kind == ReplayVolumeClaimKind.UnmaterializedGrowth ? claim.PeakAllocatedBytes : Math.Max(0, claim.PeakAllocatedBytes - observation.Allocated);
            if (held.Identity != null)
            {
                string key = claim.Volume + ":" + held.Identity;
                if (identities.TryGetValue(key, out var alias))
                {
                    if (alias.Allocated != observation.Allocated || remaining != 0 || alias.Held.Claim.PeakAllocatedBytes > alias.Allocated)
                        throw new IOException("Replay reservation aliases have ambiguous future growth.");
                    continue;
                }
                identities.Add(key, observation);
            }
            growth[claim.Volume] = checked(growth.GetValueOrDefault(claim.Volume) + remaining);
        }
        return growth;
    }

    static ReplayVolumeCapacity ObserveCapacity(ReplayVolumeClaim claim, ReplayFiles files)
    {
        var capacity = ReplayStorage.ValidateCapacity(files.ObserveCapacity(claim.Directory));
        if (!string.Equals(capacity.Volume, claim.Volume, StringComparison.OrdinalIgnoreCase) || capacity.AllocationUnit != claim.AllocationUnit)
            throw new IOException("Replay reservation capacity volume or allocation unit changed.");
        return capacity;
    }
    // Prefixes are refreshed first. One native capacity query per identical
    // directory/volume/unit target suffices for this locked operation.
    static IEnumerable<ReplayVolumeClaim> CapacityClaims(IEnumerable<Observation> all) => all.Select(o => o.Held.Claim)
        .DistinctBy(c => (c.Directory.ToUpperInvariant(), c.Volume.ToUpperInvariant(), c.AllocationUnit));
    Ledger RecoverExited(Ledger ledger)
    {
        // Discharge future growth only. Materialized files stay in place, charged
        // by caller-free and the retained cache inventory. Unknown/old owners stay.
        if (ledger.Owners.RemoveAll(h => h.Producer?.ConfirmedAbsent() == true) != 0) Persist(ledger);
        return ledger;
    }
    internal void SetProducer(string owner, ReplayProducerProof producer, bool acceptedWork = false)
    {
        ValidateOwner(owner);
        if (!ReplayProducerProof.Valid(producer)) throw new IOException("Replay reservation producer identity is invalid.");
        using var lease = Acquire(acceptedWork);
        BeforeSetProducerForChecks?.Invoke();
        var ledger = Read();
        int index = ledger.Owners.FindIndex(h => h.Owner == owner);
        if (index < 0) return;
        var prior = ledger.Owners[index].Producer;
        if (prior != null && prior with { Sealed = producer.Sealed } != producer)
            throw new IOException("Replay reservation producer identity changed without closure.");
        ledger.Owners[index] = ledger.Owners[index] with { Producer = producer };
        Persist(ledger);
    }
    internal void Update(string owner, IReadOnlyList<ReplayVolumeClaim> claims, ReplayFiles files, ReplayProducerProof producer = null, bool acceptedWork = false,ReplayReservationEvidence evidence=null)
    {
        ValidateOwner(owner);
        if (claims == null || files == null || claims.Count > MaximumClaims) throw new IOException("Replay reservation update is incomplete or exceeds its bound.");
        if(evidence!=null&&evidence.Owner!=owner)throw new IOException("Reservation evidence belongs to another request owner.");
        using var lease = Acquire(acceptedWork);
        if (producer != null && !ReplayProducerProof.Valid(producer)) throw new IOException("Replay reservation producer identity is invalid.");
        var ledger = Read();
        ledger = RecoverExited(ledger);
        var pins = new DirectoryObservations();
        try
        {
            var refreshed = Refresh(ledger, files, pins,owner,evidence);
            var oldProducer = ledger.Owners.FirstOrDefault(h => h.Owner == owner)?.Producer;
            if (producer != null && oldProducer != null && (oldProducer with { Sealed = producer.Sealed }) != producer)
                throw new IOException("Replay reservation producer identity changed without closure.");
            var prior = refreshed.FirstOrDefault(h => string.Equals(h.Owner, owner, StringComparison.OrdinalIgnoreCase)).Claims ?? [];
            var incoming = new List<Observation>();
            foreach (var claim in claims)
            {
                string path = ValidateClaim(claim).Path;
                var old = prior.FirstOrDefault(o => string.Equals(o.Held.Claim.Path, path, StringComparison.OrdinalIgnoreCase));
                incoming.Add(Observe(claim, old?.Held, files, pins,evidence,observations,owner));
            }
            refreshed.RemoveAll(h => string.Equals(h.Owner, owner, StringComparison.OrdinalIgnoreCase));
            if (incoming.Count != 0) refreshed.Add((owner, incoming));
            var proposed = new Ledger(1, refreshed.Select(h => new Holder(h.Owner, h.Claims.Select(o => o.Held).ToList(),
                // Routine capacity refresh must not change launch state. Only
                // SetProducer's ordered creation/assignment callbacks may seal.
                h.Owner == owner ? oldProducer ?? producer
                    : ledger.Owners.First(o => o.Owner == h.Owner).Producer)).ToList());
            ValidateLedger(proposed);
            var all = refreshed.SelectMany(h => h.Claims).ToList();
            var growth = RemainingGrowth(all);
            // Each participating directory gets a fresh caller-visible observation.
            // Multiple paths on one volume use its most restrictive free sample.
            var available = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var claim in CapacityClaims(all))
            {
                var capacity = ObserveCapacity(claim, files);
                available[claim.Volume] = available.TryGetValue(claim.Volume, out long priorFree) ? Math.Min(priorFree, capacity.AvailableBytes) : capacity.AvailableBytes;
            }
            foreach (var volume in growth)
                if (volume.Value > available[volume.Key] - minimumUnreservedBytes)
                    throw new IOException("Shared replay storage capacity cannot hold remaining growth and its minimum unreserved floor on the target volume.");
            Persist(proposed);
            AfterUpdatePersistForChecks?.Invoke();
        }
        finally { pins.Dispose(); }
    }

    internal (string Volume, long AllocationUnit, long Bytes) ObserveUnreserved(string directory, ReplayFiles files, string excludeOwner = null)
    {
        if (excludeOwner != null) ValidateOwner(excludeOwner);
        if (files == null) throw new IOException("Replay capacity observation needs a filesystem provider.");
        using var pins = new DirectoryObservations();
        directory = Path.GetFullPath(directory);
        pins.Pin(ExistingDirectory(directory));
        Ledger ledger;
        byte[] checkpoint;
        ReplayVolumeCapacity capacity;
        using (var lease = Acquire())
        {
            ledger = Read(out checkpoint);
            if (ledger.Version != 3)
            {
                // Legacy holders are preserved byte-for-byte in meaning. This
                // one-time format transition grants no allocation/recovery credit.
                Persist(ledger); ledger = Read(out checkpoint);
            }
        }
        // Native capacity may block. It owns neither the ledger lock nor any
        // caller stop lock. Revalidate the exact durable epoch afterwards, so
        // admission/release during this query cannot turn old free space into
        // uncharged capacity. One caller query remains in flight, without retry.
        capacity = ReplayStorage.ValidateCapacity(files.ObserveCapacity(directory));
        using (var lease = Acquire())
        {
            _ = Read(out var current);
            if (current == null || !checkpoint.AsSpan().SequenceEqual(current))
                throw new ReplayVolumeReservationBusyException("Replay commitments changed during capacity sampling; no fresh evidence.", null);
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var growth = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var holder in ledger.Owners)
            foreach (var held in holder.Claims)
            {
                var claim = held.Claim;
                if (!paths.Add(claim.Path)) throw new IOException("Replay reservation paths overlap between owners.");
                // Periodic capacity needs no holder metadata or recovery I/O.
                // Charge the WHOLE promise, even if already partly allocated or
                // aliased. Caller-free already charges those bytes too: this
                // deliberate overreservation never grants uncertain credit.
                // Admission/materialization still do full identity/allocation
                // validation in Update; release still requires owner closure.
                if (!string.Equals(holder.Owner, excludeOwner, StringComparison.OrdinalIgnoreCase))
                    growth[claim.Volume] = checked(growth.GetValueOrDefault(claim.Volume) + claim.PeakAllocatedBytes);
            }
        // The capture owner applies its own growth+stop threshold separately.
        // One fresh target-volume sample follows the protected ledger read.
        // Other volumes cannot subsidize this one; negative cover is a stop.
        return (capacity.Volume, capacity.AllocationUnit, checked(capacity.AvailableBytes - growth.GetValueOrDefault(capacity.Volume)));
    }

    internal void Release(string owner, bool acceptedWork = false)
    {
        ValidateOwner(owner);
        BeforeReleaseForChecks?.Invoke();
        using var lease = Acquire(acceptedWork);
        var ledger = Read();
        observations.Revoke(owner);
        if (ledger.Owners.RemoveAll(h => string.Equals(h.Owner, owner, StringComparison.OrdinalIgnoreCase)) != 0)
        { BeforeReleasePersistForChecks?.Invoke(); Persist(ledger); }
        observations.Released(owner);
    }
}





