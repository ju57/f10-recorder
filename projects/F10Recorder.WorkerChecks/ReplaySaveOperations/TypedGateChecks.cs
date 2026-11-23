using System.Reflection;
namespace F10Recorder;

internal static class TypedGateChecks
{
    static void Need(bool value, string why) { if (!value) throw new IOException(why); }
    static void Reject(Action action, string why)
    {
        bool failed = false;
        try { action(); } catch (Exception error) when (error is IOException or ArgumentException or System.ComponentModel.Win32Exception or ObjectDisposedException) { failed = true; }
        Need(failed, why);
    }
    static ReplayGuardDirectory Root(string path) => new(path, Native.DirectoryIdentity(path), ReplayStorage.ObserveCapacity(path).Volume);
    internal static void Run(string root, Action<string, object> passed, Action bound)
    {
        Directory.CreateDirectory(root);
        using var parent = System.Diagnostics.Process.GetCurrentProcess();
        DateTime startedUtc = parent.StartTime.ToUniversalTime();
        byte[] template = global::ReplayMp4Checks.PacketMuxFixture();
        var interval = new ReplayInterval(0, 1, 1);
        var independent = ReplayMp4Reader.Read(new MemoryStream(template), template.Length, interval, 60, true);
        var video = independent.Tracks.Single(t => t.Kind == "vide");
        var audio = independent.Tracks.Single(t => t.Kind == "soun");
        var proof = new ReplayMp4Expectation(60, 0, video.Sps, video.Pps,
            video.Samples.Select(s => new ReplayExpectedPicture(s.Idr, s.CodedSha256, s.PacketSha256)), 1024,
            audio.Samples.Select(s => new ReplayExpectedAudio(s.Duration, s.PayloadSha256)));
        string Make(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        ReplayExportRequest Request(string folder, string recipe = ReplaySaveExport.PacketRecipe)
        {
            var info = new ReplaySessionInfo(folder, Path.Combine(folder, "segments.csv"), 1, 20, 60, "h264_nvenc", ".ts", false, true);
            var request = new ReplayExportRequest(info, interval, [], Path.Combine(folder, "unused-concat.txt"), Path.Combine(folder, "main.mp4"), new());
            request.SelectRecipe(recipe); request.FreezeIntegrity(proof); return request;
        }
        ReplayFileExtent Write(ReplayGuardDirectory directory, string leaf, byte[] bytes)
        {
            using var output = directory.CreateNew(leaf); output.Data().Write(bytes); output.Data().Flush(); return output.Observe().Extent;
        }
        void Receipt(ReplayExportRequest request, ReplayFileExtent actual)
            => request.FreezeManagedMux(new(Environment.ProcessId, startedUtc, true, true, "Completed", 1, template.Length,
                0, 0, actual, null, null, null, 65536, 0, new(), new(), 0, 0));
        void Attach(ReplayExportRequest request, ReplayGuardDirectory directory, ReplayFileExtent actual)
            => request.AttachNativePublication(() => new ReplayPublicationLease(request.Partial, directory,
                directory.OpenPublicationRead("main.mp4", actual), actual));

        {
            string folder = Make("positive"); using var directory = Root(folder); var request = Request(folder);
            var actual = Write(directory, "main.mp4", template); Receipt(request, actual); Attach(request, directory, actual);
            int checks = 0;
            void Check()
            {
                bound(); checks++;
                if (request.IntegrityGateTiming.LeaseMilliseconds == null) return;
                Reject(() => File.Move(request.Partial, Path.Combine(folder, "stolen.mp4")), "Checked leaf was renameable during the actual packet parser.");
                Reject(() => new FileStream(request.Partial, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose(), "Actual packet parser admitted a writer.");
            }
            var result = ReplayOutputIntegrity.Read(request, Check, retainForPublication: true);
            Need(result == new ReplayIntegrityResult(60, 48) && request.ValidatedOutput != null, "Actual typed gate failed complete expected tracks/counts.");
            Reject(() => request.OpenPublication(new()), "Native factory was reusable after gate transfer.");
            Reject(() => request.ValidatedOutput.PinDecoderPath(), "Native packet capability escaped to an unsupported pathname decoder.");
            string final = Path.Combine(folder, "final.mp4");
            new ReplayFiles().Publish(request, final);
            Need(request.ValidatedOutput.Published && !File.Exists(request.Partial) && ReplayStorage.Observe(final).Identity == actual.Identity, "Existing publication route did not move the checked native object.");
            request.ValidatedOutput.Dispose();
            Need(File.ReadAllBytes(final).SequenceEqual(template), "Native typed publication changed checked bytes.");
            passed("actual-packet-gate-retains-native-typed-lease-and-publishes", new { checks, result.CodedPictures, result.AudioPackets });
        }
        foreach (string fault in new[] { "picture", "audio", "drift", "truncated" })
        {
            string folder = Make(fault); using var directory = Root(folder); var request = Request(folder);
            byte[] bytes = template.ToArray();
            if (fault == "picture") bytes[checked((int)video.Samples[10].Offset + video.Samples[10].Bytes - 1)] ^= 1;
            if (fault == "audio") bytes[checked((int)audio.Samples[12].Offset + 1)] ^= 1;
            if (fault == "truncated") bytes = bytes[..^1];
            if (fault == "drift") bytes = (byte[])typeof(global::ReplayMp4Checks).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { "drift" });
            var actual = Write(directory, "main.mp4", bytes); Receipt(request, actual); Attach(request, directory, actual);
            Reject(() => ReplayOutputIntegrity.Read(request, bound, retainForPublication: true), "Actual gate accepted " + fault);
            Need(request.ValidatedOutput == null && !File.Exists(Path.Combine(folder, "final.mp4")), "Failed gate retained publication authority.");
            // Cache's separate packet-requires-ValidatedOutput precondition is
            // not bypassed by calling the legacy path-Move fallback here.
            directory.Dispose();
            passed("actual-typed-gate-rejects-" + fault, null);
        }
        foreach (string fault in new[] { "identity", "length", "allocation-unit" })
        {
            string folder = Make("receipt-" + fault); using var directory = Root(folder); var request = Request(folder);
            var actual = Write(directory, "main.mp4", template);
            var changed = fault == "identity" ? actual with { Identity = "00000000:0000000000000000" }
                : fault == "length" ? actual with { Length = actual.Length + 1 } : actual with { AllocationUnit = actual.AllocationUnit * 2 };
            Receipt(request, changed);
            // Adapter sees the correct producer object here; the UNCHANGED
            // actual gate must independently reject its wrong reported receipt.
            Attach(request, directory, actual);
            Reject(() => ReplayOutputIntegrity.Read(request, bound, retainForPublication: true), "Producer receipt mismatch reached publication: " + fault);
            Need(request.ValidatedOutput == null, "Wrong receipt retained a checked lease.");
            directory.Dispose(); passed("actual-typed-gate-rejects-reported-" + fault, null);
        }
        {
            string folder = Make("normalization"); using var directory = Root(folder);
            var actual = Write(directory, "main.mp4", template);
            var reader = directory.OpenPublicationRead("main.mp4", actual);
            Reject(() => new ReplayPublicationLease(null, directory, reader, actual), "Invalid path accepted transferred ownership.");
            directory.Dispose(); passed("native-lease-normalization-failure-closes-transferred-reader", null);
        }
        {
            string folder = Make("observer-sharing"); using var directory = Root(folder);
            var actual = Write(directory, "main.mp4", template);
            using var observer = directory.OpenObservation("main.mp4", actual);
            var reader = directory.OpenPublicationRead("main.mp4", actual);
            Reject(() => new ReplayPublicationLease(Path.Combine(folder, "main.mp4"), directory, reader, actual), "Native constructor ignored an earlier live no-delete observer.");
            Need(observer.Observe().Extent.Identity == actual.Identity, "Constructor failure closed another owner's observer.");
            observer.Dispose(); directory.Dispose(); passed("native-lease-handoff-requires-prior-observer-close", null);
        }
        foreach (string fault in new[] { "null", "throw", "exact", "recipe-change" })
        {
            string folder = Make("factory-" + fault); using var directory = Root(folder);
            var request = Request(folder, fault == "exact" ? ReplayExactExport.Recipe : ReplaySaveExport.PacketRecipe);
            var actual = Write(directory, "main.mp4", template); Receipt(request, actual);
            int calls = 0;
            if (fault == "exact") Reject(() => Attach(request, directory, actual), "Exact fallback acquired a packet-only native capability.");
            else
            {
                request.AttachNativePublication(() => { calls++; if (fault == "throw") throw new IOException("Injected factory failure."); return null; });
                if (fault == "recipe-change") request.SelectRecipe(ReplayExactExport.Recipe);
                Reject(() => request.OpenPublication(new()), "Invalid native factory was accepted.");
                if (fault == "recipe-change") request.SelectRecipe(ReplaySaveExport.PacketRecipe);
                Reject(() => request.OpenPublication(new()), "Failed factory was retried.");
                Need(calls == (fault == "recipe-change" ? 0 : 1), "Factory repeated after failure or recipe change.");
            }
            passed("native-factory-boundary-" + fault, new { calls });
        }
        {
            string folder = Make("collision"); using var directory = Root(folder); var request = Request(folder);
            var actual = Write(directory, "main.mp4", template); byte[] foreign = [0xE7, 0xE7]; Write(directory, "final.mp4", foreign);
            Receipt(request, actual); Attach(request, directory, actual); ReplayOutputIntegrity.Read(request, bound, retainForPublication: true);
            try { Reject(() => new ReplayFiles().Publish(request, Path.Combine(folder, "final.mp4")), "Typed publication replaced a foreign destination."); }
            finally { request.ValidatedOutput.Dispose(); }
            Need(File.ReadAllBytes(Path.Combine(folder, "final.mp4")).SequenceEqual(foreign) && File.ReadAllBytes(request.Partial).SequenceEqual(template), "Failed publication changed either file.");
            passed("actual-typed-publication-collision-preserves-both", null);
        }
    }
}

