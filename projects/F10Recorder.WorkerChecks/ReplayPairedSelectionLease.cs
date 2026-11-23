namespace F10Recorder;

// Qualification-only owner. The first request's protected input objects remain
// immutable across both actual exports. New snapshot names are independent;
// ordered native source identities, extents, interval and profile must match.
internal sealed class ReplayPairedSelectionLease : IDisposable
{
    static readonly List<ReplayPairedSelectionLease> uncertain = new();
    // A failed qualification cannot release its extra input owner as proof of
    // producer absence. The bounded controller exits on this first failure.
    internal static void RetainUntilControllerExit(ReplayPairedSelectionLease owner)
    { if (owner != null) lock (uncertain) if (!uncertain.Contains(owner)) uncertain.Add(owner); }
    readonly List<FileStream> inputs = new();
    readonly Segment[] protectedSegments;
    readonly ReplayExportRequest original;
    readonly string sourceDirectory;
    readonly ReplayGuardDirectory sourceRoot;
    readonly Dictionary<string,string> parentIdentities = new(StringComparer.OrdinalIgnoreCase);
    readonly List<object> replayAliases = new();
    readonly object replayGate = new();
    bool replayPreparationAttempted;
    Exception replayPreparationError;
    internal readonly string AcquisitionSelectionHash;
    internal static string Identity(ReplayExportRequest request, string scope) => ReplayReferenceModel.Hash(new {
        scope, request.Interval, request.Info.FramesPerSecond, request.Info.Codec, request.Info.RecordAudio,
        inputs = request.Inputs.Select(i => new { i.Start, i.End, i.Extent.Volume, i.Extent.Identity, i.Extent.Length, i.Extent.AllocationUnit }).ToArray()
    });
    internal ReplayPairedSelectionLease(ReplayExportRequest request, string scope, Action check)
    {
        if (request.Info.ReplaySeconds is < 1 or > 1800 || request.Inputs.Count < 1
            || request.Inputs.Count > request.Info.ReplaySeconds + 5) throw new IOException("Paired acquisition input bound exceeded.");
        AcquisitionSelectionHash = Identity(request, scope);
        original = request;
        sourceDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Info.Directory));
        protectedSegments = request.Inputs.Select(i => new Segment { Path = i.Path, Start = i.Start, End = i.End }).ToArray();
        try
        {
            sourceRoot = new(sourceDirectory, Native.DirectoryIdentity(sourceDirectory), ReplayStorage.ObserveCapacity(sourceDirectory).Volume);
            foreach (var input in request.Inputs)
            {
                check?.Invoke();
                var stream = new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                inputs.Add(stream);
                var observed = ReplayStorage.ObserveInventory(stream.SafeFileHandle, input.Extent);
                if (observed.Length != input.Extent.Length) throw new IOException("Paired protected source extent changed.");
                string parent = Path.GetDirectoryName(Path.GetFullPath(input.Path));
                if (!parent.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(Path.GetDirectoryName(parent), sourceDirectory, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Paired protected input is outside the original session or an immediate child.");
                    if (!parentIdentities.ContainsKey(parent))
                    {
                        using var child = sourceRoot.OpenDirectory(Path.GetFileName(parent), Native.DirectoryIdentity(parent));
                        parentIdentities.Add(parent, child.Identity);
                    }
                }
            }
        }
        catch { try { Dispose(); } catch { } throw; }
    }
    // The live capture inventory may advance while the first output completes.
    // The paired request must select from this owner's protected objects rather
    // than ask that newer inventory to reconstruct an earlier source window.
    internal List<Segment> ReadSegments()
    { lock (replayGate) return protectedSegments.Select(s => new Segment { Path = s.Path, Start = s.Start, End = s.End }).ToList(); }
    internal object ReplayAliases { get { lock (replayGate) return replayAliases.ToArray(); } }
    // Each save has its own rooted registry. Re-root foreign-parent protected
    // objects into the ORIGINAL session only after the actual producer stops.
    // Relative native links preserve objects, order and clocks without granting
    // shipping authority over any prior snapshot directory.
    internal void PrepareForReplay(IReplaySessionSource source, Action check)
    {
        lock (replayGate)
        {
            if (replayPreparationAttempted)
            {
                if (replayPreparationError != null) throw new IOException("Earlier paired alias preparation failed; owners remain retained.", replayPreparationError);
                return;
            }
            replayPreparationAttempted = true;
            try
            {
                if (!source.Stopped || !source.OutputProducerStopped || !Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.ReplayInfo.Directory)).Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Paired aliases require the stopped original session and actual producer absence.");
                int foreign = original.Inputs.Count(i => !Path.GetDirectoryName(Path.GetFullPath(i.Path)).Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase));
                if (foreign > 512) throw new IOException("Paired alias namespace exceeds its finite bound.");
                string nonce = Guid.NewGuid().ToString("N");
                for (int n = 0; n < original.Inputs.Count; n++)
                {
                    check?.Invoke(); var input = original.Inputs[n];
                    string parent = Path.GetDirectoryName(Path.GetFullPath(input.Path));
                    if (parent.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                    var before = ReplayStorage.ObserveInventory(inputs[n].SafeFileHandle, input.Extent);
                    if (before.Length != input.Extent.Length) throw new IOException("Paired held object extent changed before alias.");
                    using var child = sourceRoot.OpenDirectory(Path.GetFileName(parent), parentIdentities[parent]);
                    using var ticket = child.OpenRead(Path.GetFileName(input.Path), input.Extent);
                    string leaf = $"paired-input-{nonce}-{n:D6}{original.Info.Extension}";
                    ticket.LinkTo(sourceRoot, leaf);
                    using var alias = sourceRoot.OpenRead(leaf, input.Extent);
                    var extent = alias.Observe().Extent;
                    if (extent.Volume != input.Extent.Volume || extent.Identity != input.Extent.Identity || extent.Length != input.Extent.Length || extent.AllocationUnit != input.Extent.AllocationUnit)
                        throw new IOException("Paired alias changed the original native object or extent.");
                    string path = Path.Combine(sourceDirectory, leaf);
                    protectedSegments[n] = new() { Path = path, Start = input.Start, End = input.End };
                    replayAliases.Add(new { source = input.Path, alias = path, extent, input.Start, input.End });
                }
            }
            catch (Exception error)
            { replayPreparationError = error; RetainUntilControllerExit(this); throw; }
        }
    }
    internal void Verify(ReplayExportRequest request, string scope, Action check)
    {
        if (Identity(request, scope) != AcquisitionSelectionHash) throw new IOException("Paired requests do not select the same ordered protected source objects and interval.");
        foreach (var input in request.Inputs)
        {
            check?.Invoke();
            using var stream = new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            var observed = ReplayStorage.ObserveInventory(stream.SafeFileHandle, input.Extent);
            if (observed.Length != input.Extent.Length) throw new IOException("Paired snapshot extent changed.");
        }
    }
    internal void WriteCompletedProofMapping(string proofPath, string outputPath, Action check)
    {
        lock (replayGate)
        {
            if (!replayPreparationAttempted || replayPreparationError != null) throw new IOException("C00 aliases are not sealed.");
            var proof = System.Text.Json.JsonSerializer.Deserialize<ReplayCompletedInputDigest[]>(File.ReadAllBytes(proofPath));
            if (proof.Length != original.Inputs.Count) throw new IOException("C00 established digest count differs.");
            var mapped = proof.Select((p, n) => {
                check?.Invoke(); var input = original.Inputs[n];
                if (p.Path != input.Path || p.Start != input.Start || p.End != input.End || p.Extent != input.Extent)
                    throw new IOException("C00 established digest/native selection attribution changed.");
                string path = protectedSegments[n].Path;
                using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var extent = ReplayStorage.ObserveInventory(held.SafeFileHandle, input.Extent);
                if (extent.Identity != p.Extent.Identity || extent.Length != p.Extent.Length || extent.Volume != p.Extent.Volume)
                    throw new IOException("C00 immutable alias is no longer the proven source.");
                return p with { Path = path };
            }).ToArray();
            byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(mapped);
            if (bytes.Length > 524288) throw new IOException("C00 mapped digest metadata512KiB bound.");
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            output.Write(bytes); output.Flush(true);
        }
    }
    public void Dispose()
    {
        if (replayPreparationError != null)
        { RetainUntilControllerExit(this); throw new IOException("Failed paired alias preparation retains native owners until controller exit.", replayPreparationError); }
        Exception first = null;
        foreach (var stream in inputs) try { stream.Dispose(); } catch (Exception error) { first ??= error; }
        if (first == null) try { sourceRoot?.Dispose(); } catch (Exception error) { first = error; }
        if (first != null) { RetainUntilControllerExit(this); throw new IOException("Paired input closure failed.", first); }
    }
}
