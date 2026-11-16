using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// Actual collector cleanup seam, observer threads/metadata handle, trace writer,
// dummy producer and private durable claims. No capture or codec is started.
internal static class ReplayFailureObserverChecks
{
    sealed class FailFlush(Stream inner, ManualResetEventSlim failed) : Stream
    {
        int armed;
        internal void Arm() => Volatile.Write(ref armed, 1);
        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush()
        {
            if (Volatile.Read(ref armed) != 0) { failed.Set(); throw new IOException("controlled-trace-flush-failure"); }
            inner.Flush();
        }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => inner.Seek(o, s);
        public override void SetLength(long n) => inner.SetLength(n);
        public override void Write(byte[] b, int o, int c) => inner.Write(b, o, c);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static object Case(string root, string name)
    {
        string directory = Path.Combine(root, name), fixture = Path.Combine(directory, "fixture");
        string buffer = Path.Combine(fixture, "T01", "buffer"); Directory.CreateDirectory(buffer);
        string leaf = Path.Combine(buffer, "chunk-000000001.ts"); File.WriteAllBytes(leaf, new byte[4096]);
        string authority = Path.Combine(directory, "authority"), owner = Guid.NewGuid().ToString("N");
        var ledger = new ReplayVolumeReservations(authority); var files = new ReplayFiles();
        var capacity = ReplayStorage.ObserveCapacity(fixture);
        ledger.Update(owner, [new(Path.Combine(fixture, "future-growth"), fixture, capacity.Volume, capacity.AllocationUnit,
            4096, ReplayVolumeClaimKind.UnmaterializedGrowth)], files);
        bool ClaimHeld() => File.ReadAllText(Path.Combine(authority, "replay-volume-reservations.json")).Contains(owner, StringComparison.Ordinal);
        using var producerReady = new ManualResetEventSlim(); using var stopProducer = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var writerFailed = new ManualResetEventSlim(); FailFlush traceStream = null;
        Exception producerError = null; int armed = 0, injected = 0;
        var producer = new Thread(() => {
            try { using (new FileStream(leaf, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                { producerReady.Set(); Require(stopProducer.Wait(10000), "Bounded dummy stop request missing."); } }
            catch (Exception e) { producerError = e; producerReady.Set(); }
        }) { IsBackground = true, Name = "bounded-failure-producer" };
        var probe = new CalibrationObserverProbe { Opened = (handle, path) => {
            if (name != "writer-failure" && Volatile.Read(ref armed) == 1 && path == leaf && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            { entered.Set(); Require(release.Wait(5000), "Bounded observer block not released."); }
        } };
        var plan = new CalibrationPlan(fixture, "unused-no-engine", "nonmedia-observer-followup", new(), [], [], 0, 0,
            BudgetBytes: 2_000_000_000, StopBytes: 1_900_000_000, FloorBytes: 60_000_000_000,
            OutsideBuildAllowanceBytes: 0, DirectoryLimit: 64, FileLimit: 512);
        ReplayCalibrationRun.Sampler sampler = null; Thread releaser = null;
        string error = null, fixtureCleanupError = null; bool releasedClaim = false, heldAtDecision = false, observerAbsentAtDecision = false;
        bool writerReceiptAtDecision = false; JsonElement traceAtDecision = default;
        var clock = Stopwatch.StartNew(); producer.Start();
        try
        {
            Require(producerReady.Wait(1000) && producerError == null, "Dummy producer did not open its file.");
            sampler = new(plan, active: false, probe: probe,
                traceOpen: name == "writer-failure" ? () => traceStream = new FailFlush(new FileStream(Path.Combine(fixture, "storage.csv"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), writerFailed) : null);
            sampler.BeginActive(); Volatile.Write(ref armed, 1);
            if (name != "writer-failure") Require(entered.Wait(1500), "Actual metadata observer did not enter the controlled block.");
            else
            {
                Require(traceStream != null, "Real trace stream was not initialized."); traceStream.Arm();
                Require(writerFailed.Wait(1500), "Armed real trace flush failure was not observed.");
            }
            sampler.RequestCaptureStop("T01", () => { if (name != "producer-alive") stopProducer.Set(); });
            if (name != "producer-alive") Require(producer.Join(1000) && producerError == null, "Dummy producer absence not confirmed.");
            if (name == "draining")
            {
                releaser = new Thread(() => { Thread.Sleep(150); release.Set(); }) { IsBackground = true, Name = "bounded-observer-release" };
                releaser.Start();
            }
            if (name == "writer-failure")
            {
                var wait = Stopwatch.StartNew();
                while (!sampler.ObserverAbsent) { Require(wait.ElapsedMilliseconds < 2000, "Non-writer observers did not close."); Thread.Sleep(5); }
            }
            try { ReplayCalibrationRun.CompleteStoppedCaptureClaim(sampler, !producer.IsAlive, () => {
                ledger.Release(owner); releasedClaim = true;
            }, observerJoinMilliseconds: name == "observer-blocked" ? 100 : 5000); }
            catch (IOException e) { error = e.Message; }
            heldAtDecision = ClaimHeld(); observerAbsentAtDecision = sampler.ObserverAbsent;
            writerReceiptAtDecision = File.Exists(Path.Combine(fixture, "storage-writer.json"));
            traceAtDecision = JsonSerializer.SerializeToElement(sampler.TraceReceipt);
        }
        finally
        {
            stopProducer.Set(); release.Set();
            Require(producer.Join(2000), "Dummy producer cleanup unconfirmed.");
            if (releaser != null) Require(releaser.Join(1000), "Observer releaser cleanup unconfirmed.");
            try { sampler?.Dispose(); }
            catch (IOException e) { fixtureCleanupError = e.Message; }
        }
        bool passed = name == "draining" ? error == null && releasedClaim && !heldAtDecision && observerAbsentAtDecision && writerReceiptAtDecision
            && traceAtDecision.GetProperty("final").GetBoolean() && traceAtDecision.GetProperty("writerAbsent").GetBoolean()
            && traceAtDecision.GetProperty("failure").ValueKind == JsonValueKind.Null
            && traceAtDecision.GetProperty("enqueued").GetInt64() == traceAtDecision.GetProperty("written").GetInt64()
            : error != null && !releasedClaim && heldAtDecision;
        if (name == "writer-failure") passed &= writerReceiptAtDecision
            && traceAtDecision.GetProperty("final").GetBoolean() && traceAtDecision.GetProperty("writerAbsent").GetBoolean()
            && traceAtDecision.GetProperty("failure").GetString() == "controlled-trace-flush-failure"
            && traceAtDecision.GetProperty("enqueued").GetInt64() > traceAtDecision.GetProperty("written").GetInt64()
            && error?.Contains("controlled-trace-flush-failure", StringComparison.Ordinal) == true;
        else if (name != "draining") passed &= !writerReceiptAtDecision;
        if (name == "writer-failure") passed &= fixtureCleanupError?.Contains("controlled-trace-flush-failure", StringComparison.Ordinal) == true;
        else if (name == "observer-blocked") passed &= fixtureCleanupError?.Contains("Storage observer did not stop.", StringComparison.Ordinal) == true;
        else passed &= fixtureCleanupError == null;
        return new { name, passed, error, releasedClaim, heldAtDecision, observerAbsentAtDecision, writerReceiptAtDecision,
            traceAtDecision, fixtureCleanupError, producerAbsentAfterCleanup = !producer.IsAlive, observerAbsentAfterFixtureCleanup = sampler?.ObserverAbsent,
            claimStillHeldAfterFixtureCleanup = ClaimHeld(), seconds = clock.Elapsed.TotalSeconds,
            primaryFailure = "controlled-paired-selection-mismatch; never replaced by a successful qualification",
            scope = "No media or native producer; controlled real managed producer/file, actual sampler/writer/private claims. After-cleanup facts do not change the decision-point outcome." };
    }
    internal static int Run(string root)
    {
        Require(!Directory.Exists(root) && !File.Exists(root), "Preserve prior observer fixture."); Directory.CreateDirectory(root);
        var rows = new List<JsonElement>();
        foreach (string name in new[] { "draining", "producer-alive", "observer-blocked", "writer-failure" })
            rows.Add(JsonSerializer.SerializeToElement(Case(root, name)));
        bool passed = rows.All(r => r.GetProperty("passed").GetBoolean());
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed, rows, childProcesses = 0, captureStarted = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { passed, cases = rows.Count }));
        return passed ? 0 : 1;
    }
}
