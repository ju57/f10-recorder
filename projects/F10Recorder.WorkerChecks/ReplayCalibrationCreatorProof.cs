using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    internal static int CreatorProofChecks(string root, string reference)
    {
        Require(root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && new FileInfo(reference).Length <= 65536, "Fresh small creator-proof fixture required.");
        using var original = System.Text.Json.JsonDocument.Parse(File.ReadAllText(reference));
        var plan = original.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>() with { Root = root };
        Directory.CreateDirectory(root); string cell = Path.Combine(root, "H02"); Directory.CreateDirectory(cell);
        File.Copy(reference, Path.Combine(root, "admitted-plan.json"));
        string raw = Path.Combine(cell, "controlled.raw"); File.WriteAllBytes(raw, [0, 64, 128, 255]);
        string[] args = ["-hide_banner", "-loglevel", "warning", "-f", "rawvideo", "-pixel_format", "gray", "-video_size", "2x2",
            "-framerate", "1", "-i", raw, "-frames:v", "1", "-f", "null", "-"];
        string arguments = Path.Combine(cell, "controlled-arguments.json"); Write(arguments, args);
        var prior = (nativeChildRoot, nativeChildren, nativeChildLimit);
        try
        {
            nativeChildRoot = root; nativeChildren = 0; nativeChildLimit = 1;
            MediaDependency.RootDirectory = plan.EngineRoot;
            AdmitNativeChild("offline-inspection:H02");
            var receipt = ReplayExportProcess.Run(MediaDependency.ResolveExecutable(), args, cell, null, timeoutMilliseconds: 5000);
            PublishInspectionCreatorProof(plan, cell, "H02", arguments, args, receipt);
            Require(receipt.Success && receipt.ProducerAbsent && receipt.ActualExitObserved, "Tiny null-output decoder did not close normally.");
            Write(Path.Combine(root, "checks.json"), new { result = "creator-proof-emitted", originalPlanHash = CreatorPlanHash(plan),
                wrapperHash = HashFile(reference), receipt.ProcessId, receipt.ProcessStartedUtc,
                meaning = "Actual creator emitter over the preserved generated wrapper and one owned joined2x2 raw/null frame. No live capture/audio/UI/playable output/catalog/settings; not qualification." });
            return 0;
        }
        finally { (nativeChildRoot, nativeChildren, nativeChildLimit) = prior; }
    }
    static string CreatorPlanHash(CalibrationPlan plan)
    {
        using var admitted = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(plan.Root, "admitted-plan.json")));
        string hash = admitted.RootElement.GetProperty("planHash").GetString();
        Require(hash?.Length == 64 && hash.All(Uri.IsHexDigit), "Creator lacks the owning original-plan identity.");
        return hash;
    }
    static int CreatorAdmission(CalibrationPlan plan, string label, string role, int index, DateTime childStarted)
    {
        string file = Path.Combine(plan.Root, "child-admissions.jsonl");
        Require(new FileInfo(file).Length <= 65536, "Creator admission log bound.");
        var rows = File.ReadAllLines(file).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
        try
        {
            var matching = rows.Where(row => row.RootElement.GetProperty("role").GetString() == role + ":" + label).ToArray();
            Require(index >= 0 && index < matching.Length, "Creator stage has no admitted role.");
            var row = matching[index].RootElement;
            Require(row.GetProperty("utc").GetDateTime() <= childStarted, "Creator role was not admitted before creation.");
            return row.GetProperty("ordinal").GetInt32();
        }
        finally { foreach (var row in rows) row.Dispose(); }
    }
    // Qualification-only retrospective evidence, emitted after Cache completion.
    // Never participates in product timing or supplies PID-based cleanup power.
    static void PublishCreatorProofs(CalibrationPlan plan, string cell, string label, string variant,
        CaptureSession capture, int exportArgumentCount)
    {
        if (!CapacityQualification(plan) && plan.FreshCandidateAdmission is not (RetainedCoverageAdmission or SameCaptureRetainedAdmission)) return;
        using var parent = Process.GetCurrentProcess();
        var job = capture.ProducerProof;
        Require(job.WorkerId == parent.Id && job.WorkerStartedUtcTicks == parent.StartTime.ToUniversalTime().Ticks
            && job.Sealed && !string.IsNullOrWhiteSpace(job.JobName), "Creator proof requires the actual sealed producer job.");
        var children = capture.LastExportProcesses;
        Require(children.Count >= (variant == "packet" ? 0 : 1) && children.Count <= 2
            && exportArgumentCount >= children.Count, "Creator export-stage count is invalid.");
        string engine = MediaDependency.ResolveExecutable();
        Require(plan.Files.GetValueOrDefault(engine) == HashFile(engine), "Creator engine changed.");
        void Publish(ReplayExportProcessReceipt child, string role, int stage, string argumentsFile, string[] arguments)
        {
            Require(child is { Success: true, ResourcesFinal: true, ResourceError: null, ProcessId: > 0, ProcessStartedUtc: not null }
                && arguments.Length is > 0 and <= 256, "Creator stage has incomplete actual exit/resources.");
            string name = $"creator-exit-{variant}-{role}-{stage}.json";
            string final = Path.Combine(cell, name), temporary = Path.Combine(cell, "." + name + ".tmp");
            var receipt = SaveProcess(cell, $"creator-{variant}-{role}-{stage}", child);
            object stagedInput = null;
            int inputAt = Array.IndexOf(arguments, "-i");
            if (inputAt >= 0 && inputAt + 1 < arguments.Length && arguments[inputAt + 1] == "pipe:0")
            {
                var timing = capture.LastPreparationTiming;
                Require(variant == "packet" && role == "export" && stage == 0 && ReferenceEquals(child, children[0])
                    && timing is { FeedJoined: true, Aborted: false, Error: null, FeedSha256.Length: 64, FedBytes: > 0 and <= 8388608 }
                    && timing.Joined >= timing.RunCompleted && timing.Joined >= timing.FeedCompleted && timing.FeedCompleted > 0,
                    "Pipe creator requires the actual normally joined staged producer and verified feeder.");
                var saveTiming = capture.LastExportTiming;
                Require(saveTiming != null && saveTiming.CompletionReady.HasValue && timing.Started >= saveTiming.Requested
                    && timing.Joined <= saveTiming.CompletionReady.Value, "Staged creator must belong to this completed save, not an earlier preparation.");
                string witnessFile = Path.Combine(cell, "native-export-witness.json");
                Require(new FileInfo(witnessFile).Length is > 0 and <= 65536, "Bounded staged native witness required.");
                using var witnessDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(witnessFile));
                var witness = witnessDocument.RootElement;
                Require(witness.GetProperty("preparationSelected").GetBoolean() && witness.GetProperty("preparationJoined").GetBoolean()
                    && witness.GetProperty("scopeClosed").GetBoolean()
                    && witness.GetProperty("preparation").GetProperty("FeedSha256").GetString() == timing.FeedSha256
                    && witness.GetProperty("preparation").GetProperty("FedBytes").GetInt64() == timing.FedBytes,
                    "Staged creator differs from the actual closed native request witness.");
                var selection = capture.LastOutputSelection;
                Require(selection is { Closed: true } && witness.GetProperty("selectionIdentity").GetString() == selection.Identity,
                    "Staged creator selection differs from its actual closed request.");
                var input = selection.Inputs.Single(s => s.Start <= selection.Start && s.End > selection.Start);
                var info = ((IReplaySessionSource)capture).ReplayInfo;
                var projected = ReplayHybridPlan.ProjectPictures(((long)Math.Round(input.Start * info.FramesPerSecond), (long)Math.Round(input.End * info.FramesPerSecond)),
                    ReplayHybridPlan.FrameAtOrAfter(selection.Start, info.FramesPerSecond), ReplayHybridPlan.FrameAtOrAfter(selection.End, info.FramesPerSecond));
                string partial = witness.GetProperty("Partial").GetString();
                string boundary = ReplayOutputWorkspace.BoundaryPath(partial, 0);
                var expected = ReplaySaveExport.BoundaryArguments(info, "pipe:0", projected.First, projected.Count, boundary).ToList();
                expected.InsertRange(expected.IndexOf("-i"), ["-f", "mpegts"]);
                Require(arguments.SequenceEqual(expected) && input.Extent.Length == timing.FedBytes
                    && HashFile(input.Path) == timing.FeedSha256, "Staged creator arguments or protected source bytes differ.");
                stagedInput = new { kind = "joined-protected-staged-feed-v1", witnessFile, witnessSha256 = HashFile(witnessFile),
                    selectionIdentity = selection.Identity, sourcePath = input.Path, sourceBytes = timing.FedBytes, sourceSha256 = timing.FeedSha256,
                    first = projected.First, count = projected.Count, partial, boundary, timing };
            }
            int roleIndex = role == "export" ? int.Parse(Path.GetFileNameWithoutExtension(argumentsFile)["export-arguments-".Length..]) - 1 : 0;
            Write(temporary, new { kind = "qualification-child-creator-v1", planSha256 = CreatorPlanHash(plan),
                admittedPlanSha256 = HashFile(Path.Combine(plan.Root, "admitted-plan.json")),
                helperSha256 = HashFile(typeof(ReplayCalibrationRun).Assembly.Location), parentPid = parent.Id,
                parentStartedUtc = parent.StartTime.ToUniversalTime(), label, variant, role, stage,
                admissionOrdinal = CreatorAdmission(plan, label, role, roleIndex, child.ProcessStartedUtc.Value),
                enginePath = engine, engineSha256 = HashFile(engine), ownership = "sealed-capture-job", job, argumentsFile,
                argumentsSha256 = HashFile(argumentsFile), arguments, receipt, stagedInput });
            File.Move(temporary, final); // Readers never see a partly flushed proof.
        }
        for (int i = 0; i < children.Count; i++)
        {
            string argumentsFile = Path.Combine(cell, $"export-arguments-{exportArgumentCount - children.Count + i + 1}.json");
            var arguments = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(argumentsFile));
            Publish(children[i], "export", i, argumentsFile, arguments);
        }
        if (capture.LastValidation != null)
        {
            string argumentsFile = Path.Combine(cell, $"creator-{variant}-validation-arguments.json");
            Write(argumentsFile, capture.ValidationArguments);
            Publish(capture.LastValidation, "runtime-validation", 0, argumentsFile, capture.ValidationArguments);
        }
    }

    static void PublishInspectionCreatorProof(CalibrationPlan plan, string root, string label, string argumentsFile,
        string[] arguments, ReplayExportProcessReceipt child)
    {
        if (plan == null || !CapacityQualification(plan) && plan.FreshCandidateAdmission is not (RetainedCoverageAdmission or SameCaptureRetainedAdmission)) return;
        Require(child is { Success: true, ResourcesFinal: true, ResourceError: null }, "Inspection creator closure is incomplete.");
        using var parent = Process.GetCurrentProcess();
        string cell = Path.Combine(plan.Root, label), variant = Path.GetFullPath(root) == Path.GetFullPath(cell) ? "packet" : "legacy";
        string engine = MediaDependency.ResolveExecutable();
        Require(plan.Files.GetValueOrDefault(engine) == HashFile(engine), "Inspection creator engine changed.");
        string name = $"creator-exit-{variant}-offline-inspection-0.json", temporary = Path.Combine(cell, "." + name + ".tmp");
        var receipt = SaveProcess(root, "creator-inspection", child);
        Write(temporary, new { kind = "qualification-child-creator-v1", planSha256 = CreatorPlanHash(plan),
            admittedPlanSha256 = HashFile(Path.Combine(plan.Root, "admitted-plan.json")),
            helperSha256 = HashFile(typeof(ReplayCalibrationRun).Assembly.Location), parentPid = parent.Id,
            parentStartedUtc = parent.StartTime.ToUniversalTime(), label, variant, role = "offline-inspection", stage = 0,
            admissionOrdinal = CreatorAdmission(plan, label, "offline-inspection", variant == "packet" ? 0 : 1, child.ProcessStartedUtc.Value),
            enginePath = engine, engineSha256 = HashFile(engine), ownership = "direct-held-joined-independent-inspection", job = (object)null,
            argumentsFile, argumentsSha256 = HashFile(argumentsFile), arguments, receipt });
        File.Move(temporary, Path.Combine(cell, name));
    }
}
