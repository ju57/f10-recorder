namespace F10Recorder;

// Isolated integration prototype, not selected by the maintained recorder.
// This owner is deliberately limited to managed, serialized output writers.
// Its storage consumer must not launch a producer or write between consumers
// of one snapshot. External-child and custom-provider paths are ineligible.
internal sealed class ReplaySaveEvidenceOwner : IDisposable
{
    internal enum Role { Main, Scratch }
    internal sealed record Member(string Leaf, ReplayGuardDirectory.Observation Observation);
    internal sealed class Snapshot
    {
        readonly IReadOnlyList<Member> members;
        bool active = true;
        internal Snapshot(IEnumerable<Member> members) => this.members = Array.AsReadOnly(members.ToArray());
        internal IReadOnlyList<Member> Members => active ? members : throw new IOException("Output evidence scope ended.");
        internal void End() => active = false;
    }
    sealed class Output(string leaf, long limit, Role role, ReplayGuardDirectory.FileTicket writer,
        ReplayGuardDirectory.FileTicket observer)
    {
        internal readonly string Leaf = leaf;
        internal readonly long Limit = limit;
        internal readonly Role Role = role;
        internal ReplayGuardDirectory.FileTicket Writer = writer, Observer = observer, Checked, Publication;
        internal ReplayFileExtent Closed;
        internal ReplayIntegrityResult Validation;
        internal bool Writing, Published;
    }
    readonly ReplayGuardDirectory directory;
    readonly Func<Snapshot, bool> observeGlobal;
    readonly ReplayStorageObservationGate global;
    readonly Dictionary<string, Output> outputs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ReplayMp4Expectation> expectations = new(StringComparer.OrdinalIgnoreCase);
    readonly object sync = new();
    readonly long combinedLimit;
    Snapshot current;
    bool poisoned, disposed, callback, publishing;
    int inputStages;

    // Directory lifetime transfers to this owner. The exact native volume,
    // directory ID, filesystem support and path policy were admitted upstream.
    internal ReplaySaveEvidenceOwner(ReplayGuardDirectory directory, long combinedLimit,
        Func<Snapshot, bool> observeGlobal, Func<long> timestamp = null, long frequency = 0)
    {
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        if (combinedLimit <= 0) throw new ArgumentOutOfRangeException(nameof(combinedLimit));
        this.combinedLimit = combinedLimit;
        this.observeGlobal = observeGlobal ?? throw new ArgumentNullException(nameof(observeGlobal));
        global = new(() => this.observeGlobal(current ?? throw new IOException("Global evidence has no current output scope.")),
            100, 500, timestamp: timestamp, frequency: frequency);
    }
    void Require(bool publicationCheck = false)
    {
        if (disposed || poisoned) throw new IOException("Accepted-save evidence owner is closed or uncertain.");
        if (callback) throw new IOException("Evidence callbacks cannot reenter owned operations.");
        if (publishing && !publicationCheck) throw new IOException("Publication owns this evidence lifetime.");
    }
    internal void Check(bool force = false) { lock (sync) CheckCore(force); }
    void CheckCore(bool force)
    {
        Require(publicationCheck: true); callback = true;
        try
        {
            global.RequireFreshness();
            var fresh = new List<Member>(outputs.Count);
            var identities = new HashSet<(string Volume, string Identity)>();
            long total = 0;
            bool conservative = false;
            foreach (var output in outputs.Values.Where(o => !o.Published))
            {
                var actual = (output.Observer ?? output.Publication ?? throw new IOException("Owned output has no live pathname-pinning observation owner.")).Observe();
                if (actual.Extent.AllocatedBytes > output.Limit || actual.Extent.Length > output.Limit)
                    throw new IOException("Owned output exceeded its declared per-file envelope.");
                if (!identities.Add((actual.Extent.Volume, actual.Extent.Identity)))
                    throw new IOException("Owned output members alias one another.");
                if (output.Closed != null && actual.Extent.Length != output.Closed.Length)
                    throw new IOException("Closed output grew or shrank before publication.");
                total = checked(total + Math.Max(actual.Extent.Length, actual.Extent.AllocatedBytes));
                // A new hardlink invalidates the cheap uniqueness certificate.
                // It requires the old global ownership check THIS callback;
                // it never waits for the next scheduled global observation.
                conservative |= actual.Links != 1;
                fresh.Add(new(output.Leaf, actual));
            }
            if (total > combinedLimit) throw new IOException("Owned output exceeded its combined envelope.");
            current = new(fresh);
            try { if (force || conservative) global.CheckNow(); else global.Check(); }
            finally { current.End(); current = null; }
            // This includes all local output checks and global checking work.
            global.RequireFreshness();
        }
        catch { lock (sync) poisoned = true; throw; }
        finally { callback = false; }
    }

