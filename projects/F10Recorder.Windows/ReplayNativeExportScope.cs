using System.Collections.ObjectModel;
namespace F10Recorder;

// Managed fixed-file path only. Data bodies and guard callbacks never execute
// under sync; Cache serializes observation before briefly acquiring this lock.
internal sealed class ReplayNativeExportScope : IDisposable
{
    internal sealed class Snapshot(ReplayNativeExportScope owner,ReplayGuardDirectory.Observation main,
        Dictionary<string, ReplayFileExtent> work, bool conservative) : IReplaySaveObservationScope
    {
        internal readonly ReplayGuardDirectory.Observation Main = main;
        internal readonly IReadOnlyDictionary<string, ReplayFileExtent> Work = new ReadOnlyDictionary<string, ReplayFileExtent>(work);
        public bool RequiresConservative => conservative;
        internal readonly ReplayNativeExportScope Owner=owner;
        internal bool Disposed;
        public void Dispose() { Disposed=true; }
    }
    sealed record Member(ReplayGuardDirectory.FileTicket Creator, ReplayGuardDirectory.FileTicket Observer);
    readonly object sync = new();
    readonly ReplayRootedFiles files;
    readonly ReplayExportRequest request;
    readonly int producerThread = Environment.CurrentManagedThreadId;
    readonly List<ReplayGuardDirectory.FileTicket> inputs = [];
    readonly Dictionary<string, Member> members = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> externalWriters = new(StringComparer.OrdinalIgnoreCase);
    bool disposed, exportClosed, uncertainOwnership;
    internal bool Closed => disposed;
    Action forceObservation;
    internal void BindBarrier(Action force) => forceObservation = force;
    internal bool MainWriterClosed { get { lock(sync) return !members.TryGetValue(request.Partial,out var main) || main.Creator.DataOwnerClosed; } }
    internal bool CanDispose { get { lock(sync) return !uncertainOwnership && decoderOwner == null && externalWriters.Count == 0 && request.IndexReaders.Joined && request.FallbackIndexReaders.Joined && request.PacketReaders.Joined && request.PacketRead is not { Joined: false }
        && request.Closure.SourcesClosed && (request.SourceProtection?.DependenciesClosed??true)
        && inputs.All(i=>i.DataOwnerClosed) && members.Values.All(m=>m.Creator == null || m.Creator.DataOwnerClosed); } }
    internal void RetainUncertain() { lock(sync) uncertainOwnership = true; }
    ReplayPublicationLease lease;
    ReplayDecodeLease decoderOwner;
    ReplayDecodeLease endedDecoderOwner;
    ReplayReservationObservations.Registration reservationObservation;
    long reservationMainPeak;
    Exception reservationObservationError;
    internal bool HasReservationObservation => reservationObservation != null;
    internal bool Eligible => !disposed && Environment.CurrentManagedThreadId == producerThread;
    internal ReplayNativeExportScope(ReplayRootedFiles files, ReplayExportRequest request)
    { this.files = files; this.request = request; }
    internal void RegisterReservationObservation(ReplayVolumeReservations reservations, string owner, long mainPeak)
    {
        lock (sync)
        {
            Require();
            if (reservations == null || reservationObservation != null || mainPeak < 0)
                throw new IOException("Native reservation observation is already owned or incomplete.");
            reservationMainPeak = mainPeak;
            reservationObservation = reservations.RegisterObservation(owner, ObserveReservation);
        }
    }

    // Metadata only: never relax producer-thread permissions on data mutation.
    // Caller releases sync before entering any ledger operation.
    internal Snapshot ObserveMaintenance()
    {
        lock (sync)
        {
            RequireObservationHealthy();
            if (reservationObservation?.Active != true || disposed) return null;
            try
            {
                if (uncertainOwnership) throw new IOException("Native reservation ownership is uncertain.");
                if (ReadProtectedMain() == null) return null;
                var snapshot = Observe();
                try
                {
                    reservationObservation.RequireActive();
                    if (!snapshot.RequiresConservative) return snapshot;
                }
                catch { snapshot.Dispose(); throw; }
                snapshot.Dispose(); return null;
            }
            catch (Exception error) when (ObservationFailure(error)) { reservationObservationError ??= error; throw; }
        }
    }

