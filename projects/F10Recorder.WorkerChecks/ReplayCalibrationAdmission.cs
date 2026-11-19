using System.Text.Json;

namespace F10Recorder;

internal sealed partial record CalibrationPlan
{
    public Dictionary<string, string> EvidenceRoles { get; init; }
}

internal enum CalibrationAdmissionMode { Capture, ValidateOnly, CheckLaunch }

internal static partial class ReplayCalibrationRun
{
    internal const string FinalAdmissionKind = "final-calibration-admission-v1";
    static readonly string[] CommonAdmissionRoles = ["source-manifest", "host-facts", "preservation-before", "run-instructions", "retained-attempt", "resources", "rehearsal"];
    static readonly string[] OutsideAdmissionRoles = ["outside-storage-baseline", "outside-inventory", "outside-inventory-plan", "outside-forward-envelope", "outside-outstanding-writes", "fresh-candidate-qualification"];
    static Dictionary<string, string> PreparedEvidenceRoles(string admission, Dictionary<string, string> files)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string role in CommonAdmissionRoles.Concat(OutsideAdmissionRoles).Concat(["composite-training-bindings","prior-failed-attempt","duration-failure-disposition","coverage-freeze",FiveMinuteContractRole,PresentationMethod.Role]))
        {
            string path = Path.Combine(admission, role == "run-instructions" ? "RUN.md" : role + ".json");
            if (files.ContainsKey(path)) result.Add(role, path);
        }
        return result;
    }
    static string EvidenceRole(CalibrationPlan plan, string role)
    {
        Require(plan?.EvidenceRoles != null && plan.EvidenceRoles.TryGetValue(role, out _), "Missing explicit evidence role: " + role);
        string path = plan.EvidenceRoles[role];
        Require(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && plan.Files.ContainsKey(path), "Unbound explicit evidence role: " + role);
        return path;
    }
    static JsonDocument ReadFinalAdmission(string path, string expectedHash)
    {
        Require(Path.IsPathFullyQualified(path), "Absolute final plan path required.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(input.Length is > 0 and <= 65536, "Final plan exceeds64KiB.");
        byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        Require(input.ReadByte() == -1 && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) == expectedHash,
            "Reviewed final plan identity mismatch.");
        return JsonDocument.Parse(bytes);
    }
    static void RequireUniqueJsonMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { Require(names.Add(property.Name), "Duplicate JSON member: " + property.Name); RequireUniqueJsonMembers(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniqueJsonMembers(item);
    }
    static int MinimumFixedPurposeSeconds(CalibrationPlan plan)
        => ContextQualification(plan) ? 1200 : DurationRetainedHistory(plan) && plan.ReplayMinutes == 1 ? 2400 : 3600;
    static bool FixedPurposeWindowValid(CalibrationPlan plan, DateTime purpose, DateTime expires, DateTime now)
    {
        double remaining = (purpose - now).TotalSeconds;
        return purpose.Kind == DateTimeKind.Utc && remaining >= MinimumFixedPurposeSeconds(plan)
            && remaining <= plan.DeadlineSeconds && purpose < expires;
    }
    static CalibrationPlan ValidateFinalAdmission(string path, string hash, JsonDocument document, out string engine)
    {
        var root = document.RootElement;
        RequireUniqueJsonMembers(root);
        var plan = root.GetProperty("plan").Deserialize<CalibrationPlan>();
        Require(plan?.Files != null, "Final plan bindings are absent.");
        string directory = Path.GetDirectoryName(path);
        Require(root.GetProperty("controllerKind").GetString() == (FiveMinuteStorage(plan) ? "issue83-five-minute-c00-owner-v1" : SameCaptureQualification(plan) ? "issue83-two-confirmation-owner-v2" : OwnedHistoryQualification(plan) ? "issue83-owned-history-cohort-v1" : "issue83-fresh-guard2-cohort-v1")
            && root.GetProperty("parentHandleBudget").GetInt32() == (SameCaptureQualification(plan) ? 49152 : OwnedHistoryQualification(plan) ? CompositeParentHandleBudget(plan) : 98304)
            && root.GetProperty("maximumSupportChildren").GetInt32() == 1
            && root.GetProperty("maximumManagedRoles").GetInt32() == (SameCaptureQualification(plan) ? 16 : 61) && root.GetProperty("maximumOuterRoles").GetInt32() == 1
            && root.GetProperty("controlCreatorEvidence").GetString() == "prefixture-held-instance-control-receipt-v1"
            && root.GetProperty("stopFile").GetString() == Path.Combine(directory, "STOP.REQUEST")
            && root.GetProperty("watchStatusFile").GetString() == Path.Combine(directory, "NEXT-HANDOFF.json")
            && !root.TryGetProperty("fault", out _) && !root.TryGetProperty("lifecycleMode", out _), "Final controller contract or owned paths changed.");
        foreach (string role in new[] { "entryPath", "runtimeManifest", "controllerRuntime" })
            Require(plan.Files.ContainsKey(root.GetProperty(role).GetString()), "Final controller/runtime role is unbound: " + role);
        DateTime created = root.GetProperty("createdUtc").GetDateTime(), expires = root.GetProperty("expiresUtc").GetDateTime();
        Require(created.Kind == DateTimeKind.Utc && expires.Kind == DateTimeKind.Utc && created <= DateTime.UtcNow
            && DateTime.UtcNow.AddSeconds(plan.DeadlineSeconds) < expires, "Final admission expired or full execution window does not fit.");
        DateTime purpose = root.GetProperty("originalPurposeDeadlineUtc").GetDateTime();
        Require(FixedPurposeWindowValid(plan, purpose, expires, DateTime.UtcNow),
            $"Fixed purpose window must retain{MinimumFixedPurposeSeconds(plan) / 60}minutes and fit the admitted deadline without reset.");
        if(OwnedHistoryRepair(plan))ValidateOwnedHistoryPriorAttempt(plan,root.GetProperty("originalPurposeDeadlineUtc").GetString());
        Require(!Directory.Exists(plan.Root) && !File.Exists(plan.Root) && Path.GetFullPath(plan.Root).StartsWith(
            Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Final admission requires a fresh task artifact root.");
        if (FiveMinuteStorage(plan)) _ = ReadFiveMinuteContract(plan);
        else if (ContextQualification(plan)) _ = ReadContextFreeze(plan);
        var roles = root.GetProperty("plan").GetProperty("EvidenceRoles");
        Require(roles.ValueKind == JsonValueKind.Object, "Explicit evidence roles must be an object.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles.EnumerateObject())
        {
            Require(names.Add(role.Name) && role.Value.ValueKind == JsonValueKind.String, "Duplicate or invalid explicit evidence roles.");
            string rolePath = role.Value.GetString();
            Require(!string.IsNullOrWhiteSpace(rolePath) && Path.IsPathFullyQualified(rolePath)
                && paths.Add(Path.GetFullPath(rolePath)) && plan.Files.ContainsKey(rolePath), "Duplicate, aliased or unbound explicit evidence roles.");
        }
        foreach (string role in CommonAdmissionRoles) _ = EvidenceRole(plan, role);
        if (plan.CompositeQualification) foreach (string role in OutsideAdmissionRoles) _ = EvidenceRole(plan, role);
        if (!plan.CompositeQualification) RequireExactExporterAdmission(plan);
        ValidateResources(plan);
        if (Exact14(plan)) plan = VerifyIssue14Execution(plan, root);
        foreach (var file in plan.Files) Require(HashFile(file.Key) == file.Value, "Runtime identity changed: " + file.Key);
        string runtimeManifest = root.GetProperty("runtimeManifest").GetString();
        Require(new FileInfo(runtimeManifest).Length is > 0 and <= 262144, "Runtime closure manifest exceeds bound.");
        using (var runtime = JsonDocument.Parse(File.ReadAllBytes(runtimeManifest)))
        {
            RequireUniqueJsonMembers(runtime.RootElement);
            Require(runtime.RootElement.EnumerateObject().Count() == 475, "Exact475-member runtime closure required.");
            foreach (var file in runtime.RootElement.EnumerateObject()) Require(HashFile(file.Name) == file.Value.GetString(), "Runtime closure changed: " + file.Name);
        }
        string assembly = Path.GetFullPath(typeof(ReplayCalibrationRun).Assembly.Location), host = Path.GetFullPath(Environment.ProcessPath);
        Require(plan.Files.ContainsKey(assembly) && plan.Files.ContainsKey(host), "Own assembly/host is not bound.");
        foreach (string required in new[] { "F10Recorder.Windows.dll", "F10Recorder.Core.dll", "F10Recorder.WorkerChecks.deps.json", "F10Recorder.WorkerChecks.runtimeconfig.json" })
            Require(plan.Files.ContainsKey(Path.Combine(Path.GetDirectoryName(assembly), required)), "Required runtime dependency is unbound.");
        MediaDependency.RootDirectory = plan.EngineRoot;
        engine = MediaDependency.ResolveExecutable(); // Static executable verification only; never starts an engine.
        Require(plan.Files.GetValueOrDefault(Path.GetFullPath(engine)) == MediaDependency.ExecutableSha256.ToUpperInvariant(), "Managed engine identity missing.");
        foreach (string image in new[] { "dotnet.exe", "F10Recorder.WorkerChecks.dll", "ffmpeg.exe" })
            Require(plan.Files.Keys.Count(file => Path.GetFileName(file) == image) == 1, "Final executable role is ambiguous: " + image);
        Require(plan.Files.Keys.Contains(Path.Combine(Environment.SystemDirectory, "conhost.exe"), StringComparer.OrdinalIgnoreCase), "Console image is unbound.");
        if (plan.CompositeQualification) ValidateCompositeOutsideBaseline(plan);
        if (CapacityTrainingContinuation(plan)) _ = ReadCompleteCapacityTraining(plan);
        var environment = root.GetProperty("environment");
        Require(environment.GetProperty("os").GetString() == Environment.OSVersion.VersionString
            && environment.GetProperty("machine").GetString() == Environment.MachineName
            && environment.GetProperty("Width").GetInt32() == 1920 && environment.GetProperty("Height").GetInt32() == 1080
            && environment.GetProperty("SampleRate").GetInt32() == 48000 && environment.GetProperty("Channels").GetInt32() == 2,
            "Bound host/reference environment is incompatible.");
        var capacity = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(plan.Root));
        Require(capacity.Volume == environment.GetProperty("capacity").GetProperty("Volume").GetString()
            && capacity.AllocationUnit == environment.GetProperty("capacity").GetProperty("AllocationUnit").GetInt64()
            && new DriveInfo(Path.GetPathRoot(plan.Root)).DriveFormat == "NTFS"
            && capacity.AvailableBytes >= QualificationFreeRequirement(plan) && (!FiveMinuteStorage(plan) || capacity.AvailableBytes >= 90_000_000_000),
            "Current volume/capacity does not meet final admission.");
        Require(HashFile(path) == hash, "Final plan changed during validation.");
        return plan;
    }
    static string AdmissionReceiptPath(string planPath) => Path.Combine(Path.GetDirectoryName(planPath), "PREFLIGHT.json");
    static void WriteFinalAdmissionReceipt(string path, string hash, JsonDocument document)
    {
        Write(AdmissionReceiptPath(path), new { kind = FinalAdmissionKind, result = "passed", planPath = Path.GetFullPath(path), planSha256 = hash,
            helperSha256 = HashFile(typeof(ReplayCalibrationRun).Assembly.Location), createdUtc = DateTime.UtcNow,
            expiresUtc = document.RootElement.GetProperty("expiresUtc").GetDateTime(), sourceEntered = false, captureStarted = false,
            audioStarted = false, engineStarted = false, currentDeviceMetadataDeferredUntilAuthorizedLaunch = true });
        Console.WriteLine("Passed final calibration admission: " + hash);
    }
    static void RequireFinalAdmissionReceipt(string path, string hash, JsonDocument document)
    {
        string receipt = AdmissionReceiptPath(path);
        Require(File.Exists(receipt) && new FileInfo(receipt).Length is > 0 and <= 8192, "Successful final-plan preflight receipt required.");
        using var input = JsonDocument.Parse(File.ReadAllBytes(receipt)); var row = input.RootElement;
        Require(row.GetProperty("kind").GetString() == FinalAdmissionKind && row.GetProperty("result").GetString() == "passed"
            && row.GetProperty("planPath").GetString() == Path.GetFullPath(path) && row.GetProperty("planSha256").GetString() == hash
            && row.GetProperty("helperSha256").GetString() == HashFile(typeof(ReplayCalibrationRun).Assembly.Location)
            && row.GetProperty("createdUtc").GetDateTime().Kind == DateTimeKind.Utc
            && row.GetProperty("createdUtc").GetDateTime() <= DateTime.UtcNow
            && row.GetProperty("expiresUtc").GetDateTime() == document.RootElement.GetProperty("expiresUtc").GetDateTime()
            && DateTime.UtcNow < row.GetProperty("expiresUtc").GetDateTime(), "Preflight receipt does not validate this exact final plan/helper/window.");
    }
    static void RequireRecordingAuthorization(string path, string hash, JsonDocument document)
    {
        string file = Path.Combine(Path.GetDirectoryName(path), "current-authorization.json");
        Require(File.Exists(file) && new FileInfo(file).Length is > 0 and <= 8192, "Fresh recording authorization required.");
        using var input = JsonDocument.Parse(File.ReadAllBytes(file)); var approval = input.RootElement; var plan = document.RootElement;
        if (plan.GetProperty("plan").TryGetProperty("FreshCandidateAdmission", out var admission)
            && admission.ValueKind == JsonValueKind.String && admission.GetString() == FiveMinuteStorageAdmission)
        {
            DateTime granted = approval.GetProperty("approvedUtc").GetDateTime();
            Require(granted.Kind == DateTimeKind.Utc && DateTime.UtcNow - granted <= TimeSpan.FromMinutes(15),
                "Five-minute C00 launch grant expired.");
        }
        Require(approval.GetProperty("approvedPlanSha256").GetString() == hash && approval.GetProperty("approvedCaptures").GetInt32() == plan.GetProperty("plan").GetProperty("Order").GetArrayLength()
            && !string.IsNullOrWhiteSpace(approval.GetProperty("questionItemId").GetString())
            && approval.GetProperty("approvedUtc").GetDateTime() <= DateTime.UtcNow
            && approval.GetProperty("originalPurposeDeadlineUtc").GetString() == plan.GetProperty("originalPurposeDeadlineUtc").GetString()
            && approval.GetProperty("aggregatePromisesBytes").GetInt64() == plan.GetProperty("aggregatePromiseBytes").GetInt64()
            && approval.GetProperty("approvedAggregateEarlyBytes").GetInt64() == plan.GetProperty("proposedAggregateEarlyBytes").GetInt64()
            && approval.GetProperty("approvedAggregateHardBytes").GetInt64() == plan.GetProperty("proposedAggregateHardBytes").GetInt64()
            && approval.GetProperty("minimumFreeBytes").GetInt64() == 60_000_000_000, "Recording/resource authorization does not match this exact final plan.");
    }
    static int ReportStartupFailure(string path, string hash, string phase, Exception error)
    {
        var report = new { result = "failed-before-source", phase, planSha256 = hash, errorType = error.GetType().Name,
            cause = error.Message, captureStarted = false, audioStarted = false, engineStarted = false, utc = DateTime.UtcNow };
        string json = JsonSerializer.Serialize(report);
        try { Write(Path.Combine(Path.GetDirectoryName(path), "STARTUP-FAILURE-" + Guid.NewGuid().ToString("N") + ".json"), report); }
        catch (Exception writeError) { Console.Error.WriteLine("Startup report write failed: " + writeError.GetType().Name); }
        Console.Error.WriteLine(json);
        return 1;
    }
}
