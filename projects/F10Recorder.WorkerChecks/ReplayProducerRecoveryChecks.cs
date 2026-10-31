using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace F10Recorder;

internal static class ReplayProducerRecoveryChecks
{
    const string Volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";
    sealed class Files : ReplayFiles
    {
        internal override ReplayVolumeCapacity ObserveCapacity(string directory) => new(Volume, 4096, 9_000_000_000);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    sealed record Ready(ReplayProducerProof Proof, string Owner, int ChildId, long ChildStartedUtcTicks);
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly().Location);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info);
    }
    internal static int Child(string root, bool sealedLaunch)
    {
        string profile = Path.Combine(root, "profile"), security = Path.Combine(root, "security");
        Directory.CreateDirectory(profile);
        using var job = new Native.ProcessJob(recoverable: true);
        var latch = new ReplayStorageStopLatch(profile, security);
        var guard = latch.BeginCaptureGuard().Guard ?? throw new IOException("Fixture guard did not arm.");
        var proof = ReplayProducerProof.Current(job.Name, false);
        latch.RecordProducer(guard.HoldId, proof);
        var store = new ReplayVolumeReservations(security);
        store.Update(guard.HoldId, [new(Path.Combine(profile, "growth"), profile, Volume, 4096, 4096, ReplayVolumeClaimKind.UnmaterializedGrowth)], new Files(), proof);
        using var child = Start("--replay-producer-wait");
        job.Add(child);
        if (sealedLaunch)
        {
            proof = proof with { Sealed = true };
            latch.RecordProducer(guard.HoldId, proof);
            store.SetProducer(guard.HoldId, proof);
        }
        else
        {
            // Reproduce maintenance carrying a stale sealed capture proof
            // while an exporter is between creation and job assignment. That
            // routine refresh must preserve the explicit launch fence.
            store.SetProducer(guard.HoldId, proof with { Sealed = true });
            store.SetProducer(guard.HoldId, proof);
            store.Update(guard.HoldId, [new(Path.Combine(profile, "growth"), profile, Volume, 4096, 4096, ReplayVolumeClaimKind.UnmaterializedGrowth)], new Files(), proof with { Sealed = true });
        }
        File.WriteAllText(Path.Combine(root, "ready.pending"), JsonSerializer.Serialize(new Ready(proof, guard.HoldId, child.Id, child.StartTime.ToUniversalTime().Ticks)));
        File.Move(Path.Combine(root, "ready.pending"), Path.Combine(root, "ready.json"));
        Thread.Sleep(15000); // Self-bounded; the controller normally kills this exact owned leader.
        return 0;
    }
    internal static int Run(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh recovery evidence directory.");
        Directory.CreateDirectory(root);
        int failed = 0; var checks = new List<object>();
        void Check(string name, Action action)
        {
            try { action(); checks.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; checks.Add(new { name, passed = false, error = ex.ToString() }); }
        }
        Check("living-worker-is-not-absence-even-with-empty-job", () =>
        {
            using var job = new Native.ProcessJob(recoverable: true);
            Require(!ReplayProducerProof.Current(job.Name, true).ConfirmedAbsent(), "An empty job must not discharge a living worker's future writes.");
        });
        foreach (bool sealedLaunch in new[] { true, false })
            Check(sealedLaunch ? "crash-recovery-waits-for-native-job-then-requires-explicit-resume" : "interrupted-launch-retains-unknown-growth", () =>
            {
                string folder = Path.Combine(root, sealedLaunch ? "sealed" : "unsealed"); Directory.CreateDirectory(folder);
                string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security");
                using var leader = Start("--replay-producer-proof-child", folder, sealedLaunch ? "sealed" : "unsealed");
                Process child = null; IntPtr retainedJob = IntPtr.Zero;
                try
                {
                    var clock = Stopwatch.StartNew();
                    while (!File.Exists(Path.Combine(folder, "ready.json")))
                    {
                        Require(!leader.HasExited && clock.Elapsed < TimeSpan.FromSeconds(8), "Owned proof fixture did not become ready."); Thread.Sleep(20);
                    }
                    var ready = JsonSerializer.Deserialize<Ready>(File.ReadAllText(Path.Combine(folder, "ready.json")));
                    child = Process.GetProcessById(ready.ChildId);
                    Require(child.StartTime.ToUniversalTime().Ticks == ready.ChildStartedUtcTicks, "Fixture child identity changed.");
                    retainedJob = OpenJobObject(4, false, ready.Proof.JobName);
                    Require(retainedJob != IntPtr.Zero, "Native fixture job could not be pinned.");
                    var latch = new ReplayStorageStopLatch(profile, security);
                    var store = new ReplayVolumeReservations(security);
                    string sentinel = Path.Combine(profile, "retained-footage.txt"); File.WriteAllText(sentinel, "protected fixture footage");
                    leader.Kill(); Require(leader.WaitForExit(5000), "Owned leader exit was not confirmed.");
                    Require(!child.WaitForExit(100), "Pinned job must retain its active child for the negative proof control.");
                    Require(!latch.ProducerAbsenceConfirmed() && latch.Read().Held, "Dead worker with an active producer must remain held.");
                    Require(store.ObserveUnreserved(profile, new Files()).Bytes == 8_999_995_904, "Active or unsealed producer must retain its future 4096 bytes.");
                    CloseHandle(retainedJob); retainedJob = IntPtr.Zero;
                    Require(child.WaitForExit(5000), "Closing the last owned job handle did not confirm child exit.");
                    Require(latch.ProducerAbsenceConfirmed() == sealedLaunch, "Only a sealed, exited producer group can establish recovery proof.");
                    Require(store.ObserveUnreserved(profile, new Files()).Bytes == (sealedLaunch ? 9_000_000_000 : 8_999_995_904), "Recovery released the wrong future obligation.");
                    Require(latch.Read().Held && File.ReadAllText(sentinel) == "protected fixture footage", "Recovery must preserve the stop and retained material.");
                    string scope = new('A', 64);
                    var headroom = new ReplayCaptureHeadroom(scope, Volume, 4096, 4096, 8192);
                    var model = new ReplayReferenceModel("controlled-recovery", scope, Volume, 4096, 0, 0, [], headroom);
                    using var coordinator = new ReplayStorageCoordinator(latch, store, new Files(), () => new(model, null), (_, _) => true);
                    if (sealedLaunch)
                    {
                        coordinator.RequireResume(profile, profile);
                        Require(latch.Read().Held, "Successful safe checks alone must not clear the stop.");
                        coordinator.BeginCapture(scope, profile, profile, () => { }, explicitResume: true);
                        Require(!coordinator.Hold.Held, "Explicit safe Resume must arm a fresh owned capture guard.");
                        coordinator.CompleteCapture(true);
                        Require(!latch.Read().Held, "Fresh guard should complete normally.");
                    }
                    else
                    {
                        bool rejected = false;
                        try { coordinator.RequireResume(profile, profile); } catch (IOException) { rejected = true; }
                        Require(rejected && latch.Read().Held, "Unknown launch must reject Resume despite returning space.");
                    }
                }
                finally
                {
                    if (!leader.HasExited) { leader.Kill(); leader.WaitForExit(5000); }
                    if (retainedJob != IntPtr.Zero) CloseHandle(retainedJob);
                    if (child != null) { Require(child.WaitForExit(5000), "Owned child cleanup is unconfirmed."); child.Dispose(); }
                }
            });
        Check("confirmed-storage-stop-survives-worker-replacement-with-proof", () =>
        {
            string profile = Path.Combine(root, "normal-profile"), security = Path.Combine(root, "normal-security"); Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security);
            string scope = new('B', 64);
            var headroom = new ReplayCaptureHeadroom(scope, Volume, 4096, 4096, 8192);
            var model = new ReplayReferenceModel("controlled-normal-recovery", scope, Volume, 4096, 0, 0, [], headroom);
            using (var coordinator = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(security), new Files(), () => new(model, null), (_, _) => true))
            {
                coordinator.BeginCapture(scope, profile, profile, () => { });
                coordinator.StopForStorageFailure("controlled storage stop"); coordinator.CompleteCapture(true);
            }
            Require(new ReplayStorageStopLatch(profile, security).ProducerAbsenceConfirmed(), "Confirmed normal closure must be durable after replacement.");
            Require(latch.Read().Held, "Durable closure proof does not clear a storage stop.");
        });
        File.WriteAllText(Path.Combine(root, "producer-recovery.json"), JsonSerializer.Serialize(new { failed, checks,
            effects = "Four bounded nonmedia fixture children; exact leader termination, native named job/query/last-handle controls, tiny protected files. No engine, GPU, recording, audio, visible UI or startup changes." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Producer recovery: {checks.Count - failed}/{checks.Count} passed");
        return failed == 0 ? 0 : 1;
    }
}
