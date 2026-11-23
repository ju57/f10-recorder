using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static class ReplayCapacityMonitoringChecks
{
    const string Volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";
    static readonly string Scope = new('A', 64);
    static ReplayCalibrationAvailability Qualification()
    {
        var headroom = new ReplayCaptureHeadroom(Scope, Volume, 4096, 65536, 32768, ReplayCapacityPolicy.CoverageMilliseconds);
        return new(new("controlled-two-second-evidence", Scope, Volume, 4096, 0, 0, [], headroom), null);
    }
    sealed class Files : ReplayFiles
    {
        internal long Free = 9_000_000_000;
        internal int Calls;
        internal Action BeforeCapacity;
        internal override ReplayVolumeCapacity ObserveCapacity(string path)
        { Interlocked.Increment(ref Calls); BeforeCapacity?.Invoke(); return new(Volume, 4096, Interlocked.Read(ref Free)); }
    }
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    static void Wait(Func<bool> condition, int milliseconds, string reason)
    { Require(SpinWait.SpinUntil(condition, milliseconds), reason); }
    internal static int Run(string root)
    {
        Require(!Directory.Exists(root) && !File.Exists(root), "Monitoring checks need a fresh private root.");
        Directory.CreateDirectory(root);
        var rows = new List<object>(); int failures = 0;
        void Check(string name, Action<string> body)
        {
            string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            try { body(directory); rows.Add(new { name, passed = true }); }
            catch (Exception error) { failures++; rows.Add(new { name, passed = false, error = error.ToString() }); }
        }
        Check("609ms-capacity-delay-with-qualified-cover", directory =>
        {
            var files = new Files(); string security = Path.Combine(directory, "security");
            var latch = new ReplayStorageStopLatch(directory, security);
            using var coordinator = new ReplayStorageCoordinator(latch, new(security), files, Qualification, (_, _) => true);
            bool stopped = false; coordinator.BeginCapture(Scope, directory, directory, () => stopped = true);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            files.BeforeCapacity = () => { entered.Set(); Require(release.Wait(3000), "Delayed capacity query was not released."); };
            int before = files.Calls;
            coordinator.Poll(); Require(entered.Wait(1000), "Capacity query never entered.");
            var delay = Stopwatch.StartNew();
            try
            {
                while (delay.ElapsedMilliseconds < 609) { coordinator.Poll(); Thread.Sleep(5); }
                Require(!stopped && files.Calls == before + 1, "One609ms query with cover must not stop or accumulate queries.");
            }
            finally { files.BeforeCapacity = null; release.Set(); }
            Wait(coordinator.Poll, 1000, "Delayed completion was not consumed.");
            Require(!stopped, "A covered delayed successful observation must remain healthy.");
            coordinator.CompleteCapture(true);
            Require(!latch.Read().Held, "Confirmed healthy closure must release its guard.");
        });
        foreach (bool blockLatch in new[] { false, true })
        Check(blockLatch ? "blocked-latch-stop-and-closure" : "blocked-capacity-stop-and-closure", directory =>
        {
            var files = new Files(); string security = Path.Combine(directory, "security");
            var store = new ReplayVolumeReservations(security); var latch = new ReplayStorageStopLatch(directory, security);
            using var coordinator = new ReplayStorageCoordinator(latch, store, files, Qualification, (_, _) => true);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var exited = new ManualResetEventSlim();
            bool stopped = false; coordinator.BeginCapture(Scope, directory, directory, () => stopped = true);
            Action block = () => { entered.Set(); try { Require(release.Wait(5000), "Blocked observation was not released."); } finally { exited.Set(); } };
            if (blockLatch) latch.BeforeReadForChecks = block; else files.BeforeCapacity = block;
            coordinator.StartSupervision(); Require(entered.Wait(1000), "Controlled observer did not enter.");
            try
            {
                Wait(() => coordinator.Stopped && Volatile.Read(ref stopped), 3000, "Monitoring outage did not deliver bounded controlled stop.");
                var close = Stopwatch.StartNew(); coordinator.CompleteCapture(true);
                Require(close.ElapsedMilliseconds < 1500 && coordinator.CapturePending, "Pending observer closure must return within its bounded drain and retain ownership.");
                string ledger = File.ReadAllText(Path.Combine(security, "replay-volume-reservations.json"));
                Require(ledger.Contains("capture-growth-", StringComparison.Ordinal), "Producer absence alone must not release an active observer's claim.");
                bool rejected = false;
                try { coordinator.BeginCapture(Scope, directory, directory, () => { }, explicitResume: true); } catch (IOException) { rejected = true; }
                Require(rejected, "An unjoined observer cannot be replaced by a new capture.");
            }
            finally { latch.BeforeReadForChecks = null; files.BeforeCapacity = null; release.Set(); Require(exited.Wait(1000), "Controlled observer did not leave."); }
            // A task's actual return follows the final fixture signal; retry
            // closure, never observations or new capture, until that return.
            Wait(() => { coordinator.CompleteCapture(true); return !coordinator.CapturePending; }, 1000, "Released observer did not close.");
            Require(store.ObserveUnreserved(directory, files).Bytes == files.Free && latch.Read().Held,
                "Only joined observer plus producer absence releases growth; stop remains explicit-Resume held.");
        });
        Check("low-space-before-and-after-acceptance", directory =>
        {
            var files = new Files { Free = 2_000_000_000 }; string security = Path.Combine(directory, "security");
            var latch = new ReplayStorageStopLatch(directory, security);
            using var coordinator = new ReplayStorageCoordinator(latch, new(security), files, Qualification, (_, _) => true);
            bool rejected = false;
            try { coordinator.RequireAdmission(directory, directory); } catch (IOException) { rejected = true; }
            Require(rejected, "Low space must reject before acquisition.");
            files.Free = 9_000_000_000; bool stopped = false;
            coordinator.BeginCapture(Scope, directory, directory, () => stopped = true);
            files.Free = 2_000_000_000;
            Wait(coordinator.Poll, 1000, "Lost capacity observation did not complete.");
            Require(stopped && coordinator.Hold.Held, "Sudden loss after acceptance must request stop and retain its hold.");
            coordinator.CompleteCapture(true);
        });
        Check("blocked-capture-seal-does-not-own-stop-lock", directory =>
        {
            var files = new Files(); string security = Path.Combine(directory, "security");
            var store = new ReplayVolumeReservations(security); var latch = new ReplayStorageStopLatch(directory, security);
            using var coordinator = new ReplayStorageCoordinator(latch, store, files, Qualification, (_, _) => true);
            using var job = new Native.ProcessJob(recoverable: true); // Empty; no source or child process.
            var proof = ReplayProducerProof.Current(job.Name, false);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            bool stopped = false;
            coordinator.BeginCapture(Scope, directory, directory, () => stopped = true, producerProof: proof);
            store.BeforeSetProducerForChecks = () => { entered.Set(); Require(release.Wait(5000), "Controlled capture seal was not released."); };
            var seal = Task.Run(() => coordinator.SealProducer(proof with { Sealed = true }));
            try
            {
                Require(entered.Wait(1000), "Capture producer seal did not enter.");
                var timer = Stopwatch.StartNew(); coordinator.StopForStorageFailure("Controlled stop during producer sealing.");
                Require(timer.ElapsedMilliseconds < 500 && stopped, "Blocked seal must not delay the stop request.");
                coordinator.CompleteCapture(true);
                Require(coordinator.CapturePending, "Producer absence cannot release a pending sealing operation.");
            }
            finally
            {
                store.BeforeSetProducerForChecks = null; release.Set();
                try { Require(seal.Wait(1000), "Capture seal did not close."); }
                catch (AggregateException error) when (error.InnerException is IOException) { }
            }
            coordinator.CompleteCapture(true);
            Require(!coordinator.CapturePending && latch.Read().Held, "Joined seal/producer closes only growth and preserves explicit Resume hold.");
        });
        Check("one-closer-owns-the-capture-generation", directory =>
        {
            var files = new Files(); string security = Path.Combine(directory, "security");
            var latch = new ReplayStorageStopLatch(directory, security);
            using var coordinator = new ReplayStorageCoordinator(latch, new(security), files, Qualification, (_, _) => true);
            coordinator.BeginCapture(Scope, directory, directory, () => { });
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int closers = 0;
            coordinator.BeforeClosureForChecks = () =>
            {
                if (Interlocked.Increment(ref closers) == 1) { entered.Set(); Require(release.Wait(2000), "Generation closer was not released."); }
            };
            var first = Task.Run(() => coordinator.CompleteCapture(true));
            try
            {
                Require(entered.Wait(1000), "First closer did not reach its outside-lock boundary.");
                var second = Task.Run(() => coordinator.CompleteCapture(true));
                Require(second.Wait(500) && coordinator.CapturePending && closers == 1,
                    "A competing closer must not inherit the first owner's absence/release operation.");
                bool rejected = false;
                try { coordinator.BeginCapture(Scope, directory, directory, () => { }); } catch (IOException) { rejected = true; }
                Require(rejected, "A pending closer must prevent a replacement generation.");
            }
            finally { release.Set(); Require(first.Wait(1000), "First generation closer did not finish."); coordinator.BeforeClosureForChecks = null; }
            coordinator.BeginCapture(Scope, directory, directory, () => { });
            Require(coordinator.CapturePending && !coordinator.Hold.Held, "A new generation must retain its own guard after old closers finish.");
            coordinator.CompleteCapture(true);
        });
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { failures, checks = rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Capacity monitoring: {rows.Count - failures}/{rows.Count} passed.");
        return failures == 0 ? 0 : 1;
    }
}