    // The actual index batch must return its joined task, including all failed
    // or cancelled readers. No caller receives ownership of a native ticket.
    // The directory cannot close while that task owns any source stream.
    internal async Task<T> WithInputsAsync<T>(ReplayGuardDirectory inputDirectory,
        IReadOnlyList<(string Leaf, ReplayFileExtent Expected)> inputs,
        Func<IReadOnlyList<FileStream>, Task<T>> joinedBatch)
    {
        lock (sync)
        {
            Require();
            if (inputStages != 0 || outputs.Values.Any(o => o.Writing)) throw new IOException("Input stage overlaps an unsupported producer.");
            inputStages++;
        }
        var tickets = new List<ReplayGuardDirectory.FileTicket>();
        try
        {
            foreach (var input in inputs)
            {
                Check();
                tickets.Add(inputDirectory.OpenAsyncRead(input.Leaf, input.Expected));
            }
            var streams = Array.AsReadOnly(tickets.Select(t => t.Data()).ToArray());
            if (streams.Any(s => !s.IsAsync)) throw new IOException("Native input did not preserve asynchronous reader semantics.");
            return await joinedBatch(streams).ConfigureAwait(false);
        }
        catch { lock (sync) poisoned = true; throw; }
        finally
        {
            Exception closeError = null;
            foreach (var ticket in tickets)
                try { ticket.Dispose(); } catch (Exception error) { closeError ??= error; }
            lock (sync)
            {
                inputStages--;
                if (closeError != null) { poisoned = true; throw new IOException("Input batch closed with uncertain ownership.", closeError); }
            }
        }
    }

    internal void FreezeExpectation(string leaf, ReplayMp4Expectation expectation)
    {
        lock (sync)
        {
            Require(); ReplayGuardDirectory.ValidateLeaf(leaf);
            if (expectation == null || outputs.ContainsKey(leaf) || !expectations.TryAdd(leaf, expectation))
                throw new IOException("Source proof must be frozen once before creating the output it will check.");
        }
    }
    internal void WriteManaged(string leaf, long limit, Role role, Action<FileStream, Action> write)
    { lock (sync) WriteManagedCore(leaf, limit, role, write); }
    void WriteManagedCore(string leaf, long limit, Role role, Action<FileStream, Action> write)
    {
        Require();
        if (outputs.Values.Any(o => o.Writing) || outputs.ContainsKey(leaf)
            || limit <= 0 || limit > combinedLimit || !Enum.IsDefined(role)
            || role == Role.Main && !expectations.ContainsKey(leaf)) throw new IOException("Managed output stage lacks its role, pre-write proof or envelope.");
        Check(force: true);
        ReplayGuardDirectory.FileTicket creator = null, observer = null;
        Output output = null;
        try
        {
            creator = directory.CreateNew(leaf);
            if (!creator.CreatedNew || creator.AllowsOtherWriters) throw new IOException("Managed output lacks exclusive creation evidence.");
            observer = directory.OpenObservation(leaf, creator.Observe().Extent);
            output = new(leaf, limit, role, creator, observer) { Writing = true };
            outputs.Add(leaf, output); creator = observer = null;
            write(output.Writer.Data(), () => Check());
            output.Writer.Data().Flush();
            output.Writer.Dispose(); output.Writer = null; output.Writing = false;
            output.Closed = output.Observer.Observe().Extent;
            Check(force: true);
        }
        catch { poisoned = true; throw; }
        finally
        {
            creator?.Dispose(); observer?.Dispose();
            // A synchronous managed callback has returned before close. An
            // external or unjoined asynchronous writer cannot use this route.
            if (output?.Writer != null)
            {
                try { output.Writer.Dispose(); output.Writer = null; output.Writing = false; }
                catch { poisoned = true; throw; }
            }
        }
    }

