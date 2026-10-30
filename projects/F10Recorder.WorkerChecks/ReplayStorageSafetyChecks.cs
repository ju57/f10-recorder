using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// Controlled byte bounds exercise policy only; they are not calibration evidence.
internal static class ReplayStorageSafetyChecks
{
    const string Volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";
    const string OtherVolume = @"\\?\Volume{00000000-0000-0000-0000-000000000002}\";
    static readonly ReplayCaptureHeadroom ControlledHeadroom = new(new string('A', 64), Volume, 4096, 4096, 8192);
    static ReplayStorageAssessment Assess(long bytes, ReplayCaptureHeadroom headroom = null) => ReplayStorageSafety.Assess(new(Volume, 4096, bytes), headroom ?? ControlledHeadroom);
    static void Require(bool condition, string error) { if (!condition) throw new Exception(error); }
    internal static int Run(string root)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        var results = new List<object>();
        void Check(string name, Action action)
        {
            try { action(); results.Add(new { name, result = "passed" }); }
            catch (Exception error) { results.Add(new { name, result = "failed", error = error.ToString() }); }
        }
        Check("five decimal GB warns strictly below boundary", () =>
        {
            Require(!Assess(5_000_000_000).ShouldWarn && Assess(5_000_000_000).SafeToResume, "Exactly five decimal GB must be safe without warning.");
            Require(Assess(4_999_999_999).ShouldWarn && Assess(4_999_999_999).Kind == ReplayStorageSafetyKind.Warning, "One byte below five decimal GB must warn.");
        });
        Check("qualified stop boundary and allocation unit neighbours", () =>
        {
            Require(Assess(2_000_008_192).ShouldStop, "One allocation unit below stop boundary must stop.");
            Require(Assess(2_000_012_288).ShouldStop && !Assess(2_000_012_288).SafeToResume, "Exact 2GB plus 4096 poll plus 8192 stop growth must stop.");
            Require(!Assess(2_000_016_384).ShouldStop && Assess(2_000_016_384).SafeToResume, "One allocation unit above stop boundary must allow explicit Resume.");
        });
        Check("missing qualification is explicit and cannot authorize Resume", () =>
        {
            var result = ReplayStorageSafety.Assess(new(Volume, 4096, 3_000_000_000), null);
            Require(result.Kind == ReplayStorageSafetyKind.Unmeasured && !result.HeadroomKnown && result.ShouldStop && !result.SafeToResume && result.ShouldWarn,
                "An unmeasured ordinary profile must reject acquisition without manufacturing a growth allowance.");
        });
        Check("unknown capacity identity and allocation mismatch fail closed", () =>
        {
            Require(ReplayStorageSafety.Assess(null, ControlledHeadroom).ShouldStop, "Unavailable capacity must stop.");
            Require(ReplayStorageSafety.Assess(new("unidentified", 4096, 9_000_000_000), ControlledHeadroom).ShouldStop, "Unknown volume must stop.");
            Require(ReplayStorageSafety.Assess(new(OtherVolume, 4096, 9_000_000_000), ControlledHeadroom).ShouldStop, "Different volume must stop.");
            Require(ReplayStorageSafety.Assess(new(Volume, 8192, 9_000_000_000), ControlledHeadroom).ShouldStop, "Different allocation unit must stop.");
            Require(ReplayStorageSafety.Assess(new(Volume, 3, 9_000_000_000), null).ShouldStop, "Invalid allocation unit must stop even without qualification.");
            Require(Assess(9_000_000_000, ControlledHeadroom with { Scope = "not-a-scope" }).ShouldStop, "Invalid scope must stop.");
            Require(Assess(9_000_000_000, ControlledHeadroom with { PollGrowthBytes = -1 }).ShouldStop, "Negative growth must stop.");
            Require(ReplayStorageSafety.Assess(new(Volume, 4096, -1), null).ShouldStop, "Negative capacity must stop.");
        });
        Check("checked growth overflow never produces a safe result", () =>
        {
            Require(Assess(long.MaxValue, ControlledHeadroom with { PollGrowthBytes = long.MaxValue }).ShouldStop, "Stop threshold overflow must stop.");
            Require(Assess(long.MaxValue, ControlledHeadroom with { PollGrowthBytes = long.MaxValue - 2_000_000_000, StopGrowthBytes = 1 }).ShouldStop, "Combined growth overflow must stop.");
        });
        Check("absent read creates no state and persisted hold survives aliases", () =>
        {
            string folder = Path.Combine(root, "persistence"); Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
            byte[] settings = [1, 3, 5], media = [2, 4, 6]; File.WriteAllBytes(Path.Combine(profile, "settings.json"), settings); File.WriteAllBytes(Path.Combine(profile, "saved.mp4"), media);
            var latch = new ReplayStorageStopLatch(profile, security);
            Require(!latch.Read().Held && !Directory.Exists(security), "Construct/read of absent state must create no private files.");
            var stopped = latch.Stop("controlled low storage");
            Require(stopped.Held && stopped.Durable && stopped.HoldId != null, "Stop must persist a named durable hold.");
            Require(new ReplayStorageStopLatch(profile, security).Read().HoldId == stopped.HoldId, "Hold must survive latch recreation.");
            string alias = Path.Combine(folder, "alias");
            var command = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            command.ArgumentList.Add("/d"); command.ArgumentList.Add("/c"); command.ArgumentList.Add("mklink"); command.ArgumentList.Add("/J"); command.ArgumentList.Add(alias); command.ArgumentList.Add(profile);
            using var junction = Process.Start(command); junction.WaitForExit();
            Require(junction.ExitCode == 0, "Controlled profile junction fixture creation failed: " + junction.StandardError.ReadToEnd());
            Require(new ReplayStorageStopLatch(alias, security).Read().HoldId == stopped.HoldId, "Filesystem aliases must share the hold.");
            Require(new ReplayStorageStopLatch(Path.Combine(profile, "."), security).Read().Held, "Normalized path aliases must share the hold.");
            string other = Path.Combine(folder, "other-profile"); Directory.CreateDirectory(other);
            Require(!new ReplayStorageStopLatch(other, security).Read().Held, "Independent profiles must remain independent.");
            Require(latch.ClearAfterSafeResume(Assess(2_000_012_288)).Held && new ReplayStorageStopLatch(profile, security).Read().Held, "Unsafe Resume must retain durable hold.");
            Require(latch.ClearAfterSafeResume(ReplayStorageSafety.Assess(new(Volume, 4096, 9_000_000_000), null)).Held, "Unmeasured Resume must retain hold.");
            Require(!new ReplayStorageStopLatch(alias, security).ClearAfterSafeResume(Assess(2_000_016_384)).Held, "Explicit qualified safe Resume should clear the hold through an alias.");
            Require(!new ReplayStorageStopLatch(profile, security).Read().Held, "Cleared hold must remain clear after reopening.");
            Require(File.ReadAllBytes(Path.Combine(profile, "settings.json")).SequenceEqual(settings) && File.ReadAllBytes(Path.Combine(profile, "saved.mp4")).SequenceEqual(media), "Hold operations must preserve settings and existing media bytes.");
        });
        Check("corrupt unknown oversized and pending holds reject safe Resume", () =>
        {
            foreach (string variant in new[] { "corrupt", "unknown", "oversized", "pending" })
            {
                string folder = Path.Combine(root, variant); Directory.CreateDirectory(folder);
                string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
                var latch = new ReplayStorageStopLatch(profile, security); Require(latch.Stop("controlled fixture").Durable, "Fixture hold must persist.");
                string file = Directory.GetFiles(Path.Combine(security, "profiles"), "*.json").Single();
                if (variant == "corrupt") File.WriteAllText(file, "{");
                if (variant == "unknown") File.WriteAllText(file, "{\"Version\":100}");
                if (variant == "oversized") File.WriteAllText(file, new string('x', 4097));
                if (variant == "pending") File.WriteAllText(Path.ChangeExtension(file, ".pending"), "partial");
                var reopened = new ReplayStorageStopLatch(profile, security);
                Require(reopened.Read().Held && reopened.ClearAfterSafeResume(Assess(9_000_000_000)).Held && File.Exists(file), "Unknown or incomplete hold must survive explicit safe Resume for " + variant);
            }
        });
        Check("sharing failure on clear retains the durable hold", () =>
        {
            string folder = Path.Combine(root, "failed-clear"); Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security); Require(latch.Stop("controlled fixture").Durable, "Fixture hold must persist.");
            string file = Directory.GetFiles(Path.Combine(security, "profiles"), "*.json").Single();
            using (var denyDelete = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                Require(latch.ClearAfterSafeResume(Assess(9_000_000_000)).Held && File.Exists(file), "Failed deletion must retain the named hold.");
            Require(new ReplayStorageStopLatch(profile, security).Read().Held, "Failed clear must stay held after reopening.");
            Require(!latch.ClearAfterSafeResume(Assess(9_000_000_000)).Held, "Explicit safe Resume after releasing the failing filesystem condition should clear.");
        });
        Check("inaccessible profile or private state fails closed", () =>
        {
            string folder = Path.Combine(root, "read-failure"); Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security); Require(latch.Stop("controlled fixture").Durable, "Fixture hold must persist.");
            string lockFile = Directory.GetFiles(Path.Combine(security, "profiles"), "*.lock").Single();
            using (var inaccessible = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Require(new ReplayStorageStopLatch(profile, security).Read().Held, "Unreadable hold lock must fail closed.");
                Require(new ReplayStorageStopLatch(profile, security).ClearAfterSafeResume(Assess(9_000_000_000)).Held, "Unreadable hold lock must reject Resume.");
            }
            Require(new ReplayStorageStopLatch(Path.Combine(folder, "missing-profile"), Path.Combine(folder, "absent-security")).Read().Held, "Unavailable profile identity must fail closed.");
        });
        Check("owned guard normal completion and abandoned restart", () =>
        {
            string folder = Path.Combine(root, "guard-normal"); Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security);
            var armed = latch.BeginCaptureGuard();
            Require(armed.Guard != null && armed.Hold.Held && armed.Hold.Durable && armed.Guard.HoldId == armed.Hold.HoldId, "Capture must receive an ownership token only after its guard is durable.");
            Require(new ReplayStorageStopLatch(profile, security).Read().Held, "Abandoned capture guard must hold on reopening.");
            Require(armed.Guard.CompleteNormally(false).Held, "An unconfirmed producer must retain the guard.");
            Require(!armed.Guard.CompleteNormally(true).Held && !new ReplayStorageStopLatch(profile, security).Read().Held, "Owned normal completion with confirmed producer absence must clear the guard without numeric qualification.");
        });
        Check("preexisting holds and failed arming never provide capture ownership", () =>
        {
            string folder = Path.Combine(root, "guard-admission"); Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security);
            var previous = latch.Stop("controlled previous storage stop");
            var blocked = latch.BeginCaptureGuard();
            Require(blocked.Guard == null && blocked.Hold.Held && blocked.Hold.HoldId == previous.HoldId, "A preexisting stop must not be claimed as a capture guard.");
            Require(!latch.ClearAfterSafeResume(Assess(9_000_000_000)).Held, "Fixture explicit qualified Resume must clear the old stop.");
            var first = latch.BeginCaptureGuard(); Require(first.Guard != null, "First capture guard should arm.");
            var second = new ReplayStorageStopLatch(profile, security).BeginCaptureGuard();
            Require(second.Guard == null && second.Hold.HoldId == first.Guard.HoldId, "A preexisting capture guard must never be claimed by a new owner.");
            Require(!first.Guard.CompleteNormally(true).Held, "Owned first guard should complete normally.");
            string failedRoot = Path.Combine(folder, "not-a-directory"); File.WriteAllText(failedRoot, "controlled obstruction");
            var failed = new ReplayStorageStopLatch(profile, failedRoot).BeginCaptureGuard();
            Require(failed.Guard == null && failed.Hold.Held && !failed.Hold.Durable, "Failed guard persistence must supply no acquisition ownership token.");
        });
        Check("stopped guard never completes after space returns", () =>
        {
            string folder = Path.Combine(root, "guard-stop"); Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security); var armed = latch.BeginCaptureGuard(); Require(armed.Guard != null, "Fixture guard should arm.");
            var stopped = armed.Guard.Stop("controlled actual storage stop");
            Require(armed.Guard.Stopped && stopped.Held && stopped.Durable, "Stop must mark the token stopped and retain a durable record.");
            Require(Assess(9_000_000_000).SafeToResume && armed.Guard.CompleteNormally(true).Held, "Returning capacity cannot let stopped guard complete normally.");
            Require(new ReplayStorageStopLatch(profile, security).Read().Held, "Stopped guard must remain held after reopening.");
            Require(!latch.ClearAfterSafeResume(Assess(9_000_000_000)).Held, "Explicit qualified Resume may clear a valid stop.");
        });
        Check("wrong token pending and malformed guard cannot complete", () =>
        {
            foreach (string variant in new[] { "wrong-token", "pending", "malformed", "stop-mode" })
            {
                string folder = Path.Combine(root, "guard-" + variant); Directory.CreateDirectory(folder);
                string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
                var latch = new ReplayStorageStopLatch(profile, security); var armed = latch.BeginCaptureGuard(); Require(armed.Guard != null, "Fixture guard should arm.");
                string file = Directory.GetFiles(Path.Combine(security, "profiles"), "*.json").Single();
                if (variant == "wrong-token") File.WriteAllText(file, File.ReadAllText(file).Replace(armed.Guard.HoldId, Guid.NewGuid().ToString("N")));
                if (variant == "pending") File.WriteAllText(Path.ChangeExtension(file, ".pending"), "partial");
                if (variant == "malformed") File.WriteAllText(file, "{");
                if (variant == "stop-mode") Require(latch.Stop("controlled direct stop").Durable, "Direct stop should convert guard mode to stop.");
                Require(armed.Guard.CompleteNormally(true).Held && File.Exists(file) && new ReplayStorageStopLatch(profile, security).Read().Held, "Mismatched or invalid owned guard must remain held for " + variant);
            }
        });
        Check("failed stop conversion retains prearmed durable evidence", () =>
        {
            string folder = Path.Combine(root, "guard-failed-stop"); Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security); var armed = latch.BeginCaptureGuard(); Require(armed.Guard != null, "Fixture guard should arm.");
            string file = Directory.GetFiles(Path.Combine(security, "profiles"), "*.json").Single();
            using (var denyReplacement = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = armed.Guard.Stop("controlled stop update failure");
                Require(armed.Guard.Stopped && failed.Held && !failed.Durable, "Guard must mark itself stopped even when specific stop persistence fails.");
            }
            Require(File.Exists(file) && new ReplayStorageStopLatch(profile, security).Read().Held, "Prearmed durable guard must survive failed stop conversion and reopening.");
            Require(armed.Guard.CompleteNormally(true).Held && File.Exists(file), "A failed stop conversion can never permit normal completion.");
        });
        Check("failed normal completion retains guard for reopened worker", () =>
        {
            string folder = Path.Combine(root, "guard-failed-completion"); Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security); var armed = latch.BeginCaptureGuard(); Require(armed.Guard != null, "Fixture guard should arm.");
            string file = Directory.GetFiles(Path.Combine(security, "profiles"), "*.json").Single();
            using (var denyDeletion = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                Require(armed.Guard.CompleteNormally(true).Held && File.Exists(file), "Failed completion deletion must retain guard.");
            Require(new ReplayStorageStopLatch(profile, security).Read().Held, "Reopened worker must observe guard after failed completion.");
        });
        Check("legacy version one stop remains explicit Resume compatible", () =>
        {
            string folder = Path.Combine(root, "legacy-stop"); Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security); Require(latch.Stop("controlled legacy stop").Durable, "Fixture stop should persist.");
            string file = Directory.GetFiles(Path.Combine(security, "profiles"), "*.json").Single();
            using var original = JsonDocument.Parse(File.ReadAllText(file));
            var legacy = new { Version = 1, ProfileIdentity = original.RootElement.GetProperty("ProfileIdentity").GetString(), HoldId = original.RootElement.GetProperty("HoldId").GetString(), Reason = "controlled legacy stop", StoppedAtUtc = original.RootElement.GetProperty("StoppedAtUtc").GetString() };
            File.WriteAllText(file, JsonSerializer.Serialize(legacy));
            Require(new ReplayStorageStopLatch(profile, security).Read().Held && latch.BeginCaptureGuard().Guard == null, "Legacy stop must remain held and unclaimable.");
            Require(!latch.ClearAfterSafeResume(Assess(9_000_000_000)).Held, "Legacy valid stop may clear only with explicit qualified Resume.");
        });
        Check("unmeasured necessary two decimal GB floor", () =>
        {
            foreach (long bytes in new[] { 1_999_999_999L, 2_000_000_000L })
            {
                var result = ReplayStorageSafety.Assess(new(Volume, 4096, bytes), null);
                Require(result.ShouldStop && result.ShouldWarn && !result.HeadroomKnown && result.SourceKind == ReplayStorageAssessmentSource.Unmeasured && !result.SafeToResume,
                    "At or below the necessary 2GB floor must stop without claiming qualified headroom.");
                Require(result.Reason.Contains("headroom", StringComparison.OrdinalIgnoreCase) && result.Reason.Contains("two decimal GB", StringComparison.OrdinalIgnoreCase),
                    "Unmeasured floor stop must explain missing headroom and the necessary floor.");
            }
            var above = ReplayStorageSafety.Assess(new(Volume, 4096, 2_000_000_001), null);
            Require(above.Kind == ReplayStorageSafetyKind.Unmeasured && above.ShouldStop && above.ShouldWarn && !above.SafeToResume && !above.HeadroomKnown,
                "One byte above the necessary floor still rejects acquisition without qualified advance headroom.");
        });
        Check("explicit Resume atomically replaces stop with a new guard", () =>
        {
            string folder = Path.Combine(root, "resume-replacement"); Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security); var old = latch.Stop("controlled old stop");
            var resumed = latch.BeginCaptureGuardAfterSafeResume(Assess(9_000_000_000));
            Require(resumed.Guard != null && resumed.Hold.Durable && resumed.Hold.Held && resumed.Guard.HoldId != old.HoldId,
                "Safe explicit Resume must replace old stop with a distinct durable guard token.");
            Require(new ReplayStorageStopLatch(profile, security).Read().HoldId == resumed.Guard.HoldId,
                "Reopening immediately after Resume admission must see the new durable guard, never an absent hold.");
            Require(!resumed.Guard.CompleteNormally(true).Held, "New owned Resume guard may clear after normal producer absence.");
        });
        Check("Resume replacement rejects unsafe unmeasured and unknown state", () =>
        {
            foreach (string variant in new[] { "unsafe", "unmeasured", "pending", "malformed", "absent" })
            {
                string folder = Path.Combine(root, "resume-" + variant); Directory.CreateDirectory(folder);
                string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
                var latch = new ReplayStorageStopLatch(profile, security);
                ReplayStorageHold old = variant == "absent" ? null : latch.Stop("controlled old stop");
                string file = old == null ? null : Directory.GetFiles(Path.Combine(security, "profiles"), "*.json").Single();
                if (variant == "pending") File.WriteAllText(Path.ChangeExtension(file, ".pending"), "partial");
                if (variant == "malformed") File.WriteAllText(file, "{");
                var assessment = variant == "unsafe" ? Assess(2_000_012_288) : variant == "unmeasured" ? ReplayStorageSafety.Assess(new(Volume, 4096, 9_000_000_000), null) : Assess(9_000_000_000);
                var rejected = latch.BeginCaptureGuardAfterSafeResume(assessment);
                Require(rejected.Guard == null && rejected.Hold.Held, "Rejected Resume replacement must supply no capture token for " + variant);
                if (file != null) Require(File.Exists(file) && new ReplayStorageStopLatch(profile, security).Read().Held, "Rejected replacement must retain old hold for " + variant);
                if (variant is "unsafe" or "unmeasured") Require(new ReplayStorageStopLatch(profile, security).Read().HoldId == old.HoldId, "Rejected qualification must preserve exact old token.");
                if (variant == "absent") Require(!Directory.Exists(security), "Absent-state replacement must not create files.");
            }
        });
        Check("failed Resume publication preserves previous durable stop", () =>
        {
            string folder = Path.Combine(root, "resume-failed-publication"); Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security); var old = latch.Stop("controlled old stop");
            string file = Directory.GetFiles(Path.Combine(security, "profiles"), "*.json").Single(); byte[] previous = File.ReadAllBytes(file);
            using (var denyReplacement = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = latch.BeginCaptureGuardAfterSafeResume(Assess(9_000_000_000));
                Require(failed.Guard == null && failed.Hold.Held, "Failed atomic replacement must provide no acquisition guard.");
            }
            Require(File.ReadAllBytes(file).SequenceEqual(previous), "Failed replacement must preserve old durable stop bytes.");
            var reopened = new ReplayStorageStopLatch(profile, security).Read();
            Require(reopened.Held, "Reopening after replacement failure must remain held with durable evidence.");
            using var retained = JsonDocument.Parse(File.ReadAllBytes(file));
            Require(retained.RootElement.GetProperty("HoldId").GetString() == old.HoldId, "Previous durable stop identity must survive failed publication.");
        });
        Check("explicit Resume replaces validated abandoned guard with distinct ownership", () =>
        {
            string folder = Path.Combine(root, "resume-abandoned-guard"); Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security"); Directory.CreateDirectory(profile);
            var first = new ReplayStorageStopLatch(profile, security).BeginCaptureGuard(); Require(first.Guard != null, "Fixture old guard should arm.");
            // This file-only fixture declares the caller's producer-absence proof;
            // runtime integration must establish that proof from actual producers.
            var reopened = new ReplayStorageStopLatch(profile, security);
            var replacement = reopened.BeginCaptureGuardAfterSafeResume(Assess(9_000_000_000));
            Require(replacement.Guard != null && replacement.Hold.Durable && replacement.Guard.HoldId != first.Guard.HoldId, "Valid abandoned guard may be replaced only with distinct newly persisted ownership.");
            Require(first.Guard.CompleteNormally(true).Held && reopened.Read().HoldId == replacement.Guard.HoldId, "Old token must never clear newly published Resume guard.");
            Require(!replacement.Guard.CompleteNormally(true).Held, "New owned guard should complete normally with confirmed absence.");
        });
        int failures = results.Count(result => JsonSerializer.SerializeToElement(result).GetProperty("result").GetString() == "failed");
        File.WriteAllText(Path.Combine(root, "storage-safety-checks.json"), JsonSerializer.Serialize(new { result = failures == 0 ? "passed" : "failed", fixture = "controlled byte bounds, not calibration", checks = results }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var result in results) Console.WriteLine(JsonSerializer.Serialize(result));
        return failures == 0 ? 0 : 1;
    }
}





