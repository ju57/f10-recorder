using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    static readonly object nativeChildGate = new();
    static string nativeChildRoot;
    static int nativeChildren;
    static int nativeChildLimit = 48;
    static void AdmitNativeChild(string role)
    {
        if (nativeChildRoot == null) return;
        lock (nativeChildGate)
        {
            Require(nativeChildren < nativeChildLimit, "Authorized native child ceiling reached before launch.");
            int ordinal = ++nativeChildren;
            File.AppendAllText(Path.Combine(nativeChildRoot, "child-admissions.jsonl"), JsonSerializer.Serialize(new { ordinal, role, utc = DateTime.UtcNow }) + "\n");
        }
    }
    internal static bool Exact14(CalibrationPlan p) => p?.ReplayExporterRecipe == ReplayExactExport.Recipe && (p.ProductionQualification || p.MaximumChildren is 48 or 68 or 80 or 96 or 97 or 105 or 118 or 122 or 125 or 129 or 137 or 141 or 145 or 147 or 151);
    static void ValidateExact14(CalibrationPlan p)
    {
        if (p?.ProductionQualification == true) { ValidateProductionQualification(p); return; }
        if (p?.MaximumChildren is 118 or 122 or 125 or 129 or 137 or 141 or 145 or 147 or 151) { ValidateExactContinuation14(p); return; }
        Require(p?.ExactContinuationRoot == null && !p.OperatorForegroundHandoff && !p.UserForegroundHandoff && !p.AutomaticFixtureHandoff, "A legacy admission cannot select continuation policy.");
        bool corrected = p?.MaximumChildren == 68;
        bool leased = p?.MaximumChildren == 80;
        bool readonlyRuntime = p?.MaximumChildren is 96 or 97 or 105;
        Require(p != null && p.Order?.SequenceEqual(Order) == true && p.ReplayExporterRecipe == ReplayExactExport.Recipe
            && p.ValidationChildren == 8 && (readonlyRuntime ? (p.MaximumChildren == 105 ? p.RetainedChildren == 68 : p.MaximumChildren == 97 ? p.RetainedChildren == 60 : p.RetainedChildren == 52) && p.SourceComponentBytes == 850_000_000
                && p.OutputComponentBytes == 1_100_000_000 && p.RetainedBudgetBytes == 18_000_000_000 && p.RetainActiveOutputHandles
                && p.ReadonlyValidationPhases && p.BudgetBytes == (p.MaximumChildren == 105 ? 32_000_000_000 : 30_000_000_000)
                && p.StopBytes == (p.MaximumChildren == 105 ? 30_000_000_000 : 28_000_000_000) && p.DeadlineSeconds == (p.MaximumChildren is 97 or 105 ? 10800 : 9000)
                && p.FileLimit == 32768 && p.DirectoryLimit == 4096 && p.OfflineDeadlineSeconds == 2100
                : (leased ? p.RetainedChildren == 35 && p.SourceComponentBytes == 850_000_000
                && p.OutputComponentBytes == 1_100_000_000 && p.RetainedBudgetBytes == 14_000_000_000 && p.RetainActiveOutputHandles
                && p.BudgetBytes == 26_000_000_000 && p.StopBytes == 24_000_000_000 && p.DeadlineSeconds == 7200
                && p.FileLimit == 24576 && p.DirectoryLimit == 3072
                : (corrected ? p.RetainedChildren == 24 && p.SourceComponentBytes == 850_000_000
                && p.OutputComponentBytes == 800_000_000 && p.RetainedBudgetBytes == 8_000_000_000
                : p.MaximumChildren == 48 && p.RetainedChildren is >= 0 and <= 7 && p.SourceComponentBytes == 1_000_000_000
                && p.OutputComponentBytes == 1_000_000_000 && p.RetainedBudgetBytes == 6_000_000_000)
                && !p.RetainActiveOutputHandles && p.BudgetBytes == 20_000_000_000 && p.StopBytes == 18_000_000_000
                && p.DeadlineSeconds == 5400 && p.FileLimit == 16384 && p.DirectoryLimit == 2048)
                && !p.ReadonlyValidationPhases && p.OfflineDeadlineSeconds == 900)
            && p.FloorBytes == 60_000_000_000 && p.OutsideBuildAllowanceBytes == 268_435_456
            && p.FreezeRetainedAllocations && p.ReuseCurrentMetadataHandles,
            "Issue14 exact native resource boundary changed.");
        Require(p.RetainedRoots is { Length: > 0 and <= 64 } && p.RetainedRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() == p.RetainedRoots.Length
            && p.RetainedRoots.All(Directory.Exists) && double.IsFinite(p.RetainedElapsedSeconds) && p.RetainedElapsedSeconds >= 0
            && p.RetainedElapsedSeconds < (p.MaximumChildren is 97 or 105 ? 6900 : readonlyRuntime ? 5100 : leased ? 3300 : 1500)
            && p.RetainedOfflineSeconds is >= 0 and <= 600 && p.RetainedOfflineSeconds <= p.RetainedElapsedSeconds,
            "Issue14 retained time/evidence cannot fit the new native phase.");
        string root = Path.GetFullPath(p.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var history = p.RetainedRoots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();
        Require(history.All(h => !root.StartsWith(h, StringComparison.OrdinalIgnoreCase) && !h.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            && history.All(h => history.Count(other => h.StartsWith(other, StringComparison.OrdinalIgnoreCase)) == 1),
            "Issue14 mutable root and retained histories must be disjoint.");
    }

    internal static int PrepareIssue14(string path, string engineRoot, string root, string phaseReceipt = null, string retainedDummyRoot = null, bool corrected = false, bool leased = false, bool readonlyRuntime = false, bool queuedTrace = false)
    {
        root = Path.GetFullPath(root); string family = Path.GetDirectoryName(root);
        Require(!Directory.Exists(root) && !File.Exists(root), "Fresh issue14 root required.");
        string[] histories = Directory.GetDirectories(family).Where(p => !Path.GetFullPath(p).Equals(root, StringComparison.OrdinalIgnoreCase)).ToArray();
        MediaDependency.RootDirectory = Path.GetFullPath(engineRoot); string engine = MediaDependency.ResolveExecutable();
        var build = Path.GetDirectoryName(typeof(ReplayCalibrationRun).Assembly.Location);
        var files = new[] { Environment.ProcessPath, typeof(ReplayCalibrationRun).Assembly.Location, Path.Combine(build, "F10Recorder.Windows.dll"),
            Path.Combine(build, "F10Recorder.Core.dll"), Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"), engine }
            .ToDictionary(Path.GetFullPath, HashFile, StringComparer.OrdinalIgnoreCase);
        string launcher = Path.GetFullPath(Path.Combine("tests", "replay-calibration.ps1")); files[launcher] = HashFile(launcher);
        using var audio = new LoopbackAudio(); var bounds = Forms.Screen.PrimaryScreen.Bounds;
        Require(bounds.Width == 1920 && bounds.Height == 1080 && audio.SampleRate == 48000 && audio.Channels == 2,
            "Native reference requires current1920x1080 and48kHz stereo; no display/audio preference changes permitted.");
        var capacity = ReplayStorage.ObserveCapacity(family);
        var environment = new { os = Environment.OSVersion.VersionString, machine = Environment.MachineName,
            bounds.Width, bounds.Height, primaryOutput = Native.PrimaryOutputIndex(), audio.EndpointId, audio.SampleRate, audio.Channels, audio.FfmpegFormat, capacity };
        var inventory = ReplayStorage.Inventory(histories, new ReplayFiles(), readonlyRuntime ? 32768 : leased ? 24576 : 16384, readonlyRuntime ? 4096 : leased ? 3072 : 2048);
        long baseline = ReplayStorage.Bytes(inventory);
        long sourceComponent = corrected ? 850_000_000 : 1_000_000_000, outputComponent = leased ? 1_100_000_000 : corrected ? 800_000_000 : 1_000_000_000;
        long budget = queuedTrace ? 32_000_000_000 : readonlyRuntime ? 30_000_000_000 : leased ? 26_000_000_000 : 20_000_000_000,
            stop = queuedTrace ? 30_000_000_000 : readonlyRuntime ? 28_000_000_000 : leased ? 24_000_000_000 : 18_000_000_000;
        Require(!queuedTrace || corrected && leased && readonlyRuntime, "Queued trace admission requires the complete corrected recipe.");
        int deadline = queuedTrace ? 10800 : readonlyRuntime ? 9000 : leased ? 7200 : 5400;
        long controlEvidence = queuedTrace ? 64_000_000 + 256_000_000 + 67_108_864 : 0;
        long forward = checked(6 * sourceComponent + 6 * outputComponent + 1_010_000_000 + controlEvidence);
        Require(baseline + forward + 268_435_456 < stop
            && baseline + forward + 268_435_456 + 1_256_000_000 <= budget
            && capacity.AvailableBytes >= 60_000_000_000L + budget, "Retained baseline plus six bounded sources/outputs and closure reserves cannot fit.");
        object phase = null; double retainedSeconds = 0, retainedOffline = 0; int retainedChildren = 0;
        if (phaseReceipt != null)
        {
            using var receipt = JsonDocument.Parse(File.ReadAllText(phaseReceipt));
            var r = receipt.RootElement; DateTime started = r.GetProperty("startedUtc").GetDateTime();
            string rehearsal = Path.GetFullPath(r.GetProperty("rehearsalRoot").GetString());
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(rehearsal, "verification.json")));
            Require(result.RootElement.GetProperty("result").GetString() == "passed" && histories.Contains(rehearsal, StringComparer.OrdinalIgnoreCase), "Completed charged rehearsal is required.");
            retainedSeconds = (DateTime.UtcNow - started).TotalSeconds; retainedChildren = r.GetProperty("childrenIncludingPreparation").GetInt32();
            Require(started.Kind == DateTimeKind.Utc && retainedSeconds >= 0 && retainedSeconds + 3900 < deadline
                && (queuedTrace ? retainedChildren == 68 : readonlyRuntime ? retainedChildren == 52 : leased ? retainedChildren == 35 : corrected ? retainedChildren == 24 : retainedChildren is >= 5 and <= 7),
                "Full native schedule/helpers do not fit the remaining authorized phase.");
            files[Path.GetFullPath(phaseReceipt)] = HashFile(phaseReceipt);
            files[Path.Combine(rehearsal, "verification.json")] = HashFile(Path.Combine(rehearsal, "verification.json"));
            if (corrected)
            {
                string closureFile = Path.GetFullPath(r.GetProperty("priorClosure").GetString());
                using var closure = JsonDocument.Parse(File.ReadAllText(closureFile));
                Require(closure.RootElement.GetProperty("nativeAcquisitionsCharged").GetInt32() == (queuedTrace ? 6 : readonlyRuntime ? 4 : leased ? 3 : 2)
                    && closure.RootElement.GetProperty("cumulativeProcessCharges").GetInt32() == (queuedTrace ? 67 : readonlyRuntime ? 51 : leased ? 34 : 19)
                    && closure.RootElement.GetProperty("remainingOwnedMediaProcesses").GetInt32() == 0,
                    "Prior native charges/closure do not fit this proposed correction boundary.");
                files[closureFile] = HashFile(closureFile); retainedOffline = 30;
                string numeric = Path.Combine(Path.GetDirectoryName(path), "corrected-numeric-checks");
                Require(!Directory.Exists(numeric), "Fresh corrected admission controls required.");
                Issue14AdmissionChecks(numeric);
                files[Path.Combine(numeric, "verification.json")] = HashFile(Path.Combine(numeric, "verification.json"));
                if (leased)
                {
                    string probe = Path.GetFullPath(r.GetProperty("metadataProbe").GetString());
                    using var proof = JsonDocument.Parse(File.ReadAllText(probe));
                    Require(proof.RootElement.GetProperty("result").GetString() == "passed", "Full-size output metadata qualification is required.");
                    files[probe] = HashFile(probe); retainedOffline = 60;
                }
                if (readonlyRuntime)
                {
                    string transition = Path.GetFullPath(r.GetProperty("capturedTransition").GetString());
                    using var proof = JsonDocument.Parse(File.ReadAllText(transition));
                    Require(proof.RootElement.GetProperty("result").GetString() == "passed", "Actual captured encode/validation transition qualification is required.");
                    files[transition] = HashFile(transition); retainedOffline = 240;
                    if (queuedTrace)
                    {
                        var t = proof.RootElement;
                        Require(t.GetProperty("traceStallPhase").GetString() == "active" && t.GetProperty("trace").GetProperty("maximumWriteMilliseconds").GetDouble() >= 600
                            && t.GetProperty("trace").GetProperty("failure").ValueKind == JsonValueKind.Null
                            && t.GetProperty("trace").GetProperty("enqueued").GetInt64() == t.GetProperty("trace").GetProperty("written").GetInt64()
                            && t.GetProperty("trace").GetProperty("writerAbsent").GetBoolean(), "Bounded writer active-stall/drain proof is required.");
                        string controls = Path.GetFullPath(r.GetProperty("priorControls").GetString());
                        foreach (string name in new[] { "version", "nonzero", "timeout", "output-limit" })
                        { string control = Path.Combine(controls, name + ".json"); files[control] = HashFile(control); }
                        retainedOffline = 360;
                    }
                }
            }
            phase = new { startedUtc = started, receipt = Path.GetFullPath(phaseReceipt), conservativeRemainingSeconds = 3900, rehearsal };
        }
        var p = new CalibrationPlan(root, MediaDependency.RootDirectory, ReplayReferenceModel.Hash(environment), files, Order, histories, retainedSeconds, retainedChildren,
            MaximumChildren: queuedTrace ? 105 : readonlyRuntime ? 96 : leased ? 80 : corrected ? 68 : 48, BudgetBytes: budget, StopBytes: stop, DeadlineSeconds: deadline,
            RetainedOfflineSeconds: retainedOffline, MetadataHandleRoots: histories, ReuseCurrentMetadataHandles: true,
            DirectoryLimit: readonlyRuntime ? 4096 : leased ? 3072 : 2048, OutputComponentBytes: outputComponent, RetainedBudgetBytes: readonlyRuntime ? 18_000_000_000 : leased ? 14_000_000_000 : corrected ? 8_000_000_000 : 6_000_000_000,
            FileLimit: readonlyRuntime ? 32768 : leased ? 24576 : 16384, OfflineDeadlineSeconds: readonlyRuntime ? 2100 : 900, OutsideBuildAllowanceBytes: 268_435_456,
            FreezeRetainedAllocations: true, ReplayExporterRecipe: ReplayExactExport.Recipe, ValidationChildren: 8,
            RehearsalReuseRoot: retainedDummyRoot == null ? null : Path.GetFullPath(retainedDummyRoot), SourceComponentBytes: sourceComponent,
            RetainActiveOutputHandles: leased, ReadonlyValidationPhases: readonlyRuntime);
        if (p.RehearsalReuseRoot != null)
            Require(histories.Contains(p.RehearsalReuseRoot, StringComparer.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(p.RehearsalReuseRoot, "T01", "buffer", "session-dummy", "segments.csv")),
                "Rehearsal reuse must name retained dummy source evidence.");
        ValidateExact14(p);
        Write(path, new { plan = p, environment, nativePhase = phase, effects = Effects(p), recipe = Recipe(p), baseline,
            commitments = new { sources = checked(6 * sourceComponent), outputs = checked(6 * outputComponent), snapshotPayload = 1_010_000_000L, archives = 0,
                qualificationRequiresSuccessfulHardlinks = true, snapshotsRetainedUnarchived = true, controlsEvidenceAndTrace = controlEvidence, closure = 1_256_000_000L },
            childTable = new { collector = 1, fixtures = 6, captures = 6, exports = 8, runtimeValidators = 8, inspections = 8, controls = queuedTrace ? 0 : 4,
                priorCharges = retainedChildren, spare = queuedTrace ? 0 : readonlyRuntime ? 3 : leased ? 4 : corrected ? 3 : 0 },
            execution_requires_review = true });
        Console.WriteLine(path); return 0;
    }

    static CalibrationPlan VerifyIssue14Execution(CalibrationPlan p, JsonElement document)
    {
        if (p.ProductionQualification) return VerifyProductionExecution(p, document);
        var phase = document.GetProperty("nativePhase");
        Require(phase.ValueKind == JsonValueKind.Object, "Live run requires a reviewed cumulative native-phase receipt and completed rehearsal.");
        int reserve = ExactFinalWorker14(p) ? p.MaximumChildren is 145 or 147 or 151 ? 960 : 1200 : p.UserForegroundHandoff ? 2340 : 2160;
        double elapsed = ExactContinuation14(p) ? ContinuationTiming14(phase, DateTime.UtcNow, reserve, p.MaximumChildren == 151 ? 7200 : p.MaximumChildren == 147 ? 6300 : p.MaximumChildren is 137 or 141 or 145 ? 5400 : 3600).Elapsed
            : (DateTime.UtcNow - phase.GetProperty("startedUtc").GetDateTime()).TotalSeconds;
        Require(elapsed >= p.RetainedElapsedSeconds && elapsed + (ExactContinuation14(p) ? reserve : 3900) < p.DeadlineSeconds, "Complete native schedule no longer fits.");
        VerifyIssue14Family(p);
        return p with { RetainedElapsedSeconds = elapsed };
    }
    static void VerifyIssue14Family(CalibrationPlan p)
    {
        var actual = Directory.GetDirectories(Path.GetDirectoryName(p.Root)).Where(r => !Path.GetFullPath(r).Equals(Path.GetFullPath(p.Root), StringComparison.OrdinalIgnoreCase));
        Require(actual.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(p.RetainedRoots.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            "Retained issue14 family changed after admission.");
    }

    static void ReuseIssue14EngineControls(CalibrationPlan p)
    {
        Require(p.ProductionQualification || ExactContinuation14(p) || p.ReadonlyValidationPhases && (p.MaximumChildren == 97 && p.RetainedChildren == 60 || p.MaximumChildren == 105 && p.RetainedChildren == 68), "Only an explicitly admitted restart may reuse controls.");
        string prior = null;
        foreach (string name in new[] { "version", "nonzero", "timeout", "output-limit" })
        {
            string[] matches = p.Files.Keys.Where(file => Path.GetFileName(file) == name + ".json"
                && Path.GetFileName(Path.GetDirectoryName(file)) == "controls").ToArray();
            Require(matches.Length == 1, "Exactly one hash-bound prior engine control is required.");
            string directory = Path.GetDirectoryName(matches[0]); prior ??= directory;
            Require(directory == prior && p.RetainedRoots.Contains(Path.GetDirectoryName(directory), StringComparer.OrdinalIgnoreCase),
                "Reused controls must belong to one retained attempt.");
            using var document = JsonDocument.Parse(File.ReadAllText(matches[0]));
            string[] expectedArguments = name switch
            {
                "version" or "output-limit" => ["-version"],
                "nonzero" => ["-this-option-is-not-real"],
                _ => ["-hide_banner", "-nostdin", "-re", "-f", "lavfi", "-i", "anullsrc", "-t", "10", "-f", "null", "-"]
            };
            Require(document.RootElement.GetProperty("arguments").Deserialize<string[]>().SequenceEqual(expectedArguments),
                "Prior engine control arguments changed.");
            var receipt = document.RootElement.GetProperty("receipt");
            bool absent = receipt.GetProperty("ProducerAbsent").GetBoolean();
            bool exited = receipt.GetProperty("ActualExitObserved").GetBoolean();
            int? exit = receipt.GetProperty("ExitCode").ValueKind == JsonValueKind.Null ? null : receipt.GetProperty("ExitCode").GetInt32();
            bool success = receipt.GetProperty("Failure").ValueKind == JsonValueKind.Null && absent && exited && exit == 0
                && receipt.GetProperty("CaptureComplete").GetBoolean();
            int limit = name == "output-limit" ? 128 : 2097152;
            Require(absent && exited && receipt.GetProperty("CapturedBytes").GetInt32() is var captured && captured >= 0 && captured <= limit,
                "Prior engine control cleanup/output boundary failed.");
            Require(name switch { "version" => success, "nonzero" => exit.HasValue && exit != 0 && !success,
                "timeout" => receipt.GetProperty("TimedOut").GetBoolean() && !success,
                _ => receipt.GetProperty("OutputLimitExceeded").GetBoolean() && !success },
                "Prior engine control did not demonstrate " + name);
        }
        Issue14AdmissionChecks(Path.Combine(p.Root, "restart-admission-checks"));
        Write(Path.Combine(p.Root, "reused-engine-controls.json"), new { prior, engine = MediaDependency.ExecutableSha256,
            limits = "Same hash-bound managed engine; four prior controls already charged. No new control child." });
    }

    internal static int Issue14AdmissionChecks(string root)
    {
        Directory.CreateDirectory(root); string h = Path.Combine(root, "history"); Directory.CreateDirectory(h);
        var p = new CalibrationPlan(Path.Combine(root, "unused"), "unused", "control", new(), Order, [h], 0, 0,
            MaximumChildren: 48, BudgetBytes: 20_000_000_000, StopBytes: 18_000_000_000, DeadlineSeconds: 5400,
            RetainedOfflineSeconds: 0, MetadataHandleRoots: [h], ReuseCurrentMetadataHandles: true, DirectoryLimit: 2048,
            OutputComponentBytes: 1_000_000_000, RetainedBudgetBytes: 6_000_000_000, FileLimit: 16384,
            OfflineDeadlineSeconds: 900, OutsideBuildAllowanceBytes: 268_435_456, FreezeRetainedAllocations: true,
            ReplayExporterRecipe: ReplayExactExport.Recipe, ValidationChildren: 8);
        ValidateResources(p); int rejected = 0;
        foreach (var bad in new[] { p with { MaximumChildren = 49 }, p with { BudgetBytes = p.BudgetBytes + 1 }, p with { StopBytes = p.StopBytes + 1 },
            p with { DeadlineSeconds = 5401 }, p with { FloorBytes = p.FloorBytes - 1 }, p with { ValidationChildren = 7 },
            p with { RetainedChildren = 8 }, p with { RetainedRoots = [root] }, p with { FreezeRetainedAllocations = false },
            p with { ReplayExporterRecipe = "copy" }, p with { Order = Order.Reverse().ToArray() } })
        { bool failed = false; try { ValidateExact14(bad); } catch (IOException) { failed = true; } Require(failed, "Changed exact boundary passed."); rejected++; }
        var correction = p with { MaximumChildren = 68, RetainedChildren = 24, SourceComponentBytes = 850_000_000,
            OutputComponentBytes = 800_000_000, RetainedBudgetBytes = 8_000_000_000 };
        ValidateResources(correction);
        foreach (var bad in new[] { correction with { MaximumChildren = 69 }, correction with { RetainedChildren = 25 },
            correction with { SourceComponentBytes = 850_000_001 }, correction with { OutputComponentBytes = 800_000_001 },
            correction with { RetainedBudgetBytes = 8_000_000_001 }, correction with { BudgetBytes = 20_000_000_001 },
            correction with { StopBytes = 18_000_000_001 }, correction with { DeadlineSeconds = 5401 } })
        { bool failed = false; try { ValidateExact14(bad); } catch (IOException) { failed = true; } Require(failed, "Changed correction boundary passed."); rejected++; }
        var lease = correction with { MaximumChildren = 80, RetainedChildren = 35, RetainActiveOutputHandles = true,
            BudgetBytes = 26_000_000_000, StopBytes = 24_000_000_000, DeadlineSeconds = 7200,
            OutputComponentBytes = 1_100_000_000, RetainedBudgetBytes = 14_000_000_000, FileLimit = 24576, DirectoryLimit = 3072 };
        ValidateResources(lease);
        foreach (var bad in new[] { lease with { MaximumChildren = 81 }, lease with { RetainedChildren = 36 },
            lease with { RetainActiveOutputHandles = false }, lease with { BudgetBytes = lease.BudgetBytes + 1 },
            lease with { StopBytes = lease.StopBytes + 1 }, lease with { DeadlineSeconds = 7201 },
            lease with { OutputComponentBytes = lease.OutputComponentBytes + 1 }, lease with { FileLimit = 24577 } })
        { bool failed = false; try { ValidateExact14(bad); } catch (IOException) { failed = true; } Require(failed, "Changed output-lease boundary passed."); rejected++; }
        var readOnly = lease with { MaximumChildren = 96, RetainedChildren = 52, ReadonlyValidationPhases = true,
            BudgetBytes = 30_000_000_000, StopBytes = 28_000_000_000, DeadlineSeconds = 9000,
            RetainedBudgetBytes = 18_000_000_000, FileLimit = 32768, DirectoryLimit = 4096, OfflineDeadlineSeconds = 2100 };
        ValidateResources(readOnly);
        foreach (var bad in new[] { readOnly with { MaximumChildren = 97 }, readOnly with { RetainedChildren = 53 },
            readOnly with { ReadonlyValidationPhases = false }, readOnly with { BudgetBytes = readOnly.BudgetBytes + 1 },
            readOnly with { StopBytes = readOnly.StopBytes + 1 }, readOnly with { DeadlineSeconds = 9001 },
            readOnly with { OfflineDeadlineSeconds = 2101 }, readOnly with { RetainActiveOutputHandles = false } })
        { bool failed = false; try { ValidateExact14(bad); } catch (IOException) { failed = true; } Require(failed, "Changed read-only validation boundary passed."); rejected++; }
        var restart = readOnly with { MaximumChildren = 97, RetainedChildren = 60, DeadlineSeconds = 10800 };
        ValidateResources(restart); RequireExactExporterAdmission(restart);
        foreach (var bad in new[] { restart with { MaximumChildren = 96 }, restart with { RetainedChildren = 59 },
            restart with { DeadlineSeconds = 10801 }, restart with { ReadonlyValidationPhases = false } })
        { bool failed = false; try { ValidateExact14(bad); } catch (IOException) { failed = true; } Require(failed, "Changed restart boundary passed."); rejected++; }
        var queued = restart with { MaximumChildren = 105, RetainedChildren = 68, RetainedOfflineSeconds = 360, RetainedElapsedSeconds = 360,
            BudgetBytes = 32_000_000_000, StopBytes = 30_000_000_000 };
        ValidateResources(queued); RequireExactExporterAdmission(queued);
        foreach (var bad in new[] { queued with { MaximumChildren = 106 }, queued with { RetainedChildren = 67 }, queued with { DeadlineSeconds = 10801 },
            queued with { BudgetBytes = queued.BudgetBytes + 1 }, queued with { StopBytes = queued.StopBytes + 1 } })
        { bool failed = false; try { ValidateExact14(bad); } catch (IOException) { failed = true; } Require(failed, "Changed queued-trace boundary passed."); rejected++; }
        string cell = Path.Combine(root, "worker-cache-control"); Directory.CreateDirectory(Path.Combine(cell, "worker-profile"));
        string cache = Issue14WorkerCase.CacheRoot(cell);
        string[] args = ["-i", "unused", Path.Combine(cache, "session-control", "chunk-%09d.ts")];
        Require(ReplayCalibrationStartup.NormalizeCaptureArguments(args, cell, cache)[^1] == Path.Combine("<session>", "chunk-%09d.ts"), "Exact worker cache root did not normalize.");
        bool wrongCache = false; try { ReplayCalibrationStartup.NormalizeCaptureArguments(args, cell); } catch (IOException) { wrongCache = true; }
        Require(wrongCache, "A direct cache root was accepted for a profile-scoped worker cache.");
        var traceControls = BoundedCalibrationTraceChecks.Run();
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = "passed", exactFit = true, rejected, traceControls,
            effects = "Tiny nonmedia files, numeric/path/order controls and bounded in-memory writer stall/overflow/error checks; no devices, engine, UI or model activation." }));
        return 0;
    }
}
