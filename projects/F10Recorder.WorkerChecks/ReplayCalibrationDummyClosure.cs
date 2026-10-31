using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

internal sealed record ReplayDummyClosurePlan(CalibrationPlan Resources, string EvidenceRoot, string[] DummyRoots, double PriorRehearsalSeconds,
    bool AppendToEvidence = false, bool Compress = false);

// Archive only explicitly named, closed nonmedia failures/diagnostics. Real or
// historical media roots have different receipts and cannot enter this route.
internal static class ReplayCalibrationDummyClosure
{
    internal static int Checks(string root)
    {
        using var owner = PilotStorageObservation.Create(root);
        string[] history = Enumerable.Range(0, 4).Select(n => Path.Combine(root, "history-" + n)).ToArray();
        foreach (string item in history) Directory.CreateDirectory(item);
        string[] sources = [Path.Combine(root, "failed-dummy"), Path.Combine(root, "diagnostic-dummy")];
        for (int n = 0; n < sources.Length; n++)
        {
            using var sourceOwner = PilotStorageObservation.Create(sources[n]);
            File.WriteAllText(Path.Combine(sources[n], "dummy.bin"), "Preserve verified dummy bytes.");
            File.WriteAllText(Path.Combine(sources[n], n == 0 ? "failure.json" : "diagnostic.json"), JsonSerializer.Serialize(new {
                result = n == 0 ? "failed-full-nonmedia-rehearsal" : "diagnostic-completed-not-a-rehearsal-pass" }));
        }
        string evidence = Path.Combine(root, "evidence"), work = Path.Combine(root, "work");
        using var runtime = File.OpenRead(typeof(ReplayCalibrationDummyClosure).Assembly.Location);
        var bindings = new Dictionary<string, string> { [typeof(ReplayCalibrationDummyClosure).Assembly.Location] = Convert.ToHexString(SHA256.HashData(runtime)) };
        var resources = new CalibrationPlan(work, "unused", "closure-control", bindings, ["T01", "T02", "T03", "H02", "H01", "H03"],
            [.. history, .. sources, evidence], 0, 0, RetainedOfflineSeconds: 0);
        string plan = Path.Combine(root, "closure.json"); File.WriteAllText(plan, JsonSerializer.Serialize(new ReplayDummyClosurePlan(resources, evidence, sources, 0)));
        using (var input = File.OpenRead(plan)) Run(plan, Convert.ToHexString(SHA256.HashData(input)));
        if (sources.Any(Directory.Exists) || Directory.GetFiles(evidence, "*.tar").Length != 2 || Directory.GetDirectories(evidence).Length != 0)
            throw new IOException("Dummy closure did not retain both archives in one flat root.");
        foreach (string archive in Directory.GetFiles(evidence, "*.tar"))
        {
            using var input = File.OpenRead(archive); using var tar = new System.Formats.Tar.TarReader(input);
            System.Formats.Tar.TarEntry entry; bool preserved = false;
            while ((entry = tar.GetNextEntry()) != null) if (entry.Name == "dummy.bin")
            { using var reader = new StreamReader(entry.DataStream); preserved = reader.ReadToEnd() == "Preserve verified dummy bytes."; }
            if (!preserved) throw new IOException("Closed dummy byte preservation control failed.");
        }
        string appended = Path.Combine(root, "another-failed-dummy");
        using (var appendedOwner = PilotStorageObservation.Create(appended))
        {
            File.WriteAllText(Path.Combine(appended, "failure.json"), "{\"result\":\"failed-full-nonmedia-rehearsal\"}");
            File.WriteAllText(Path.Combine(appended, "dummy.bin"), "Additional preserved dummy.");
        }
        string originalReceiptHash;
        using (var input = File.OpenRead(Path.Combine(evidence, "dummy-closure.json"))) originalReceiptHash = Convert.ToHexString(SHA256.HashData(input));
        string appendPlan = Path.Combine(root, "append.json");
        File.WriteAllText(appendPlan, JsonSerializer.Serialize(new ReplayDummyClosurePlan(resources with {
            Root = Path.Combine(root, "append-work"), RetainedRoots = [.. history, evidence, appended] }, evidence, [appended], 0, AppendToEvidence: true)));
        using (var input = File.OpenRead(appendPlan)) Run(appendPlan, Convert.ToHexString(SHA256.HashData(input)));
        using (var input = File.OpenRead(Path.Combine(evidence, "dummy-closure.json")))
            if (Convert.ToHexString(SHA256.HashData(input)) != originalReceiptHash || Directory.Exists(appended)
                || Directory.GetFiles(evidence, "*.tar").Length != 3 || !File.Exists(Path.Combine(evidence, "append-work-closure.json")))
                throw new IOException("Appending one closed dummy did not preserve existing closure provenance.");
        string compressedSource = Path.Combine(root, "compressed-failed-dummy");
        using (var compressedOwner = PilotStorageObservation.Create(compressedSource))
        {
            File.WriteAllText(Path.Combine(compressedSource, "failure.json"), "{\"result\":\"failed-full-nonmedia-rehearsal\"}");
            File.WriteAllText(Path.Combine(compressedSource, "dummy.bin"), "Lossless compressed dummy evidence.");
        }
        string compressedPlan = Path.Combine(root, "compressed-append.json");
        File.WriteAllText(compressedPlan, JsonSerializer.Serialize(new ReplayDummyClosurePlan(resources with {
            Root = Path.Combine(root, "compressed-work"), RetainedRoots = [.. history, evidence, compressedSource] },
            evidence, [compressedSource], 0, AppendToEvidence: true, Compress: true)));
        using (var input = File.OpenRead(compressedPlan)) Run(compressedPlan, Convert.ToHexString(SHA256.HashData(input)));
        using (var input = File.OpenRead(Path.Combine(evidence, "compressed-failed-dummy.tar.br")))
        using (var decoder = new BrotliStream(input, CompressionMode.Decompress))
        using (var tar = new System.Formats.Tar.TarReader(decoder))
        {
            System.Formats.Tar.TarEntry entry; bool preserved = false;
            while ((entry = tar.GetNextEntry()) != null) if (entry.Name == "dummy.bin")
            { using var reader = new StreamReader(entry.DataStream); preserved = reader.ReadToEnd() == "Lossless compressed dummy evidence."; }
            if (!preserved || Directory.Exists(compressedSource)) throw new IOException("Compressed closure did not preserve original dummy bytes.");
        }
        string collisionSource = Path.Combine(root, "collision-dummy");
        using (var collisionOwner = PilotStorageObservation.Create(collisionSource))
            File.WriteAllText(Path.Combine(collisionSource, "failure.json"), "{\"result\":\"failed-full-nonmedia-rehearsal\"}");
        string collision = Path.Combine(evidence, "collision-dummy.tar"); File.WriteAllText(collision, "Preserve existing destination.");
        string collisionPlan = Path.Combine(root, "collision.json"), collisionWork = Path.Combine(root, "collision-work");
        File.WriteAllText(collisionPlan, JsonSerializer.Serialize(new ReplayDummyClosurePlan(resources with {
            Root = collisionWork, RetainedRoots = [.. history, evidence, collisionSource] }, evidence, [collisionSource], 0, AppendToEvidence: true)));
        bool collisionRejected = false;
        using (var input = File.OpenRead(collisionPlan))
            try { Run(collisionPlan, Convert.ToHexString(SHA256.HashData(input))); }
            catch (IOException ex) when (ex.Message.Contains("already contains")) { collisionRejected = true; }
        if (!collisionRejected || !File.Exists(Path.Combine(collisionSource, "failure.json")) || Directory.Exists(collisionWork)
            || File.ReadAllText(collision) != "Preserve existing destination.") throw new IOException("Append collision was not rejected before source retirement.");
        File.Delete(collision); Directory.CreateDirectory(collision); collisionRejected = false;
        using (var input = File.OpenRead(collisionPlan))
            try { Run(collisionPlan, Convert.ToHexString(SHA256.HashData(input))); }
            catch (IOException ex) when (ex.Message.Contains("already contains")) { collisionRejected = true; }
        if (!collisionRejected || !File.Exists(Path.Combine(collisionSource, "failure.json")) || Directory.Exists(collisionWork)
            || !Directory.Exists(collision)) throw new IOException("Directory collision was not rejected before source retirement.");
        foreach (bool omitSource in new[] { false, true })
        {
            string omitted = omitSource ? sources[0] : evidence;
            string rejectedPlan = Path.Combine(root, omitSource ? "omitted-source.json" : "omitted-destination.json");
            File.WriteAllText(rejectedPlan, JsonSerializer.Serialize(new ReplayDummyClosurePlan(
                resources with { RetainedRoots = resources.RetainedRoots.Where(r => r != omitted).ToArray() }, evidence, sources, 0)));
            bool unchargedRejected = false;
            using (var input = File.OpenRead(rejectedPlan))
                try { Run(rejectedPlan, Convert.ToHexString(SHA256.HashData(input))); }
                catch (IOException ex) when (ex.Message.Contains("charged inventory")) { unchargedRejected = true; }
            if (!unchargedRejected) throw new IOException("Uncharged dummy source/destination was not rejected before writes.");
        }
        string unsafeRoot = Path.Combine(root, "historical-media-role");
        using (var unsafeOwner = PilotStorageObservation.Create(unsafeRoot))
        { File.WriteAllText(Path.Combine(unsafeRoot, "failure.json"), "{\"result\":\"failed-no-retry\"}"); File.WriteAllText(Path.Combine(unsafeRoot, "keep.bin"), "Keep historical role."); }
        string rejectedEvidence = Path.Combine(root, "rejected-evidence"), rejection = Path.Combine(root, "reject.json");
        string unused = Path.Combine(root, "unused-role");
        File.WriteAllText(rejection, JsonSerializer.Serialize(new ReplayDummyClosurePlan(resources with {
            RetainedRoots = [.. history, unsafeRoot, unused, rejectedEvidence] }, rejectedEvidence, [unsafeRoot, unused], 0)));
        bool rejected = false;
        using (var input = File.OpenRead(rejection))
            try { Run(rejection, Convert.ToHexString(SHA256.HashData(input))); } catch (IOException ex) when (ex.Message.Contains("Only this task")) { rejected = true; }
        if (!rejected || Directory.Exists(rejectedEvidence) || File.ReadAllText(Path.Combine(unsafeRoot, "keep.bin")) != "Keep historical role.")
            throw new IOException("Non-dummy role was not rejected before archival or retirement.");
        File.WriteAllText(Path.Combine(root, "closure-checks.json"), JsonSerializer.Serialize(new { result = "passed", archivesPreserveBytes = true,
            nonDummyRejectedBeforeWrite = true, unchargedSourceRejected = true, unchargedDestinationRejected = true,
            appendPreservesOriginalReceipt = true, appendCollisionRejectedBeforeRetirement = true, directoryCollisionRejectedBeforeRetirement = true,
            compressedClosureReadback = true }));
        Console.WriteLine("Dummy closure controls: 8/8 passed"); return 0;
    }
    internal static int Run(string file, string expectedHash)
    {
        using (var input = File.OpenRead(file)) if (Convert.ToHexString(SHA256.HashData(input)) != expectedHash) throw new IOException("Dummy closure plan changed.");
        var plan = JsonSerializer.Deserialize<ReplayDummyClosurePlan>(File.ReadAllText(file));
        if (plan.DummyRoots == null || plan.DummyRoots.Length is < 1 or > 2 || plan.DummyRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.DummyRoots.Length
            || !double.IsFinite(plan.PriorRehearsalSeconds) || plan.PriorRehearsalSeconds < 0)
            throw new IOException("Dummy closure requires one or two declared stopped nonmedia roots.");
        string[] ownedRoots = [Path.GetFullPath(plan.Resources.Root), Path.GetFullPath(plan.EvidenceRoot), .. plan.DummyRoots.Select(Path.GetFullPath)];
        string[] charged = plan.Resources.RetainedRoots.Select(Path.GetFullPath).ToArray();
        if (ownedRoots.Skip(1).Any(r => !charged.Contains(r, StringComparer.OrdinalIgnoreCase)))
            throw new IOException("Every dummy source and destination must belong to the charged inventory.");
        for (int leftIndex = 0; leftIndex < ownedRoots.Length; leftIndex++)
            for (int rightIndex = 0; rightIndex < ownedRoots.Length; rightIndex++)
            {
                if (leftIndex == rightIndex) continue;
                string relative = Path.GetRelativePath(ownedRoots[leftIndex], ownedRoots[rightIndex]);
                if (!Path.IsPathRooted(relative) && (relative == "." || !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative != ".."))
                    throw new IOException("Dummy closure work, evidence and source roots must be disjoint.");
            }
        foreach (var item in plan.Resources.Files)
        { using var input = File.OpenRead(item.Key); if (Convert.ToHexString(SHA256.HashData(input)) != item.Value) throw new IOException("Dummy closure runtime or receipt changed: " + item.Key); }
        if (!plan.Resources.Files.ContainsKey(typeof(ReplayCalibrationDummyClosure).Assembly.Location)) throw new IOException("Dummy closure does not bind its own assembly.");
        foreach (string root in plan.DummyRoots)
        {
            string full = Path.GetFullPath(root), relative = Path.GetRelativePath(PilotStorageObservation.Artifacts, full);
            if (Path.IsPathRooted(relative) || relative == "." || relative.StartsWith("..", StringComparison.Ordinal)) throw new IOException("Dummy closure source is outside owned artifacts.");
            using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(full, PilotStorageObservation.Marker)));
            if (marker.RootElement.GetProperty("root_identity").GetString() != Native.DirectoryIdentity(full)) throw new IOException("Dummy source ownership identity changed.");
            string failure = Path.Combine(full, "failure.json"), diagnostic = Path.Combine(full, "diagnostic.json");
            using var receipt = JsonDocument.Parse(File.ReadAllText(File.Exists(failure) ? failure : diagnostic));
            string kind = receipt.RootElement.GetProperty("result").GetString();
            if (kind != "failed-full-nonmedia-rehearsal" && kind != "diagnostic-completed-not-a-rehearsal-pass")
                throw new IOException("Only this task's closed nonmedia dummy evidence may be retired.");
        }
        if (plan.AppendToEvidence)
        {
            using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(plan.EvidenceRoot, PilotStorageObservation.Marker)));
            using var prior = JsonDocument.Parse(File.ReadAllText(Path.Combine(plan.EvidenceRoot, "dummy-closure.json")));
            if (marker.RootElement.GetProperty("root_identity").GetString() != Native.DirectoryIdentity(plan.EvidenceRoot)
                || prior.RootElement.GetProperty("result").GetString() != "closed-dummy-evidence-preserved")
                throw new IOException("Append destination is not the verified owned dummy-only evidence root.");
        }
        string workName = Path.GetFileName(plan.Resources.Root);
        string traceName = plan.AppendToEvidence ? workName + "-storage.csv" : "dummy-closure-storage.csv";
        string receiptName = plan.AppendToEvidence ? workName + "-closure.json" : "dummy-closure.json";
        string suffix = plan.Compress ? ".tar.br" : ".tar";
        foreach (string name in new[] { traceName, receiptName }.Concat(plan.DummyRoots.SelectMany(r => new[] { Path.GetFileName(r) + suffix, Path.GetFileName(r) + ".members.jsonl" })))
            if (Path.Exists(Path.Combine(plan.EvidenceRoot, name))) throw new IOException("Dummy closure destination already contains this evidence.");
        using IDisposable evidenceOwner = plan.AppendToEvidence ? Native.AnchorDirectory(plan.EvidenceRoot) : PilotStorageObservation.Create(plan.EvidenceRoot);
        using var workOwner = PilotStorageObservation.Create(plan.Resources.Root);
        ReplayCalibrationRun.ValidateResources(plan.Resources);
        var clock = Stopwatch.StartNew(); var receipts = new List<object>();
        using var sampler = new ReplayCalibrationRun.Sampler(plan.Resources, active: false);
        foreach (string root in plan.DummyRoots)
        {
            if (plan.PriorRehearsalSeconds + clock.Elapsed.TotalSeconds + 300 > 1800) throw new IOException("Full dummy closure cannot fit remaining rehearsal time.");
            sampler.BeginOffline("closed-dummy-archive", 24_000_000_000, 300);
            string archive;
            using (var anchor = Native.AnchorDirectory(root)) archive = ReplayCalibrationRehearsal.Compact(root, plan.Resources.Root, sampler, () =>
            { if (plan.PriorRehearsalSeconds + clock.Elapsed.TotalSeconds > 1800) throw new IOException("Dummy closure deadline exceeded."); sampler.Check(); }, compress: plan.Compress);
            string prefix = Path.GetFileName(root);
            string target = Path.Combine(plan.EvidenceRoot, prefix + suffix), manifest = Path.Combine(plan.Resources.Root, "dummy-members.jsonl");
            using var input = File.OpenRead(archive); string hash = Convert.ToHexString(SHA256.HashData(input));
            input.Dispose();
            lock (sampler.Gate)
            { File.Move(archive, target); File.Move(manifest, Path.Combine(plan.EvidenceRoot, prefix + ".members.jsonl")); }
            receipts.Add(new { originalRoot = root, archive = target, sha256 = hash });
            sampler.EndOffline();
        }
        sampler.StopAndCheck(); sampler.Dispose();
        foreach (string root in plan.DummyRoots)
        {
            if (Directory.GetFiles(root).Length != 0 || Directory.GetDirectories(root).Length != 0) throw new IOException("Unexpected dummy material remains; source root retained.");
            Directory.Delete(root, false);
        }
        string trace = Path.Combine(plan.Resources.Root, "storage.csv"); File.Move(trace, Path.Combine(plan.EvidenceRoot, traceName));
        var result = new { result = "closed-dummy-evidence-preserved", planHash = expectedHash, elapsedSeconds = clock.Elapsed.TotalSeconds,
            offlineSeconds = sampler.OfflineSeconds, newMediaChildren = 0, receipts, sampler.PeakUsed,
            retention = "Only verified new dummy originals retired; all four historical failed media roots unchanged." };
        using (var output = new FileStream(Path.Combine(plan.EvidenceRoot, receiptName), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(output, result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("Closed nonmedia dummy evidence preserved: " + plan.EvidenceRoot); return 0;
    }
}