    internal ReplayIntegrityResult Validate(string leaf,
        ReplayInterval interval, int framesPerSecond, bool audio)
    { lock (sync) return ValidateCore(leaf, interval, framesPerSecond, audio); }
    ReplayIntegrityResult ValidateCore(string leaf, ReplayInterval interval, int framesPerSecond, bool audio)
    {
        Require();
        if (!outputs.TryGetValue(leaf, out var output) || output.Role != Role.Main || output.Writing || output.Published
            || output.Closed == null || output.Checked != null || output.Validation != null
            || !expectations.TryGetValue(leaf, out var expectation))
            throw new IOException("Output has no eligible closed producer or independent source expectation.");
        try
        {
            Check(force: true);
            output.Checked = directory.OpenPublicationRead(leaf, output.Closed);
            var stream = output.Checked.Data();
            var index = ReplayMp4Reader.Read(stream, output.Closed.Length, interval, framesPerSecond, audio,
                () => Check(), includeVideoPayloadDigest: false);
            var result = expectation.Verify(index, () => Check());
            if (stream.Length != output.Closed.Length) throw new IOException("Validated output EOF changed.");
            Check(force: true);
            output.Validation = result;
            return result;
        }
        catch { poisoned = true; throw; }
    }

    internal void Publish(string leaf, ReplayGuardDirectory destination, string finalLeaf, Action releaseClaims)
    { lock (sync) PublishCore(leaf, destination, finalLeaf, releaseClaims); }
    void PublishCore(string leaf, ReplayGuardDirectory destination, string finalLeaf, Action releaseClaims)
    {
        Require();
        if (!outputs.TryGetValue(leaf, out var output) || output.Validation == null
            || output.Checked == null || output.Published || output.Writing)
            throw new IOException("Publication requires this owner's checked immutable output.");
        try
        {
            publishing = true;
            Check(force: true);
            output.Observer.Dispose(); output.Observer = null;
            // The immutable checked reader allows DELETE sharing. Acquire a
            // verified DELETE-capable/no-delete-sharing ticket BEFORE another
            // callback so the actual leaf cannot be replaced during release.
            output.Publication = directory.OpenForRemoval(leaf, output.Closed);
            Check(force: true);
            releaseClaims();
            Check(force: true);
            output.Publication.RenameTo(destination, finalLeaf);
            output.Published = true; // No later observer failure can undo publication.
        }
        catch { poisoned = true; throw; }
        finally { publishing = false; }
    }
    public void Dispose() { lock (sync) DisposeCore(); }
    void DisposeCore()
    {
        if (disposed) return;
        if (callback || publishing || inputStages != 0 || outputs.Values.Any(o => o.Writing))
            throw new IOException("Cannot close evidence while a producer or reader stage remains live.");
        List<Exception> failures = [];
        foreach (var output in outputs.Values)
        {
            foreach (var owner in new[] { output.Writer, output.Observer, output.Checked, output.Publication })
                try { owner?.Dispose(); } catch (Exception error) { failures.Add(error); }
        }
        try { directory.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) { poisoned = true; throw new AggregateException("Accepted-save evidence closure is uncertain.", failures); }
        disposed = true;
    }
}

