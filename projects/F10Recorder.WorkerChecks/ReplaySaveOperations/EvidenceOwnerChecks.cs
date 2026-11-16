using System.Reflection;
namespace F10Recorder;

internal static class EvidenceOwnerChecks
{
    static void Need(bool value, string why) { if (!value) throw new IOException(why); }
    static void Reject(Action action, string why)
    {
        bool rejected = false;
        try { action(); } catch (Exception error) when (error is IOException or ObjectDisposedException or System.ComponentModel.Win32Exception) { rejected = true; }
        Need(rejected, why);
    }
    static ReplayGuardDirectory Root(string path) => new(path, Native.DirectoryIdentity(path), ReplayStorage.ObserveCapacity(path).Volume);
    internal static void Run(string root, Action<string, object> passed)
    {
        Directory.CreateDirectory(root);
        string Make(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        byte[] original = global::ReplayMp4Checks.PacketMuxFixture();
        var interval = new ReplayInterval(0, 1, 1);
        var source = ReplayMp4Reader.Read(new MemoryStream(original), original.Length, interval, 60, true);
        var video = source.Tracks.Single(t => t.Kind == "vide");
        var audio = source.Tracks.Single(t => t.Kind == "soun");
        var proof = new ReplayMp4Expectation(60, 0, video.Sps, video.Pps,
            video.Samples.Select(s => new ReplayExpectedPicture(s.Idr, s.CodedSha256, s.PacketSha256)), 1024,
            audio.Samples.Select(s => new ReplayExpectedAudio(s.Duration, s.PayloadSha256)));
        string inputPath = Make("inputs"), goodPath = Make("good"), finalPath = Make("final");
        byte[] payload = Enumerable.Range(0, 4096).Select(n => (byte)(n % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(inputPath, "source.bin"), payload);
        var extent = ReplayStorage.Observe(Path.Combine(inputPath, "source.bin"));
        long tick = 0; int scans = 0; ReplaySaveEvidenceOwner.Snapshot escaped = null;
        using (var inputRoot = Root(inputPath))
        using (var destination = Root(finalPath))
        using (var owner = new ReplaySaveEvidenceOwner(Root(goodPath), 131072,
            snapshot => { scans++; escaped = snapshot; _ = snapshot.Members.Count; return true; }, () => tick, 1000))
        {
            var inputs = Enumerable.Range(0, 4).Select(_ => ("source.bin", extent)).ToArray();
            int read = owner.WithInputsAsync(inputRoot, inputs, async streams =>
            {
                Reject(owner.Dispose, "Owner closed before its actual joined source task.");
                Reject(inputRoot.Dispose, "Source directory closed before all input users joined.");
                var jobs = streams.Select(async stream =>
                {
                    Need(stream.IsAsync, "Native handle was relabelled synchronous.");
                    byte[] bytes = new byte[payload.Length];
                    await stream.ReadExactlyAsync(bytes);
                    Need(bytes.SequenceEqual(payload), "Async owned input changed bytes.");
                    return bytes.Length;
                }).ToArray();
                return (await Task.WhenAll(jobs)).Sum();
            }).GetAwaiter().GetResult();
            Need(read == 16384, "Joined source reader omitted data.");
            passed("owner-genuine-async-inputs-and-joined-lifetime", new { readers = 4, bytes = read });
            Reject(() => _ = escaped.Members, "An expired output snapshot remained fresh authority.");
            Reject(() => owner.WriteManaged("unproved.mp4", 65536, ReplaySaveEvidenceOwner.Role.Main, (_, _) => { }), "Main output was created without its pre-write proof.");
            owner.FreezeExpectation("main.mp4", proof);
            owner.WriteManaged("main.mp4", 65536, ReplaySaveEvidenceOwner.Role.Main, (stream, check) => { stream.Write(original); tick = 10; check(); });
            Reject(() => owner.FreezeExpectation("main.mp4", proof), "Output could replace its pre-write source proof.");
            Reject(() => owner.Publish("main.mp4", destination, "too-early.mp4", () => { }), "Publication skipped validation.");
            Need(!File.Exists(Path.Combine(finalPath, "too-early.mp4")), "Early publication created a final file.");
            var checkedResult = owner.Validate("main.mp4", interval, 60, true);
            Need(checkedResult == new ReplayIntegrityResult(60, 48), "Actual complete source gate changed counts.");
            Reject(() => new FileStream(Path.Combine(goodPath, "main.mp4"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose(), "Checked output admitted a data writer.");
            var before = ReplayStorage.Observe(Path.Combine(goodPath, "main.mp4"));
            bool released = false;
            owner.Publish("main.mp4", destination, "checked.mp4", () =>
            {
                Reject(owner.Dispose, "Claim release disposed a publication owner reentrantly.");
                Reject(() => owner.WriteManaged("reentrant.bin", 4096, ReplaySaveEvidenceOwner.Role.Scratch, (_, _) => { }), "Claim release began another managed writer.");
                Reject(() => File.Move(Path.Combine(goodPath, "main.mp4"), Path.Combine(goodPath, "replaced.mp4")), "Checked leaf was renameable between callback and publication.");
                owner.Check(); released = true;
            });
            byte[] finalBytes = new byte[original.Length];
            // The publication ticket intentionally still owns DELETE access.
            // This observer must share it; changing the pin to suit a test
            // reader would undo the protection this control is exercising.
            using (var finalReader = new FileStream(Path.Combine(finalPath, "checked.mp4"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                finalReader.ReadExactly(finalBytes);
            Need(released && !File.Exists(Path.Combine(goodPath, "main.mp4"))
                && ReplayStorage.Observe(Path.Combine(finalPath, "checked.mp4")).Identity == before.Identity
                && finalBytes.SequenceEqual(original), "Checked publication did not move the checked object.");
            passed("owner-frozen-proof-full-packet-gate-checked-publication", new { scans, pictures = 60, audioPackets = 48 });
        }
        foreach (string fault in new[] { "picture-bytes", "audio-bytes", "drift", "truncation" })
        {
            byte[] damaged = original.ToArray();
            if (fault == "picture-bytes") damaged[checked((int)video.Samples[10].Offset + video.Samples[10].Bytes - 1)] ^= 1;
            if (fault == "audio-bytes") damaged[checked((int)audio.Samples[12].Offset + 1)] ^= 1;
            if (fault == "truncation") damaged = damaged[..^1];
            if (fault == "drift") damaged = (byte[])typeof(global::ReplayMp4Checks).GetMethod("Fixture", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { "drift" });
            string folder = Make("bad-" + fault);
            using var destination = Root(finalPath);
            using var owner = new ReplaySaveEvidenceOwner(Root(folder), 131072, _ => true, () => 0, 1000);
            owner.FreezeExpectation("bad.mp4", proof);
            owner.WriteManaged("bad.mp4", 65536, ReplaySaveEvidenceOwner.Role.Main, (stream, check) => { stream.Write(damaged); check(); });
            Reject(() => owner.Validate("bad.mp4", interval, 60, true), "Integrated packet gate accepted " + fault);
            Reject(() => owner.Publish("bad.mp4", destination, fault + ".mp4", () => { }), "Uncertain owner published " + fault);
            Need(!File.Exists(Path.Combine(finalPath, fault + ".mp4")), "Failed validation published " + fault);
            passed("owner-rejects-publication-" + fault, null);
        }
        {
            string folder = Make("growth"); long now = 0; int observed = 0;
            using var owner = new ReplaySaveEvidenceOwner(Root(folder), 131072, _ => { observed++; return true; }, () => now, 1000);
            Reject(() => owner.WriteManaged("over.bin", 4096, ReplaySaveEvidenceOwner.Role.Scratch, (stream, check) =>
            {
                now = 10; stream.Write(new byte[8192]); stream.Flush(); check();
            }), "Skipped global scan also skipped actual output growth.");
            Need(observed == 1, "Growth rejection depended on another global scan.");
            Reject(() => owner.Check(), "Failed owner resumed after healthy checks.");
            passed("owner-growth-every-callback-despite-global-coalescing", new { observed });
        }
        {
            string folder = Make("late-link"); long now = 0; int observed = 0;
            using var aliases = Root(Make("aliases"));
            using var owner = new ReplaySaveEvidenceOwner(Root(folder), 131072, snapshot =>
            {
                observed++;
                if (snapshot.Members.Any(m => m.Observation.Links != 1)) throw new IOException("Conservative alias check rejected the new retained hardlink.");
                return true;
            }, () => now, 1000);
            Reject(() => owner.WriteManaged("linked.bin", 65536, ReplaySaveEvidenceOwner.Role.Scratch, (stream, check) =>
            {
                stream.Write(payload); stream.Flush();
                using var actual = Root(folder);
                using var input = actual.OpenObservation("linked.bin", ReplayStorage.Observe(Path.Combine(folder, "linked.bin")));
                input.LinkTo(aliases, "retained.bin"); now = 10; check();
            }), "A late link waited until the next scheduled scan.");
            Need(observed == 2, "Late-link callback did not enter the conservative check immediately.");
            passed("owner-late-link-forces-conservative-check-same-callback", new { observed });
        }
        {
            string folder = Make("expiry"); long now = 0;
            using var owner = new ReplaySaveEvidenceOwner(Root(folder), 131072, _ => true, () => now, 1000);
            Reject(() => owner.WriteManaged("expired.bin", 65536, ReplaySaveEvidenceOwner.Role.Scratch, (stream, check) => { now = 501; check(); }), "Owner renewed evidence after501ms without checking.");
            passed("owner-global-expiry-fails-before-publication", null);
        }
        {
            string folder = Make("checking-work"); long now = 0;
            using var owner = new ReplaySaveEvidenceOwner(Root(folder), 131072, _ => { now += 501; return true; }, () => now, 1000);
            Reject(() => owner.WriteManaged("uncreated.bin", 65536, ReplaySaveEvidenceOwner.Role.Scratch, (_, _) => { }), "Owner excluded the global checking work from expiry.");
            Need(!File.Exists(Path.Combine(folder, "uncreated.bin")), "Expired initial evidence still created output.");
            passed("owner-global-work-counts-toward-500ms", null);
        }
        {
            string folder = Make("cancelled-input");
            using var inputRoot = Root(inputPath);
            using var owner = new ReplaySaveEvidenceOwner(Root(folder), 131072, _ => true, () => 0, 1000);
            bool cancelled = false;
            try
            {
                owner.WithInputsAsync(inputRoot, new[] { ("source.bin", extent) }, async streams =>
                {
                    await streams[0].ReadExactlyAsync(new byte[4096], new CancellationToken(true));
                    return 1;
                }).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { cancelled = true; }
            Need(cancelled, "Actual async input cancellation was swallowed.");
            Reject(() => owner.Check(), "Cancelled reader owner remained publishable.");
            passed("owner-cancelled-input-joins-and-poisons", null);
        }
    }
}

