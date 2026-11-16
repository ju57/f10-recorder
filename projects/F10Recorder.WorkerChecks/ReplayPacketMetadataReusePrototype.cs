using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using F10Recorder;

// File-only diagnostic. No shipping exporter, catalog, storage evidence or Saved path is changed.
internal static class ReplayPacketMetadataReusePrototype
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    static readonly List<object> observations = [];
    static readonly List<object> attempts = [];
    static readonly List<string> controls = [];
    static Stopwatch whole;
    static long maximumMemory;
    static int opened, closed;
    static string policy;
    static string diagnosticsRoot;
    sealed record Source(string Path, string Origin, string Session, string Sha256, ReplayFileExtent Extent);
    sealed record Key(string Session, ReplayFileExtent Extent, string Policy);
    sealed class Entry(ReplayTransportIndex index, long allowance)
    {
        internal readonly ReplayTransportIndex Index = Clone(index);
        internal readonly long Allowance = allowance;
        internal int References;
    }
    sealed class Borrow(Cache owner, Entry entry) : IDisposable
    {
        internal readonly ReplayTransportIndex Index = Clone(entry.Index);
        bool disposed;
        public void Dispose() { lock (owner.Gate) { if (disposed) return; disposed = true; entry.References--; owner.Outstanding--; } }
    }
    sealed class Cache
    {
        internal readonly object Gate = new();
        readonly Dictionary<Key, Entry> entries = [];
        internal int Outstanding, Hits, Misses;
        internal long Allowance;
        bool retired;
        internal int Count { get { lock (Gate) return entries.Count; } }
        internal void Add(Key key, ReplayTransportIndex index)
        {
            lock (Gate)
            {
                Require(!retired, "Retired session cannot publish metadata.");
                if (entries.ContainsKey(key)) return;
                long bytes = checked(8192 + index.Pictures.Count * 640L + index.Audio.Count * 256L
                    + index.Timing.Count * 128L + (index.AudioFrames?.Count ?? 0) * 256L + index.Sps.Length + index.Pps.Length);
                Require(entries.Count < 301 && Allowance + bytes <= 64L * 1024 * 1024, "Metadata allowance bound.");
                entries.Add(key, new(index, bytes)); Allowance += bytes;
            }
        }
        internal Borrow Find(Key key)
        {
            lock (Gate)
            {
                Require(!retired, "Retired session cannot acquire metadata.");
                if (!entries.TryGetValue(key, out var entry)) { Misses++; return null; }
                Hits++; entry.References++; Outstanding++;
                try { return new(this, entry); }
                catch { entry.References--; Outstanding--; throw; }
            }
        }
        internal void CopyMiss() { lock (Gate) { Require(!retired, "Retired session cannot acquire metadata."); Misses++; } }
        internal void Evict(Key key) { lock (Gate) { if (entries.Remove(key, out var e)) Allowance -= e.Allowance; } }
        internal void Retire() { lock (Gate) { retired = true; entries.Clear(); Allowance = 0; } }
    }
    static ReplayTransportIndex Clone(ReplayTransportIndex x) => new(x.Bytes, x.Sha256,
        Array.AsReadOnly(x.Pictures.ToArray()), Array.AsReadOnly(x.Audio.ToArray()), Array.AsReadOnly(x.Timing.ToArray()),
        x.Sps.ToArray(), x.Pps.ToArray(), x.AudioFrames == null ? null : Array.AsReadOnly(x.AudioFrames.ToArray()));
    static Key Identity(Source s) => new(s.Session, s.Extent, policy);
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static void Guard()
    {
        using var p = Process.GetCurrentProcess(); p.Refresh();
        Require(whole.Elapsed.TotalSeconds < 75 && p.WorkingSet64 <= maximumMemory && p.HandleCount < 4096, "Prototype time/memory/handle bound.");
    }
    sealed class Counted(FileStream file) : Stream
    {
        internal long Bytes;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => file.Length;
        public override long Position { get => file.Position; set => file.Position = value; }
        public override int Read(byte[] b, int o, int c) { int n = file.Read(b, o, c); Bytes += n; return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken t = default)
        { int n = await file.ReadAsync(b, t).ConfigureAwait(false); Bytes += n; return n; }
        public override long Seek(long n, SeekOrigin o) => file.Seek(n, o);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
    sealed class Lease : IDisposable
    {
        internal readonly FileStream File;
        internal readonly Counted Stream;
        internal readonly bool SameObjectSnapshot;
        bool disposed;
        internal Lease(Source source, bool origin)
        {
            string path = origin ? source.Origin : source.Path;
            Require((System.IO.File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "Regular input required.");
            File = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
            opened++;
            try
            {
                Require(ReplayStorage.ObserveInventory(File.SafeFileHandle, source.Extent) == source.Extent, "Held input object/extent changed.");
                SameObjectSnapshot = origin || source.Path != source.Origin && ReplayStorage.Observe(source.Origin) == source.Extent;
                Stream = new(File);
            }
            catch { File.Dispose(); closed++; throw; }
        }
        public void Dispose() { if (disposed) return; disposed = true; var handle = File.SafeFileHandle; File.Dispose(); Require(handle.IsClosed, "Reader handle closure uncertain."); closed++; }
    }
    sealed class Acquisition(Borrow borrow, ReplayTransportIndex parsed, bool hit, double lookupMs, double parseMs, long readBytes) : IDisposable
    {
        internal readonly ReplayTransportIndex Index = borrow?.Index ?? parsed;
        internal readonly bool Hit = hit;
        internal readonly object Cost = new { hit, lookupAndCloneMs = lookupMs, parseAndStoreMs = parseMs, readBytes };
        public void Dispose() => borrow?.Dispose();
    }
    static (ReplayTransportIndex[] Indices, object Timing, List<Borrow> Borrows) Acquire(Source[] sources, Lease[] leases, Cache cache, bool hits)
    {
        if (!hits)
        {
            var timing = new ReplayTransportBatchTiming(); var lifetime = new ReplayWorkerLifetime();
            var indices = ReplayTransportBatchIndex.Read(leases.Select(l => new ReplayTransportSource(l.Stream, l.Stream.Length)).ToArray(),
                60, true, 4, Guard, timing, true, TimeSpan.FromSeconds(30), lifetime);
            Require(lifetime.Started && lifetime.Joined && timing.Joined, "Actual index readers did not join.");
            return (indices, timing, []);
        }
        var acquired = new ReplayTransportIndex[sources.Length]; var held = new List<Borrow>(); var perSource = new List<object>();
        var work = new ReplayPacketWorkTiming(); var acquisitionLife = new ReplayWorkerLifetime(); int ordinal = 0;
        try
        {
            ReplayPacketWork.Run(Enumerable.Range(0, sources.Length).ToArray(), 4, 16777216,
                (n, context) =>
                {
                    context.Check(); long at = Stopwatch.GetTimestamp(); Borrow borrow = null;
                    if (leases[n].SameObjectSnapshot) borrow = cache.Find(Identity(sources[n])); else cache.CopyMiss();
                    double lookup = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    if (borrow != null) return new Acquisition(borrow, null, true, lookup, 0, 0);
                    // Actual parser miss; cancellable I/O through this worker's token.
                    using var access = new CancelRead(leases[n].Stream, context.Cancellation);
                    at = Stopwatch.GetTimestamp(); long bytesBefore = leases[n].Stream.Bytes;
                    var index = ReplayTransportReader.Read(access, leases[n].Stream.Length, 60, true, context.Check);
                    Require(index.Sha256 == sources[n].Sha256, "Miss bytes differ from admitted source.");
                    if (leases[n].SameObjectSnapshot) cache.Add(Identity(sources[n]), index);
                    return new Acquisition(null, index, false, lookup, Stopwatch.GetElapsedTime(at).TotalMilliseconds, leases[n].Stream.Bytes - bytesBefore);
                }, a => 8192 + a.Index.Pictures.Count * 640L + a.Index.Audio.Count * 256L + a.Index.Timing.Count * 128L + (a.Index.AudioFrames?.Count ?? 0) * 256L,
                (a, pump) => { pump(); acquired[ordinal++] = Clone(a.Index); perSource.Add(a.Cost); }, Guard, work, TimeSpan.FromSeconds(30), acquisitionLife);
            Require(acquisitionLife.Started && acquisitionLife.Joined && work.Joined && ordinal == sources.Length, "Metadata acquisition did not honestly drain.");
            return (acquired, new { work, perSource }, held);
        }
        catch { foreach (var b in held) b.Dispose(); throw; }
    }
    sealed class CancelRead(Counted stream, CancellationToken token) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => stream.Length; public override long Position { get => stream.Position; set => stream.Position = value; }
        public override int Read(byte[] b, int o, int c) { var v = stream.ReadAsync(b.AsMemory(o, c), token); return v.IsCompleted ? v.GetAwaiter().GetResult() : v.AsTask().GetAwaiter().GetResult(); }
        public override long Seek(long n, SeekOrigin o) => stream.Seek(n, o);
        public override void Flush() => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
    static string Canonical(ReplayTransportIndex[] indices) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        indices.Select(x => new { x.Bytes, x.Sha256, x.Pictures, x.Audio, x.Timing, Sps = Convert.ToHexString(x.Sps.Span), Pps = Convert.ToHexString(x.Pps.Span), x.AudioFrames }))));
    static object FreshFallback(Source[] sources, Lease[] leases)
    {
        foreach (var l in leases) l.Stream.Position = 0;
        var timing = new ReplayTransportBatchTiming(); var lifetime = new ReplayWorkerLifetime(); bool unsupported = false;
        try
        {
            ReplayTransportBatchIndex.Read(leases.Select(l => new ReplayTransportSource(l.Stream, l.Stream.Length)).ToArray(),
                60, true, 4, Guard, timing, true, TimeSpan.FromSeconds(30), lifetime);
        }
        catch (NotSupportedException) { unsupported = true; }
        Require(lifetime.Started && lifetime.Joined && timing.Joined && timing.FinishedSources == sources.Length,
            "Fallback must freshly parse every accepted source and join.");
        // An unsupported parser may stop before EOF. Identity/digest history is
        // not current-byte proof: every source is read again before eligibility.
        for (int n = 0; n < sources.Length; n++)
        {
            Guard(); leases[n].Stream.Position = 0;
            Require(Convert.ToHexString(SHA256.HashData(leases[n].Stream)) == sources[n].Sha256, "Current source corruption defeats exact fallback.");
        }
        return new { exactFallbackEligible = true, unsupported, timing,
            meaning = "Eligibility/byte-verification diagnostic only; no decoder/exporter executed." };
    }
    static (string Digest, object Timing, long Capacity) Extract(Lease[] leases, ReplayTransportIndex[] indices, bool omitLast = false)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var workers = Enumerable.Range(0, Math.Min(4, leases.Length)).Select(_ => new ReplayPacketPrepareWorker()).ToArray();
        var timing = new ReplayPacketWorkTiming(); var life = new ReplayWorkerLifetime(); int ordinal = 0;
        var jobs = leases.Select((l, n) => { l.Stream.Position = 0; return new ReplayPacketPrepareInput(l.Stream, indices[n], !omitLast || n != leases.Length - 1, !omitLast || n != leases.Length - 1); }).ToArray();
        try
        {
            Require(indices.All(ReplayPreparedPackets.Eligible), "Do not disable bounded extraction for a cache hit.");
            ReplayPacketWork.Run(jobs, workers.Length, 16777216, (j, c) => workers[c.Worker].Prepare(j, c), b => b.CapacityBytes,
                (body, pump) =>
                {
                    pump(); Require(body.SourceHashComplete && body.SourceBytes == indices[ordinal++].Bytes, "Accepted source omitted from fresh full rehash.");
                    for (int n = 0; n < body.Count; n++) { var item = body.Event(n); digest.AppendData(body.Payload(item)); }
                }, Guard, timing, TimeSpan.FromSeconds(30), life);
            Require(life.Started && life.Joined && timing.Joined && ordinal == leases.Length, "Packet readers escaped.");
            return (Convert.ToHexString(digest.GetHashAndReset()), new { work = timing, extraction = workers.Select(w => w.Extraction).ToArray() }, workers.Sum(w => w.PeakWorkspaceBytes));
        }
        finally { foreach (var worker in workers) worker.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessIoCounters(IntPtr process, out Io io);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateHardLink(string link, string existing, IntPtr security);
    static void LinkOrThrow(string destination, string source)
    {
        bool success = CreateHardLink(destination, source, IntPtr.Zero);
        int error = success ? 0 : Marshal.GetLastPInvokeError(); // First call after failure; save before any context query.
        string message = success ? "Success" : new Win32Exception(error).Message;
        string log = Path.Combine(diagnosticsRoot, "hardlink-attempts.jsonl");
        File.AppendAllText(log, JsonSerializer.Serialize(new { kind = "native-return", success, error, message,
            source = Path.GetFullPath(source), destination = Path.GetFullPath(destination) }) + Environment.NewLine);
        try
        {
        using var process = Process.GetCurrentProcess();
        var context = new { kind = "context", success, error, message, source = Path.GetFullPath(source), destination = Path.GetFullPath(destination),
            pid = Environment.ProcessId, processBirthUtc = process.StartTime.ToUniversalTime(), processPath = Environment.ProcessPath,
            runtime = Environment.Version.ToString(), os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            currentDirectory = Environment.CurrentDirectory, account = System.Security.Principal.WindowsIdentity.GetCurrent().Name,
            sourceExists = File.Exists(source), destinationExists = File.Exists(destination),
            sourceAttributes = File.Exists(source) ? File.GetAttributes(source).ToString() : null,
            fileSystem = new DriveInfo(Path.GetPathRoot(source)).DriveFormat, freeBytes = new DriveInfo(Path.GetPathRoot(source)).AvailableFreeSpace };
        File.AppendAllText(log, JsonSerializer.Serialize(context) + Environment.NewLine);
        }
        catch (Exception contextError) { File.AppendAllText(log, JsonSerializer.Serialize(new { kind = "context-error", error, contextError.Message }) + Environment.NewLine); }
        if (!success) throw new IOException($"CreateHardLink failed: Win32 {error} ({message}); source={source}; destination={destination}", new Win32Exception(error));
    }
    sealed record Point(long Qpc, double CpuSeconds, long Allocated, int G0, int G1, int G2, long Managed, long Working, long Private, long LifetimePeakWorking, int Handles, Io Io);
    static Point Mark()
    {
        using var p = Process.GetCurrentProcess(); p.Refresh(); Require(GetProcessIoCounters(p.Handle, out var io), "Logical process I/O unavailable.");
        return new(Stopwatch.GetTimestamp(), p.TotalProcessorTime.TotalSeconds, GC.GetTotalAllocatedBytes(true), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            GC.GetTotalMemory(false), p.WorkingSet64, p.PrivateMemorySize64, p.PeakWorkingSet64, p.HandleCount, io);
    }
    static object Delta(Point a, Point b) => new { wallMs = Stopwatch.GetElapsedTime(a.Qpc, b.Qpc).TotalMilliseconds,
        cpuSeconds = b.CpuSeconds - a.CpuSeconds, allocatedBytes = b.Allocated - a.Allocated, gc = new[] { b.G0 - a.G0, b.G1 - a.G1, b.G2 - a.G2 },
        logicalReadBytes = b.Io.ReadBytes - a.Io.ReadBytes, logicalWriteBytes = b.Io.WriteBytes - a.Io.WriteBytes,
        before = a, after = b, physicalIo = "Unavailable; process counters include cache and other logical I/O." };
    static object Build(Source[] sources, Cache cache, Action injected = null)
    {
        var before = Mark(); var leases = new List<Lease>(); var timing = new ReplayTransportBatchTiming();
        long bytes = 0; string failure = null;
        try
        {
            foreach (var s in sources) { Guard(); leases.Add(new(s, true)); }
            var indices = ReplayTransportBatchIndex.Read(leases.Select(l => new ReplayTransportSource(l.Stream, l.Stream.Length)).ToArray(), 60, true, 4,
                () => { Guard(); injected?.Invoke(); }, timing, true, TimeSpan.FromSeconds(30));
            for (int n = 0; n < sources.Length; n++) Require(indices[n].Sha256 == sources[n].Sha256, "Build bytes differ from admitted source.");
            // Atomic at this diagnostic's builder boundary: nothing is published if parsing/cancellation failed.
            for (int n = 0; n < sources.Length; n++) cache.Add(Identity(sources[n]), indices[n]);
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally { foreach (var l in leases) { bytes += l.Stream.Bytes; l.Dispose(); }
            attempts.Add(new { kind = "build", inputs = sources.Length, failure, cost = Delta(before, Mark()), sourceReadBytes = bytes, timing, opened, closed }); }
        return new { phase = "metadata-build", cost = Delta(before, Mark()), sourceReadBytes = bytes, timing, entries = cache.Count, cache.Allowance };
    }
    static object Request(string label, Source[] sources, Cache cache, bool hits, bool omitLast = false)
    {
        var before = Mark(); var leases = new List<Lease>(); long bytes = 0; int hit0 = cache.Hits, miss0 = cache.Misses;
        object acquisition = null, extraction = null; string canonical = null, payload = null, failure = null; double acquireMs = 0, extractMs = 0, cleanupMs; bool fallback = false;
        try
        {
            foreach (var s in sources) { Guard(); leases.Add(new(s, false)); }
            var at = Stopwatch.GetTimestamp();
            try
            {
                var acquired = Acquire(sources, leases.ToArray(), cache, hits); acquisition = acquired.Timing;
                try
                {
                    for (int n = 0; n < sources.Length; n++) Require(acquired.Indices[n].Sha256 == sources[n].Sha256, "Acquired metadata differs from admitted source.");
                    canonical = Canonical(acquired.Indices); acquireMs = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    at = Stopwatch.GetTimestamp(); var result = Extract(leases.ToArray(), acquired.Indices, omitLast);
                    payload = result.Digest; extraction = new { result.Timing, peakSumWorkspaceBytes = result.Capacity }; extractMs = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                }
                finally { foreach (var b in acquired.Borrows) b.Dispose(); }
            }
            catch (NotSupportedException)
            {
                acquisition = FreshFallback(sources, leases.ToArray()); fallback = true; acquireMs = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
            }
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            var at = Stopwatch.GetTimestamp(); foreach (var l in leases) { bytes += l.Stream.Bytes; l.Dispose(); }
            cleanupMs = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
            Require(cache.Outstanding == 0 && opened == closed, "Request references/readers remain owned.");
            attempts.Add(new { kind = label, failure, fallback, cost = Delta(before, Mark()), sourceReadBytes = bytes, acquisition, extraction, canonical, payload, opened, closed });
        }
        return new { phase = label, fallback, cost = Delta(before, Mark()), acquisitionMs = acquireMs, extractionMs = extractMs, cleanupMs,
            sourceReadBytes = bytes, hits = cache.Hits - hit0, misses = cache.Misses - miss0, canonical, payload, acquisition, extraction, opened, closed };
    }
    static Exception Fail(Action action)
    { try { action(); } catch (Exception ex) { attempts.Add(new { kind = "expected-negative", type = ex.GetType().Name, ex.Message }); return ex; } throw new IOException("Expected negative control did not fail."); }
    static Source Tiny(string path, byte[] bytes, string session = "tiny")
    { File.WriteAllBytes(path, bytes); string snapshot = path + ".snapshot"; LinkOrThrow(snapshot, path);
        return new(snapshot, path, session, Convert.ToHexString(SHA256.HashData(bytes)), ReplayStorage.Observe(path)); }
    static void Adversarial(string root)
    {
        Directory.CreateDirectory(root); byte[] good = ReplayTransportChecks.Fixture();
        Source Fresh(string name, byte[] bytes) => Tiny(Path.Combine(root, name), bytes);
        var s = Fresh("changed.ts", good); var cache = new Cache(); Build([s], cache);
        using (var borrow = cache.Find(Identity(s)))
        {
            Require(MemoryMarshal.TryGetArray(borrow.Index.Sps, out var a), "Mutation control backing not exposed."); a.Array[a.Offset] ^= 1;
            using var second = cache.Find(Identity(s)); Require(!borrow.Index.Sps.Span.SequenceEqual(second.Index.Sps.Span), "Cache SPS aliases request backing.");
            Require(second.Index.Pictures is not ReplayTransportPicture[], "Request collection still exposes mutable array.");
        }
        controls.Add("deep-owned metadata and mutation-isolated request clone");
        Require(cache.Find(Identity(s) with { Session = "other-session" }) == null && cache.Find(Identity(s) with { Policy = "other-parser-clock" }) == null,
            "Session/parser policy key crossed."); controls.Add("session and codec/clock/parser-policy mismatch cannot hit");
        byte[] changed = good.ToArray(); changed[^1] ^= 1; File.WriteAllBytes(s.Path, changed);
        Require(Fail(() => Request("changed-byte-hit", [s], cache, true)) is IOException, "Changed-byte hit accepted.");
        controls.Add("same-object/same-size changed bytes rejected by fresh extraction hash"); cache.Retire();
        var replace = Fresh("replacement.ts", good); cache = new(); Build([replace], cache);
        File.Move(Fresh("new-object.ts", good).Path, replace.Path, true); var replaced = replace with { Extent = ReplayStorage.Observe(replace.Path) };
        Require(replaced.Extent.Identity != replace.Extent.Identity, "Replacement object did not change.");
        Request("replacement-miss", [replaced], cache, true); Require(cache.Misses == 1, "Same-size replacement incorrectly hit."); cache.Retire();
        controls.Add("same-size native object replacement takes fresh parse miss");
        var copied = Fresh("copy.ts", good); cache = new(); Build([copied], cache);
        File.Delete(copied.Path); File.Copy(copied.Origin, copied.Path); copied = copied with { Extent = ReplayStorage.Observe(copied.Path) };
        Request("copy-miss-1", [copied], cache, true); Request("copy-miss-2", [copied], cache, true);
        Require(cache.Hits == 0 && cache.Misses == 2 && cache.Count == 1, "Copy incorrectly became eligible for reuse."); cache.Retire();
        controls.Add("copies freshly parse on every request and never populate a reusable snapshot entry");
        var omit = Fresh("omitted.ts", good); cache = new(); Build([omit], cache); File.WriteAllBytes(omit.Path, changed);
        Require(Fail(() => Request("omitted-corrupt", [omit], cache, true, true)) is IOException, "Omitted-input corruption accepted."); cache.Retire();
        controls.Add("omitted-output input still fully rehashed and corruption rejected");
        var unsupported = Fresh("unsupported.ts", ReplayTransportChecks.Fixture(videoGap: true)); var okay = Fresh("fallback-peer.ts", good);
        cache = new(); Build([okay], cache);
        foreach (bool corrupt in new[] { false, true })
        {
            if (corrupt) { changed = good.ToArray(); changed[0] = 0; File.WriteAllBytes(okay.Path, changed); }
            if (corrupt) Require(Fail(() => Request("unsupported-corrupt-peer", [unsupported, okay], cache, true)) is IOException,
                "Unsupported result hid source corruption.");
            else Require(JsonSerializer.SerializeToElement(Request("unsupported-fresh-fallback", [unsupported, okay], cache, true), Json)
                .GetProperty("fallback").GetBoolean(), "Unsupported cache miss did not retain fresh fallback.");
        }
        cache.Retire();
        controls.Add("fresh unsupported fallback parses all inputs; corruption wins before fallback; full bytes verified");
        var cancel = Fresh("cancel.ts", good); cache = new(); int checks = 0;
        Require(Fail(() => Build([cancel], cache, () => { if (++checks == 2) throw new OperationCanceledException("Injected builder cancellation."); })) is OperationCanceledException
            && cache.Count == 0 && opened == closed, "Canceled builder published metadata or escaped a reader."); cache.Retire();
        controls.Add("builder cancellation drains readers, closes handles and publishes no entry");
        var prune = Fresh("prune.ts", good); cache = new(); Build([prune], cache); var held = cache.Find(Identity(prune));
        File.Delete(prune.Origin); File.Delete(prune.Path); cache.Evict(Identity(prune)); Require(cache.Count == 0 && cache.Outstanding == 1, "Eviction forged reference release.");
        cache.Retire(); Require(Fail(() => cache.Find(Identity(prune))) is IOException && cache.Outstanding == 1, "Retirement admitted borrow or hid live reference.");
        held.Dispose(); held.Dispose(); Require(cache.Outstanding == 0 && cache.Allowance == 0 && opened == closed, "Metadata owner did not release.");
        controls.Add("pruning unblocked after build; eviction/retirement deny borrows and preserve outstanding reference until release");
    }
    internal static int Run(string planPath, string expectedHash)
    {
        var processEntry = Mark();
        byte[] planBytes = File.ReadAllBytes(planPath); Require(Convert.ToHexString(SHA256.HashData(planBytes)) == expectedHash, "Plan changed.");
        using var doc = JsonDocument.Parse(planBytes); var plan = doc.RootElement;
        Require(plan.GetProperty("kind").GetString() == "packet-metadata-reuse-file-only-v1", "Wrong diagnostic route.");
        Require(Environment.Version.ToString() == plan.GetProperty("runtimeVersion").GetString(), "Resolved runtime changed.");
        foreach (var f in plan.GetProperty("files").EnumerateObject()) Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.Name))) == f.Value.GetString(), "Changed helper/runtime/source identity: " + f.Name);
        string output = plan.GetProperty("output").GetString(); Require(!Directory.Exists(output), "Preserve prior observation."); Directory.CreateDirectory(output); diagnosticsRoot = output;
        var sources = plan.GetProperty("sources").Deserialize<Source[]>(Json); Require(sources.Length == 301 && sources.Sum(s => s.Extent.Length) <= 1000000000, "Cohort bound.");
        policy = plan.GetProperty("policy").GetString(); maximumMemory = plan.GetProperty("bounds").GetProperty("child_memory_bytes").GetInt64(); whole = Stopwatch.StartNew();
        var cache = new Cache(); string error = null; object retirement = null; long heap0 = GC.GetTotalMemory(true), heapBuilt = 0, heapReleased = 0;
        try
        {
            Adversarial(Path.Combine(output, "tiny"));
            heap0 = GC.GetTotalMemory(true);
            observations.Add(Build(sources[..^1], cache)); heapBuilt = GC.GetTotalMemory(true);
            foreach (var (label, hit) in new[] { ("control-1", false), ("hit-1-newest-miss", true), ("hit-2", true), ("control-2", false) })
            {
                var row = Request(label, sources, cache, hit); observations.Add(row);
                File.WriteAllText(Path.Combine(output, label + ".json"), JsonSerializer.Serialize(row, Json));
            }
            var rows = observations.Skip(1).Select(o => JsonSerializer.SerializeToElement(o, Json)).ToArray();
            Require(rows.All(r => r.GetProperty("canonical").GetString() == rows[0].GetProperty("canonical").GetString()
                && r.GetProperty("payload").GetString() == rows[0].GetProperty("payload").GetString()), "Source metadata or extracted payload mismatch.");
            Require(rows[1].GetProperty("hits").GetInt32() == 300 && rows[1].GetProperty("misses").GetInt32() == 1
                && rows[2].GetProperty("hits").GetInt32() == 301 && rows[2].GetProperty("misses").GetInt32() == 0, "Newest miss/hit pattern changed.");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            var before = Mark(); int entries = cache.Count; long allowance = cache.Allowance; cache.Retire(); heapReleased = GC.GetTotalMemory(true);
            retirement = new { cost = Delta(before, Mark()), entriesBefore = entries, allowanceBefore = allowance, cache.Outstanding, cache.Allowance, entriesAfter = cache.Count };
            if (opened != closed || cache.Outstanding != 0) error ??= "Reader/reference closure uncertain.";
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { expectedHash, status = error == null ? "passed" : "failed", error,
                controls, observations, attempts, retirement, opened, closed, seconds = whole.Elapsed.TotalSeconds, processThroughReport = Delta(processEntry, Mark()), heap0, heapBuilt, heapReleased,
                heapMeaning = "Forced-GC process managed-heap bookends; includes pool/runtime noise, not exact cache graph size; forced collections outside requests.",
                pipeline = "Real bounded packet preparation used in every request. Cache acquisition is not shipping IndexBatch eligibility. No final mux/publication/Saved measured." }, Json));
        }
        Console.WriteLine(error == null ? "Passed finite metadata-reuse diagnostic." : error); return error == null ? 0 : 1;
    }
    internal static int SetupOnly(string planPath, string expectedHash)
    {
        byte[] bytes = File.ReadAllBytes(planPath); Require(Convert.ToHexString(SHA256.HashData(bytes)) == expectedHash, "Setup plan changed.");
        using var doc = JsonDocument.Parse(bytes); var plan = doc.RootElement;
        Require(plan.GetProperty("kind").GetString() == "packet-metadata-hardlink-setup-v1", "Wrong setup route.");
        Require(Environment.Version.ToString() == plan.GetProperty("runtimeVersion").GetString(), "Resolved setup runtime changed.");
        foreach (var f in plan.GetProperty("files").EnumerateObject()) Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.Name))) == f.Value.GetString(), "Changed setup identity: " + f.Name);
        string output = plan.GetProperty("output").GetString(); Require(!Directory.Exists(output), "Preserve setup attempt.");
        Directory.CreateDirectory(output); diagnosticsRoot = output; var at = Stopwatch.GetTimestamp(); string failure = null; bool sameObject = false, removed = false;
        try
        {
            var source = Tiny(Path.Combine(output, "changed.ts"), ReplayTransportChecks.Fixture());
            sameObject = ReplayStorage.Observe(source.Path) == ReplayStorage.Observe(source.Origin);
            Require(sameObject, "Tiny hardlink did not preserve native object/extent.");
            using (var lease = new Lease(source, false)) Require(lease.SameObjectSnapshot, "Tiny protected snapshot eligibility failed.");
            File.Delete(source.Path); File.Delete(source.Origin); removed = !File.Exists(source.Path) && !File.Exists(source.Origin);
            Require(removed && opened == closed, "Tiny setup cleanup/closure uncertain.");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { kind = "setup-only", expectedHash, failure, sameObject, removed,
            opened, closed, milliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds, noRetainedInputs = true }, Json));
        Console.WriteLine(failure ?? "Passed setup-only native hardlink/identity/lease/cleanup check."); return failure == null ? 0 : 1;
    }
}
