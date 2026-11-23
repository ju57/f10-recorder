using System.Diagnostics;
namespace F10Recorder;

// Synthetic ownership/metadata faults, never a media-validation success claim.
internal static class DecoderObservationChecks
{
    static readonly List<object> retainedUncertain = [];
    static void Need(bool ok, string why) { if (!ok) throw new IOException(why); }
    static void Reject(Action action, string why)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or ObjectDisposedException) { return; }
        throw new IOException(why);
    }
    sealed class NoReopen(string path) : ReplayFiles
    {
        internal int Reopens;
        internal override ReplayFileExtent Observe(string candidate)
        { if (candidate == path) { Reopens++; throw new IOException("Decoder-owned output must not reopen for metadata."); } return base.Observe(candidate); }
    }
    internal static void Run(string root, Action<string, object> passed, Action bound)
    {
        Directory.CreateDirectory(root);
        foreach (bool uncertain in new[] { false, true })
        {
            string directory = Path.Combine(root, uncertain ? "uncertain" : "ordinary");
            Directory.CreateDirectory(directory);
            var files = new ReplayRootedFiles(); files.Root(directory);
            var info = new ReplaySessionInfo(directory, "unused", 1, 20, 60, "h264_nvenc", ".ts", false, true);
            var request = new ReplayExportRequest(info, new(0, 1, 1), [], "unused",
                Path.Combine(directory, "output.partial.mp4"), new() { Frequency = Stopwatch.Frequency });
            request.SelectRecipe(ReplayExactExport.Recipe);
            var scope = new ReplayNativeExportScope(files, request); request.NativeFiles = scope;
            scope.BeginExternal(request.Partial);
            using (var writer = new FileStream(request.Partial, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { writer.Write(new byte[4096]); writer.Flush(); }
            scope.CompleteExternal(request.Partial); scope.CompleteExport();
            string owner = Guid.NewGuid().ToString("N");
            using (var before = scope.Observe())
                Need(scope.BorrowReservationEvidence(before, owner) == null, "Unpinned decoder gap acquired metadata eligibility.");
            using var decoder = new ReplayDecodeLease(request.Partial);
            decoder.BindObservation(request);
            Need(!scope.CanDispose && request.ValidatedOutput == null,
                "Decoder metadata granted publication or allowed active owner retirement.");
            Reject(() => decoder.BindObservation(request), "Decoder borrow was rebound.");
            using (var duplicate = new ReplayDecodeLease(request.Partial))
                Reject(() => duplicate.BindObservation(request), "Second decoder owner replaced the first.");
            string authority = Path.Combine(root, "authority-" + uncertain);
            var reservations = new ReplayVolumeReservations(authority);
            using (var snapshot = scope.Observe())
            using (var evidence = scope.BorrowReservationEvidence(snapshot, owner))
            {
                Need(evidence != null, "Protected decoder did not provide metadata evidence.");
                var extent = snapshot.Main.Extent;
                var claim = new ReplayVolumeClaim(request.Partial, directory, extent.Volume, extent.AllocationUnit, extent.CopyBytes);
                var noReopen = new NoReopen(request.Partial);
                reservations.Update(owner, [claim], noReopen, evidence: evidence);
                reservations.Update(owner, [claim], noReopen, evidence: evidence);
                Need(noReopen.Reopens == 0 && evidence.Observe(claim, directory, files.DirectoryOwner(directory).Native.Identity).Extent == extent,
                    "Decoder metadata reopened output or changed actual allocation/identity.");
                Reject(() => evidence.Observe(claim, directory, "wrong-anchor"), "Changed anchor accepted decoder evidence.");
                Reject(() => File.Move(request.Partial, request.Partial + ".replaced"), "Decoder path pin permitted replacement.");
                Reject(() => { using var writer = File.OpenWrite(request.Partial); }, "Decoder immutable owner permitted writes.");
                Reject(() => decoder.ObserveReservation(extent with { Identity = "wrong" }), "Foreign identity supplied allocation credit.");
                Need(evidence.Observe(claim with { Path = Path.Combine(directory, "foreign") }, null, null) == null,
                    "Decoder evidence was lent to a foreign path.");
                snapshot.Dispose();
                Reject(() => evidence.Observe(claim, null, null), "Expired callback snapshot retained decoder evidence.");
                if (!uncertain) reservations.Release(owner);
            }
            using var last = scope.Observe();
            using var stale = scope.BorrowReservationEvidence(last, owner);
            if (!uncertain)
            {
                var sync = typeof(ReplayNativeExportScope).GetField("sync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(scope);
                using var revoking = new ManualResetEventSlim();
                using var disposing = new ManualResetEventSlim();
                Task revoke, dispose;
                // Hold the actual observation lock to reproduce a pending
                // revocation followed by concurrent exact-child disposal.
                lock (sync)
                {
                    revoke = Task.Run(() => { revoking.Set(); decoder.EndObservation(); });
                    Need(revoking.Wait(1000), "Revoker did not start.");
                    dispose = Task.Run(() => { disposing.Set(); decoder.Dispose(); });
                    Need(disposing.Wait(1000), "Concurrent disposer did not start.");
                    Need(!revoke.Wait(30) && !dispose.Wait(30), "Disposer bypassed the active metadata observation barrier.");
                    Reject(() => { using var writer = File.OpenWrite(request.Partial); }, "Waiting revocation released immutable input.");
                }
                Need(Task.WaitAll([revoke, dispose], 2000), "Revocation/disposal did not join.");
                passed("decoder-owner-concurrent-revocation-disposal-keeps-input-until-observation-ends", null);
            }
            else decoder.EndObservation(uncertain: true);
            Reject(() => stale.Observe(new(request.Partial, directory, last.Main.Extent.Volume,
                last.Main.Extent.AllocationUnit, last.Main.Extent.CopyBytes), null, null), "Ended decoder owner repaired stale evidence.");
            using (var after = scope.Observe())
                Need(scope.BorrowReservationEvidence(after, owner) == null, "Revoked decoder borrow remained eligible.");
            if (uncertain) Reject(() => File.Delete(request.Partial), "Metadata revocation released the actual decoder input.");
            Need(request.ValidatedOutput == null && scope.CanDispose == !uncertain,
                "Revocation marked validation complete or lost uncertain ownership.");
            decoder.Dispose();
            if (uncertain)
            {
                using var ledger = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(authority, "replay-volume-reservations.json")));
                Need(ledger.RootElement.GetProperty("Owners").EnumerateArray().Any(h => h.GetProperty("Owner").GetString() == owner),
                    "Uncertain decoder failure released its durable reservation claim.");
                Reject(scope.Dispose, "Uncertain decoder scope retired its claims/owners.");
                retainedUncertain.Add(files); retainedUncertain.Add(scope);
            }
            else { scope.Dispose(); files.Dispose(); }
            passed("decoder-owner-" + (uncertain ? "uncertain-retention" : "metadata-lifetime-and-revocation"), null);
            bound();
        }
    }
}