    static bool ObservationFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or System.ComponentModel.Win32Exception or ObjectDisposedException or OverflowException;
    void RequireObservationHealthy()
    {
        if (reservationObservationError != null)
            throw new IOException("An owned reservation observation previously failed; healthy metadata cannot repair it.", reservationObservationError);
        if (reservationObservation is { Active: false, Released: false })
            throw new IOException("Reservation observation was revoked without confirmed durable release.");
    }

    internal void RequireHealthyPublication()
    {
        lock (sync)
        {
            if (disposed) throw new IOException("Publication owner is already closed.");
            RequireObservationHealthy();
        }
    }

    void RequireReservationConsumption()
    {
        RequireObservationHealthy();
        if (reservationObservation is { Active: false })
            throw new IOException("Producer reservation evidence outlived its registered request authority.");
    }

    ReplayGuardDirectory.Observation ReadProtectedMain()
    {
        if (!members.TryGetValue(request.Partial, out var main)) return null;
        bool exclusive = main.Creator is { DataOwnerClosed: false, CreatedNew: true, AllowsOtherWriters: false };
        bool immutable = lease != null && lease.Stream.CanRead && !lease.Published;
        // Keep the existing decoder/external handoff path until it has an
        // explicitly supported protection; a previous decoder is not authority.
        if (!exclusive && !immutable) return null;
        var current = immutable ? lease.ObserveNative() : main.Observer.Observe();
        if (current.Links != 1 || (current.Attributes & (uint)(FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
            return null;
        if (current.Extent.AllocatedBytes > reservationMainPeak)
            throw new IOException("Owned replay output exceeded its registered main allocation envelope.");
        return current;
    }

    ReplayReservationFact ObserveReservation(ReplayReservationObservations.Registration registration,
        ReplayVolumeClaim claim, string priorAnchor, string priorIdentity)
    {
        lock (sync)
        {
            RequireObservationHealthy();
            try
            {
            registration.RequireActive();
            if (disposed || uncertainOwnership) throw new IOException("Native reservation observer is unavailable.");
            if (!claim.Path.Equals(request.Partial, StringComparison.OrdinalIgnoreCase))
            {
                if (claim.Kind == ReplayVolumeClaimKind.UnmaterializedGrowth
                    || ReplayOutputWorkspace.Classify(request.Partial, claim.Path) != null) return null;
                throw new IOException("Owned reservation claim is outside its request output.");
            }
            if (claim.Kind != ReplayVolumeClaimKind.FilePeak || claim.PeakAllocatedBytes != reservationMainPeak)
                throw new IOException("Owned reservation kind or peak changed.");
            var directory = files.DirectoryOwner(Path.GetDirectoryName(request.Partial)).Native;
            string anchor = directory.OriginalPath, identity = directory.Identity;
            if (!claim.Directory.Equals(anchor, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Owned reservation target or anchor changed.");
            RequireReservationAnchor(directory, priorAnchor, priorIdentity);
            var current = ReadProtectedMain();
            registration.RequireActive();
            if (current == null) return null;
            if (current.Extent.Volume != claim.Volume || current.Extent.AllocationUnit != claim.AllocationUnit)
                throw new IOException("Owned reservation target volume or allocation unit changed.");
            return new(current.Extent, anchor, identity);
            }
            catch (Exception error) when (ObservationFailure(error)) { reservationObservationError ??= error; throw; }
        }
    }
    static void RequireReservationAnchor(ReplayGuardDirectory directory, string priorAnchor, string priorIdentity)
    {
        if (priorAnchor == null) return;
        if (priorAnchor.Equals(directory.OriginalPath, StringComparison.OrdinalIgnoreCase))
        {
            if (priorIdentity != directory.Identity) throw new IOException("Owned reservation target or anchor changed.");
            return;
        }
        // Admission can precede output-folder creation and retain an existing
        // ancestor. Verify that recorded identity freshly while pinned, inside
        // this observer's failure boundary. Returning null before this check
        // would let a peer's generic failure escape the sticky owner error.
        string prefix = Path.EndsInDirectorySeparator(priorAnchor) ? priorAnchor : priorAnchor + Path.DirectorySeparatorChar;
        if (!directory.OriginalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Owned reservation target or anchor changed.");
        using var pins = TrustedStorage.PinDirectories(priorAnchor);
        if (Native.DirectoryIdentity(priorAnchor) != priorIdentity)
            throw new IOException("Owned reservation ancestor identity changed.");
    }
    void Require() { if (disposed || !Eligible) throw new IOException("Native export operation is outside its serialized managed producer."); }
    internal ReplayFileExtent Extent(string path)
    {
        var directory = files.DirectoryOwner(Path.GetDirectoryName(path));
        return directory.Files[Path.GetFileName(path)].Extent;
    }
    internal FileStream OpenInput(string path, ReplayFileExtent expected, bool asynchronous)
    {
        lock (sync)
        {
            Require(); var root = files.DirectoryOwner(Path.GetDirectoryName(path)).Native;
            var ticket = asynchronous ? root.OpenAsyncRead(Path.GetFileName(path), expected) : root.OpenRead(Path.GetFileName(path), expected);
            inputs.Add(ticket); return ticket.Data();
        }
    }
    internal void CreateWorkspace() { Require(); forceObservation?.Invoke(); files.CreateDirectory(request.WorkDirectory); request.OwnWorkDirectory=true; forceObservation?.Invoke(); }
    internal void AdoptPreparedBoundary()
    {
        Require();
        var preparation = request.Preparation ?? throw new IOException("No request-owned preparation.");
        var observer = preparation.TransferClosed(request);
        lock (sync) members.Add(preparation.Boundary, new(null, observer));
        forceObservation?.Invoke();
    }
    internal FileStream CreateMain(bool asynchronous = false)
    {
        Require(); forceObservation?.Invoke(); FileStream result;
        lock (sync)
        {
            Require(); if (request.Integrity == null) throw new IOException("Main creation precedes frozen source proof.");
            var root = files.DirectoryOwner(Path.GetDirectoryName(request.Partial)).Native;
            var creator = root.CreateNew(Path.GetFileName(request.Partial), asynchronous);
            try
            {
                var observer = root.OpenObservation(Path.GetFileName(request.Partial), creator.Observe().Extent);
                members.Add(request.Partial, new(creator, observer)); result = creator.Data();
            }
            catch { creator.Dispose(); throw; }
        }
        forceObservation?.Invoke(); return result;
    }
    internal void CopyBoundary(string original, string target)
    {
        Require(); forceObservation?.Invoke(); files.Copy(original, target);
        lock (sync)
        {
            var root = files.DirectoryOwner(Path.GetDirectoryName(target)).Native;
            members.Add(target, new(null, root.OpenObservation(Path.GetFileName(target), Extent(target))));
        }
        forceObservation?.Invoke();
    }
    // The path-consuming codec opens this already-created direct leaf. These
    // handles allow its writes but deny unlink/rename; the retained parent
    // capability prevents an ancestor redirect throughout the child lifetime.
    internal void BeginExternal(string path)
    {
        Require(); forceObservation?.Invoke();
        lock (sync)
        {
            Require();
            if (!path.Equals(request.Partial, StringComparison.OrdinalIgnoreCase)
                && (!request.OwnWorkDirectory || ReplayOutputWorkspace.Classify(request.Partial, path) != ReplayOutputWorkspace.Member.Boundary))
                throw new IOException("External producer output is outside the admitted recipe.");
            if (members.ContainsKey(path)) throw new IOException("External output already has an owner.");
            var root = files.DirectoryOwner(Path.GetDirectoryName(path)).Native;
            var creator = root.CreateExternalScratch(Path.GetFileName(path));
            try
            {
                var observer = root.OpenObservation(Path.GetFileName(path), creator.Observe().Extent);
                members.Add(path, new(creator, observer)); externalWriters.Add(path);
            }
            catch { creator.Dispose(); throw; }
        }
        forceObservation?.Invoke();
    }
    // Caller supplies actual owned-child absence, never a timeout assumption.
    internal void CompleteExternal(string path)
    {
        lock (sync)
        {
            Require();
            if (!externalWriters.Contains(path)) throw new IOException("External producer does not own this leaf.");
            var member = members[path];
            var expected = member.Observer.Observe().Extent;
            member.Creator.Dispose(); // Immutable admission must deny this writer too.
            files.Admit(path, expected, true);
            externalWriters.Remove(path);
        }
        forceObservation?.Invoke();
    }
    internal void CloseFailedExternal(string path, bool producersAbsent)
    {
        lock (sync)
        {
            if (!externalWriters.Contains(path)) return;
            if (!producersAbsent) { uncertainOwnership = true; return; }
        }
        try { CompleteExternal(path); }
        catch
        {
            lock (sync) if (externalWriters.Contains(path)) uncertainOwnership = true;
            // Preserve the original producer/guard error. If the only failure
            // was its already-poisoned final guard, actual users still closed.
        }
    }
    internal void BindDecodedPublication(ReplayPublicationLease checkedOutput)
    {
        if (request.Recipe != ReplayExactExport.Recipe || checkedOutput == null) return;
        lock (sync)
        {
            Require();
            if (!exportClosed || externalWriters.Count != 0 || lease != null)
                throw new IOException("Decoded publication precedes producer closure or already has an owner.");
            var expected = checkedOutput.CheckedIdentity;
            var main = members[request.Partial];
            var actual = main.Observer.Observe().Extent;
            if (actual.Volume != expected.Volume || actual.Identity != expected.Identity || actual.Length != expected.Length)
                throw new IOException("Decoded object differs from the held external producer output.");
            main.Observer.Dispose();
            var root = files.DirectoryOwner(Path.GetDirectoryName(request.Partial)).Native;
            checkedOutput.BindRootedPublication(root, root.OpenForRemoval(Path.GetFileName(request.Partial), expected));
            lease = checkedOutput;
        }
        forceObservation?.Invoke();
    }
    internal Snapshot Observe()
    {
        lock (sync)
        {
            if (disposed) throw new IOException("Native output observer is closed.");
            RequireObservationHealthy();
            ReplayGuardDirectory.Observation main = null; bool conservative = false;
            IReadOnlyList<ReplayStorage.DirectoryEntry> parentEntries = null;
            if (!request.OwnWorkDirectory || !members.ContainsKey(request.Partial))
                parentEntries = files.DirectoryOwner(Path.GetDirectoryName(request.Partial)).Native.EnumerateLeaves(4096);
            if (!request.OwnWorkDirectory && parentEntries.Any(e => e.Path.Equals(request.WorkDirectory,StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Unowned entry appeared at the reserved workspace name.");
            if (lease != null) main = lease.ObserveNative();
            else if (members.TryGetValue(request.Partial, out var member)) main = member.Observer.Observe();
            else
            {
                // Absence is checked anew; a foreign reserved name is not
                // silently adopted as a created output or allocation credit.
                if (parentEntries.Any(e => e.Path.Equals(request.Partial, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Unowned file appeared at the reserved output name.");
            }
            if (main != null) conservative |= main.Links != 1;
            var work = new Dictionary<string, ReplayFileExtent>(StringComparer.OrdinalIgnoreCase);
            if (request.OwnWorkDirectory)
            {
                var root = files.DirectoryOwner(request.WorkDirectory).Native;
                var prepared = request.Preparation?.ObserveWork();
                var listed = root.EnumerateLeaves(3);
                foreach (var entry in listed)
                {
                    if (prepared != null && prepared.TryGetValue(entry.Path, out var preparedExtent))
                    {
                        if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.SparseFile)) != 0
                            || entry.Extent.Identity != preparedExtent.Identity || entry.Extent.Volume != preparedExtent.Volume)
                            throw new IOException("Prepared namespace differs from its held boundary.");
                        work.Add(entry.Path, preparedExtent); continue;
                    }
                    if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.SparseFile)) != 0
                        || !members.TryGetValue(entry.Path, out var owned) || ReplayOutputWorkspace.Classify(request.Partial, entry.Path) == null)
                        throw new IOException("Owned workspace contains an unregistered or unsupported member.");
                    var fresh = owned.Observer.Observe();
                    if (entry.Extent.Identity != fresh.Extent.Identity || entry.Extent.Volume != fresh.Extent.Volume)
                        throw new IOException("Workspace namespace differs from its held file.");
                    conservative |= fresh.Links != 1;
                    work.Add(entry.Path, fresh.Extent); // Numeric facts ONLY from the held file.
                    if (Math.Max(fresh.Extent.Length, fresh.Extent.AllocatedBytes) > ReplaySaveExport.BoundaryLimit)
                        throw new IOException("Native boundary exceeded its recipe envelope.");
                }
                if (members.Keys.Where(p => !p.Equals(request.Partial, StringComparison.OrdinalIgnoreCase)).Any(p => !work.ContainsKey(p)))
                    throw new IOException("Native workspace member disappeared.");
                if (work.Values.Sum(e => Math.Max(e.Length, e.AllocatedBytes)) > ReplaySaveExport.WorkDemand(request.Inputs.Sum(i => i.Extent.Length), 4096).Total)
                    throw new IOException("Native workspace exceeded its combined recipe envelope.");
            }
            return new(this,main, work, conservative);
        }
    }
    internal void BindDecoderObservation(ReplayDecodeLease decoder, string path)
    {
        lock (sync)
        {
            Require();
            if (request.Recipe != ReplayExactExport.Recipe || !exportClosed || externalWriters.Count != 0
                || decoder == null || decoderOwner != null || endedDecoderOwner != null || lease != null || uncertainOwnership
                || !path.Equals(request.Partial, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Decoder metadata precedes closed output ownership or is already bound.");
            var current = members[request.Partial].Observer.Observe();
            _ = decoder.ObserveReservation(current.Extent);
            decoderOwner = decoder;
        }
    }
    internal void EndDecoderObservation(ReplayDecodeLease decoder, bool uncertain)
    {
        lock (sync)
        {
            if (!ReferenceEquals(decoderOwner, decoder) && !ReferenceEquals(endedDecoderOwner, decoder))
                throw new IOException("Decoder metadata revocation has a different owner.");
            decoderOwner = null;
            endedDecoderOwner = decoder;
            uncertainOwnership |= uncertain;
        }
    }
    internal ReplayReservationEvidence BorrowReservationEvidence(Snapshot snapshot,string reservationOwner)
    {
        lock (sync)
        {
        Require();
        RequireReservationConsumption();
        if(snapshot==null||snapshot.Owner!=this||snapshot.Disposed)throw new IOException("Reservation snapshot provenance/lifetime mismatch.");
        if(snapshot.RequiresConservative||snapshot.Main==null)return null;
        bool exclusive=members.TryGetValue(request.Partial,out var main)&&main.Creator is{DataOwnerClosed:false,CreatedNew:true,AllowsOtherWriters:false};
        bool immutable=lease!=null&&lease.Stream.CanRead&&!lease.Published;
        var decoder = decoderOwner;
        if(!exclusive&&!immutable&&decoder==null)return null; // Unprotected handoff stays conservative.
        var directory=files.DirectoryOwner(Path.GetDirectoryName(request.Partial)).Native;
        string anchor=directory.OriginalPath,anchorIdentity=directory.Identity;
        return new(reservationOwner,(claim,priorAnchor,priorIdentity)=>
        {
            lock (sync)
            {
            Require();
            RequireReservationConsumption();
            if(snapshot.Disposed||snapshot.Owner!=this)throw new IOException("Expired native reservation snapshot.");
            if(claim.Kind!=ReplayVolumeClaimKind.FilePeak||!claim.Path.Equals(request.Partial,StringComparison.OrdinalIgnoreCase))return null;
            RequireReservationAnchor(directory,priorAnchor,priorIdentity);
            if (decoder != null)
            {
                if (!ReferenceEquals(decoderOwner, decoder))
                    throw new IOException("Decoder metadata protection ended inside callback.");
                var current = members[request.Partial].Observer.Observe();
                if (current.Links != 1 || current.Extent.Volume != snapshot.Main.Extent.Volume
                    || current.Extent.Identity != snapshot.Main.Extent.Identity
                    || current.Extent.Length != snapshot.Main.Extent.Length)
                    throw new IOException("Decoder output namespace, links or extent changed.");
                return new(decoder.ObserveReservation(current.Extent), anchor, anchorIdentity);
            }
            if(exclusive&&(!members.TryGetValue(request.Partial,out var now)||now.Creator.DataOwnerClosed)
                ||immutable&&(lease==null||!lease.Stream.CanRead||lease.Published))throw new IOException("Owned reservation protection ended inside callback.");
            return new(snapshot.Main.Extent,anchor,anchorIdentity);
            }
        });
        }
    }
    // Actual batch/extraction finally has returned and closed every FileStream.
    // Tickets close only now; no join/wait occurs under sync or Cache.gate.
    internal void CompleteExport()
    {
        Require();
        if (!CanDispose) { RetainUncertain(); throw new IOException("Actual source batch/writer closure is not confirmed."); }
        foreach (var input in inputs) input.Dispose(); inputs.Clear();
        lock (sync)
        {
            if (members.TryGetValue(request.Partial, out var main))
            {
                main.Creator.Dispose();
                if (request.Recipe != ReplayExactExport.Recipe)
                    files.Admit(request.Partial, main.Observer.Observe().Extent, true);
            }
            exportClosed = true;
        }
    }
    internal void AttachLease()
    {
        if (request.Recipe == ReplayExactExport.Recipe) return; // Full decoder retains its existing immutable lease.
        Require(); if (!exportClosed) throw new IOException("Native source users have not closed.");
        request.AttachNativePublication(() =>
        {
            ReplayPublicationLease result;
            lock (sync)
            {
                Require(); var root = files.DirectoryOwner(Path.GetDirectoryName(request.Partial)).Native;
                var reader = request.PacketPipelineSelected ? root.OpenAsyncPublicationRead(Path.GetFileName(request.Partial), request.ManagedMux.FinalOutput) : root.OpenPublicationRead(Path.GetFileName(request.Partial), request.ManagedMux.FinalOutput);
                try
                {
                    members[request.Partial].Observer.Dispose();
                    lease = new(request.Partial, root, reader, request.ManagedMux.FinalOutput); result = lease;
                }
                catch { reader.Dispose(); throw; }
            }
            forceObservation?.Invoke(); return result;
        });
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            if (reservationObservation?.Active == true)
                throw new IOException("Native reservation observation must be revoked under the ledger before closure.");
            if (!CanDispose) { uncertainOwnership = true; throw new IOException("Unknown native producer/read ownership remains retained."); }
            foreach (var input in inputs) input.Dispose(); inputs.Clear();
            foreach (var item in members.Values) { item.Creator?.Dispose(); item.Observer.Dispose(); }
            disposed = true;
        }
    }
}
