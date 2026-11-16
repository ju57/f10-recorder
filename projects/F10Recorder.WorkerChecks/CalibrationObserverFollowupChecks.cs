using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

internal static class CalibrationObserverFollowupChecks
{
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static CalibrationPlan Plan(string root) => new(root, "unused-no-engine", "nonmedia-observer-followup", new(), [], [], 0, 0,
        BudgetBytes: 100_000_000, StopBytes: 90_000_000, FloorBytes: 60_000_000_000,
        OutsideBuildAllowanceBytes: 0, DirectoryLimit: 64, FileLimit: 512);
    // The sampler has a separate fixed closure/poll reserve. The fixture's
    // conservative plan covers that reserve without writing that amount.
    static CalibrationPlan FixturePlan(string root) => Plan(root) with { BudgetBytes = 2_000_000_000, StopBytes = 1_900_000_000 };
    static object WriteReport(string path, object value)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
        Require(data.Length <= 65536, "Fixture report exceeded64KiB.");
        File.WriteAllBytes(path, data); return value;
    }
    static object RunCost(string root, string fixture, int index, bool diagnostics)
    {
        string output = Path.Combine(root, "case-" + index); Directory.CreateDirectory(output);
        string observedFixture = Path.Combine(output, "fixture"); Directory.CreateDirectory(observedFixture);
        // Fresh per-case namespace and writer receipt, with the same immutable
        // tiny objects. Hardlinks add no duplicated dummy payload. Setup is
        // outside observer timing and inside the finite batch's whole cost.
        foreach (string source in Directory.GetFiles(fixture, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(observedFixture, Path.GetRelativePath(fixture, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            Require(Native.CreateHardLink(target, source, IntPtr.Zero), "Fixture hardlink creation failed.");
        }
        using var self = Process.GetCurrentProcess();
        Require(GetProcessIoCounters(self.Handle, out var beforeIo), "Own process I/O query failed.");
        var cpu = self.TotalProcessorTime; long allocation = GC.GetTotalAllocatedBytes(true);
        var whole = Stopwatch.StartNew(); var probe = new CalibrationObserverProbe { DiagnosticsEnabled = diagnostics };
        ReplayCalibrationRun.Sampler sampler = null;
        try
        {
            sampler = new(FixturePlan(observedFixture), active: false, traceName: "observer.csv",
                traceOpen: () => new FileStream(Path.Combine(output, "storage.csv"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), probe: probe);
            while (Volatile.Read(ref sampler.Observations) < 4)
            {
                sampler.Check(); Require(whole.Elapsed.TotalSeconds < 4, "Four-scan case exceeded4seconds."); Thread.Sleep(5);
            }
            sampler.StopAndCheck();
        }
        finally { sampler?.Dispose(); }
        // Serialization/report writes, writer drain, handle closure, and the
        // probe itself stay inside whole-case wall/CPU/allocation/I/O totals.
        WriteReport(Path.Combine(output, "observer.json"), new { diagnostics, scans = probe.Scans.Select(s => new { s.Milliseconds, s.AllocatedBytes }),
            sampler.MaximumGap, sampler.LastObservation, trace = sampler.TraceReceipt, metadata = sampler.MetadataOperationTimings,
            sampler.ObserverAbsent, sampler.Failure });
        self.Refresh(); Require(GetProcessIoCounters(self.Handle, out var afterIo), "Own process final I/O query failed.");
        return new { index, diagnostics, scans = probe.Scans.Count, wholeMs = whole.Elapsed.TotalMilliseconds,
            cpuMs = (self.TotalProcessorTime - cpu).TotalMilliseconds,
            managedAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocation,
            meanScanMs = probe.Scans.Average(s => s.Milliseconds), maximumScanMs = probe.Scans.Max(s => s.Milliseconds),
            scanManagedAllocatedBytes = probe.Scans.Sum(s => s.AllocatedBytes),
            readBytes = afterIo.ReadBytes - beforeIo.ReadBytes, writeBytes = afterIo.WriteBytes - beforeIo.WriteBytes,
            otherOperations = afterIo.OtherOperations - beforeIo.OtherOperations,
            sampler.ObserverAbsent, sampler.Failure, trace = sampler.TraceReceipt };
    }
    static object RunBlock(string root)
    {
        string directory = Path.Combine(root, "blocked"); Directory.CreateDirectory(directory);
        string fixture = Path.Combine(directory, "fixture"), buffer = Path.Combine(fixture, "T01", "buffer"); Directory.CreateDirectory(buffer);
        string leaf = Path.Combine(buffer, "chunk-000000001.ts"); File.WriteAllBytes(leaf, new byte[4096]);
        string authority = Path.Combine(directory, "private-ledger");
        var ledger = new ReplayVolumeReservations(authority); var files = new ReplayFiles();
        string owner = Guid.NewGuid().ToString("N"); var capacity = ReplayStorage.ObserveCapacity(fixture);
        ledger.Update(owner, [new(Path.Combine(fixture, "future-growth"), fixture, capacity.Volume, capacity.AllocationUnit,
            4096, ReplayVolumeClaimKind.UnmaterializedGrowth)], files);
        bool ClaimHeld() => File.ReadAllText(Path.Combine(authority, "replay-volume-reservations.json")).Contains(owner, StringComparison.Ordinal);
        using var producerReady = new ManualResetEventSlim(); using var requested = new ManualResetEventSlim();
        using var requestSeen = new ManualResetEventSlim(); using var drain = new ManualResetEventSlim(); using var absent = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Exception producerError = null; SafeFileHandle observedHandle = null; int armed = 0, injected = 0;
        var producer = new Thread(() =>
        {
            try
            {
                using (var held = new FileStream(leaf, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    producerReady.Set(); Require(requested.Wait(4000), "Dummy producer stop request missing.");
                    requestSeen.Set(); Require(drain.Wait(4000), "Dummy producer drain release missing.");
                }
                absent.Set();
            }
            catch (Exception ex) { producerError = ex; producerReady.Set(); }
        }) { IsBackground = true, Name = "bounded-nonmedia-producer" };
        var probe = new CalibrationObserverProbe { Opened = (handle, path) =>
        {
            if (Volatile.Read(ref armed) == 1 && path == leaf && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            { observedHandle = handle; entered.Set(); Require(release.Wait(3000), "Injected block exceeded3seconds."); }
        } };
        ReplayCalibrationRun.Sampler sampler = null; var whole = Stopwatch.StartNew();
        double requestMs = 0, failureMs = 0; bool heldBefore = false, heldAfterProducer = false, deniedWrite = false, gateHeld = false;
        string failure = null; bool releasedClaim = false;
        producer.Start();
        try
        {
            Require(producerReady.Wait(1000) && producerError == null, "Dummy producer did not acquire its lease.");
            sampler = new(FixturePlan(fixture), active: false, probe: probe);
            sampler.BeginActive(); Volatile.Write(ref armed, 1);
            Require(entered.Wait(1500), "Real inventory seam did not enter the bounded block.");
            var wait = Stopwatch.StartNew();
            while (failure == null)
            {
                try { sampler.Check(); }
                catch (IOException ex) { failure = ex.Message; }
                Require(wait.ElapsedMilliseconds < 1200, "Controller did not detect stale observation while Gate was held.");
                if (failure == null) Thread.Sleep(5);
            }
            failureMs = wait.Elapsed.TotalMilliseconds;
            Require(failure.Contains("500ms", StringComparison.Ordinal), "Unexpected blocked observer failure.");
            gateHeld = !Monitor.TryEnter(sampler.Gate);
            if (!gateHeld) Monitor.Exit(sampler.Gate);
            Require(gateHeld && !sampler.ObserverAbsent && observedHandle is { IsClosed: false }, "Injection did not hold the real observer lock/handle.");
            var request = Stopwatch.StartNew(); sampler.RequestCaptureStop("T01", requested.Set); requestMs = request.Elapsed.TotalMilliseconds;
            Require(requestMs < 100 && requestSeen.Wait(500), "Producer request waited for observer closure.");
            heldBefore = ClaimHeld() && !absent.IsSet && File.Exists(leaf);
            bool rejectedBefore = false;
            try { sampler.RequireFailureClosure(false); } catch (IOException) { rejectedBefore = true; }
            try { using var writer = new FileStream(leaf, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) { deniedWrite = true; }
            Require(heldBefore && deniedWrite && rejectedBefore, "Request prematurely released producer/file/reservation ownership.");
            drain.Set(); Require(absent.Wait(1000) && producer.Join(1000) && producerError == null, "Dummy producer closure not confirmed.");
            heldAfterProducer = ClaimHeld() && !sampler.ObserverAbsent && !observedHandle.IsClosed && File.Exists(leaf);
            bool rejectedAfterProducer = false;
            try { sampler.RequireFailureClosure(true); } catch (IOException) { rejectedAfterProducer = true; }
            Require(heldAfterProducer && rejectedAfterProducer, "Producer absence was treated as observer closure.");
            release.Set(); sampler.Dispose();
            Require(sampler.ObserverAbsent && observedHandle.IsClosed && sampler.Failure != null, "Failed observer did not close conservatively.");
            sampler.RequireFailureClosure(absent.IsSet);
            using (new FileStream(leaf, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            ledger.Release(owner); releasedClaim = !ClaimHeld(); Require(releasedClaim, "Final verified closure did not release the fixture claim.");
            return new { result = "passed", failure, controllerFailureDetectionMs = failureMs, requestMs, gateHeld,
                heldBefore, deniedWrite, rejectedBefore, heldAfterProducer, rejectedAfterProducer, producerAbsent = absent.IsSet, observerAbsent = sampler.ObserverAbsent,
                observedHandleClosed = observedHandle.IsClosed, releasedClaim, saved = false, wholeMs = whole.Elapsed.TotalMilliseconds,
                interpretation = "A bounded dummy producer, real native metadata handle and real private reservation. No recording or accepted save ran." };
        }
        finally
        {
            requested.Set(); drain.Set(); release.Set();
            Require(producer.Join(2000), "Dummy producer cleanup unconfirmed.");
            sampler?.Dispose();
            WriteReport(Path.Combine(directory, "closure.json"), new { producerAbsent = absent.IsSet, producerAlive = producer.IsAlive,
                producerError = producerError?.ToString(), observerAbsent = sampler?.ObserverAbsent,
                observedHandleClosed = observedHandle?.IsClosed, claimHeld = ClaimHeld(), releasedClaim, failure });
        }
    }
    internal static int Run(string root, bool reuseBlockedEvidence = false)
    {
        Require(root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root), "Fresh artifacts fixture required.");
        Directory.CreateDirectory(root); var whole = Stopwatch.StartNew(); var cases = new List<object>();
        var errors = new List<string>(); object blocked = null, receiptChecks = null;
        try
        {
            receiptChecks = BoundedCalibrationTraceChecks.PostCleanup();
            string fixture = Path.Combine(root, "fixture"); Directory.CreateDirectory(fixture);
            foreach (string label in new[] { "T01", "T02", "T03", "H02", "H01", "H03" })
            {
                string buffer = Path.Combine(fixture, label, "buffer"); Directory.CreateDirectory(buffer);
                for (int i = 0; i < 32; i++) File.WriteAllBytes(Path.Combine(buffer, $"chunk-{i:D9}.ts"), new byte[4096]);
                File.WriteAllText(Path.Combine(buffer, "segments.csv"), "dummy");
                string output = Path.Combine(fixture, label, "save.partial.mp4"); File.WriteAllBytes(output, new byte[4096]);
                Directory.CreateDirectory(output + ".work"); File.WriteAllBytes(Path.Combine(output + ".work", "assembly.mp4"), new byte[4096]);
            }
            for (int i = 0; i < 8; i++)
            {
                Require(whole.Elapsed.TotalSeconds < 30, "Eight-case measurement deadline exceeded.");
                bool diagnostics = i % 4 is 1 or 2; // off/on, on/off, repeated once
                try { cases.Add(RunCost(root, fixture, i, diagnostics)); }
                catch (Exception ex) { errors.Add("case" + i + ":" + ex); break; }
            }
            if (reuseBlockedEvidence)
            {
                string source = Path.GetFullPath("artifacts/issue83-observer-followup-20261109/result-01/result.json");
                string closure = Path.GetFullPath("artifacts/issue83-observer-followup-20261109/result-01/blocked/closure.json");
                Require(new FileInfo(source).Length <= 65536 && new FileInfo(closure).Length <= 65536, "Bounded prior block evidence required.");
                byte[] priorBytes = File.ReadAllBytes(source), closureBytes = File.ReadAllBytes(closure);
                using var prior = JsonDocument.Parse(priorBytes); using var closed = JsonDocument.Parse(closureBytes);
                var proof = prior.RootElement.GetProperty("blocked"); var state = closed.RootElement;
                Require(proof.GetProperty("result").GetString() == "passed" && proof.GetProperty("gateHeld").GetBoolean()
                    && proof.GetProperty("heldBefore").GetBoolean() && proof.GetProperty("heldAfterProducer").GetBoolean()
                    && proof.GetProperty("rejectedBefore").GetBoolean() && proof.GetProperty("rejectedAfterProducer").GetBoolean()
                    && state.GetProperty("producerAbsent").GetBoolean() && !state.GetProperty("producerAlive").GetBoolean()
                    && state.GetProperty("observerAbsent").GetBoolean() && state.GetProperty("observedHandleClosed").GetBoolean()
                    && !state.GetProperty("claimHeld").GetBoolean() && state.GetProperty("releasedClaim").GetBoolean(), "Prior scoped block evidence is incomplete.");
                blocked = new { result = "reused-scoped-pass", source, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(priorBytes)),
                    closure, closureSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(closureBytes)),
                    priorOverallResult = prior.RootElement.GetProperty("result").GetString(), proof = proof.Clone(), state = state.Clone() };
            }
            else try { blocked = RunBlock(root); } catch (Exception ex) { errors.Add("blocked:" + ex); }
            try { ReplayCalibrationRun.SamplingGap(0, 574.2387); errors.Add("Historical574ms pattern was accepted."); } catch (IOException) { }
        }
        catch (Exception ex) { errors.Add(ex.ToString()); }
        WriteReport(Path.Combine(root, "result.json"), new { result = errors.Count == 0 ? "passed" : "failed", errors, cases, blocked,
            receiptChecks, seconds = whole.Elapsed.TotalSeconds, policyMilliseconds = 500, accounting = "experimental partition unchanged",
            limits = "eight4scan native cases;one3smax blocked call;bounded dummy producer threads;2MiB dummy payload;no child/capture/engine/audio/UI/catalog",
            interpretation = "Stopped tiny-fixture diagnostics-overhead comparison, not live contention or Saved latency. Whole cases include startup/cadence/closure/trace/report; per-scan costs stop before probe bookkeeping/final guards." });
        Console.WriteLine($"Observer follow-up: {cases.Count} cases;{errors.Count} failures;{root}"); return errors.Count == 0 ? 0 : 1;
    }
}
