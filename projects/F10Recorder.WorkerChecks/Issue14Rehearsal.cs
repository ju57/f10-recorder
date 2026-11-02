using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    sealed class Issue14Dummy(string session, Action<IReadOnlyList<string>, Action> export) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo => new(session, Path.Combine(session, "segments.csv"), 300, 20, 60, "h264_nvenc", ".ts", false, true);
        public bool Stopped => true;
        public bool PreserveEvidence => true;
        public string Diagnostics => "Nonmedia namespace/allocation rehearsal only.";
        public bool OutputProducerStopped => true;
        public string OutputDemandIdentity => "issue14-dummy-fixed-bytes";
        public double SaveCutoffAt(long _) => 303.5;
        public void FreezeAt(long _) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double _) { }
        public void Dispose() { }
        public void Export(IReadOnlyList<string> args, string _) => throw new IOException("Guarded writer required.");
        public void Export(IReadOnlyList<string> args, string _, Action<ReplayProducerProof> changed, Action check) => export(args, check);
        public ReplayValidation ValidateOutput(string _, ReplayInterval interval, Action<ReplayProducerProof> changed, Action check)
            => new("ControlledNonmedia", 0, 0);
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            var capacity = ReplayStorage.ObserveCapacity(selection.Destination);
            return new(ReplayOutputEvidenceKind.SourceBound, OutputDemandIdentity, selection.Identity, selection.ExporterIdentity,
                capacity.Volume, capacity.AllocationUnit, ReplayReferenceModel.Round(selection.Inputs.Sum(i => i.Extent.Length), capacity.AllocationUnit), false, null);
        }
    }

    internal static int RehearseIssue14(string planFile, string expectedHash)
    {
        Require(HashFile(planFile) == expectedHash, "Rehearsal plan changed.");
        using var document = JsonDocument.Parse(File.ReadAllText(planFile));
        var p = document.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>();
        ValidateExact14(p);
        VerifyIssue14Family(p);
        foreach (var file in p.Files) Require(HashFile(file.Key) == file.Value, "Rehearsal input changed: " + file.Key);
        Require(!Directory.Exists(p.Root), "Fresh rehearsal root required.");
        using var owned = PilotStorageObservation.Create(p.Root);
        var timer = Stopwatch.StartNew();
        using var sampler = new Sampler(p, active: false);
        bool shortage = false;
        try { sampler.BeginOffline("deliberate-shortage", p.BudgetBytes, 5); }
        catch (IOException ex) when (ex.Message.Contains("cannot fit", StringComparison.Ordinal)) { shortage = true; }
        Require(shortage, "Capacity shortage was admitted.");
        string masterSession = p.RehearsalReuseRoot == null ? null : Path.Combine(p.RehearsalReuseRoot, "T01", "buffer", "session-dummy"), masterOutput = null;
        if (masterSession != null) Require(Enumerable.Range(0, 310).All(n => new FileInfo(Path.Combine(masterSession, $"chunk-{n:D9}.ts")).Length == 3_000_000), "Retained dummy geometry changed.");
        var results = new List<object>();
        byte[] block = new byte[1_048_576]; RandomNumberGenerator.Fill(block);
        foreach (string label in Order)
        {
            sampler.RequireTime(120); sampler.Check(2_000_000_000); sampler.BeginActive();
            string cell = Path.Combine(p.Root, label), session = Path.Combine(cell, "buffer", "session-dummy");
            Directory.CreateDirectory(session);
            for (int n = 0; n < 310; n++)
            {
                string name = $"chunk-{n:D9}.ts", target = Path.Combine(session, name);
                if (masterSession == null) Payload(target, 3_000_000);
                else Require(Native.CreateHardLink(target, Path.Combine(masterSession, name), IntPtr.Zero), "Rehearsal source hardlink failed.");
            }
            File.WriteAllLines(Path.Combine(session, "segments.csv"), Enumerable.Range(0, 310).Select(n => $"chunk-{n:D9}.ts,{n},{n + 1}"));
            using var cache = new ReplayCache(new RetainFiles(sampler, true), cacheDirectory: Path.Combine(cell, "buffer"));
            var source = new Issue14Dummy(session, (args, check) =>
            {
                check();
                if (masterOutput == null) Payload(args[^1], 800_000_000, check);
                else Require(Native.CreateHardLink(args[^1], masterOutput, IntPtr.Zero), "Rehearsal output hardlink failed.");
                check();
            });
            var id = cache.RegisterSession(source);
            var submission = cache.TrySave(id, 0, cell);
            Require(submission.Accepted && submission.Completion.Wait(60000), submission.Rejection ?? "Dummy save deadline.");
            var result = submission.Completion.GetAwaiter().GetResult();
            Require(result.Succeeded && result.Interval.Seconds == 300 && result.Validation.Kind == "ControlledNonmedia", result.Error);
            Require(Directory.GetDirectories(Path.Combine(cell, "buffer"), "save-*", SearchOption.AllDirectories)
                .Sum(d => File.ReadLines(Path.Combine(d, "concat.txt")).Count(line => line.StartsWith("file '"))) == 301,
                "Head-inclusive301-entry snapshot was not preserved.");
            cache.CloseAsync().GetAwaiter().GetResult();
            masterSession ??= session; masterOutput ??= result.File;
            sampler.EndActive();
            results.Add(new { label, sources = 310, selected = 301, outputBytes = result.Bytes, result.Interval });
            sampler.SealClosedCase(label);
        }
        sampler.StopAndCheck();
        Require(sampler.MaximumActiveGap <= 500 && timer.Elapsed.TotalSeconds < 900, "Rehearsal timing boundary failed.");
        Write(Path.Combine(p.Root, "verification.json"), new { result = "passed", results, shortageRejected = shortage,
            sampler.MaximumActiveGap, sampler.PeakUsed, sampler.MaximumFiles, sampler.MaximumDirectories, elapsedSeconds = timer.Elapsed.TotalSeconds,
            effects = "No media, devices, children, window or model activation. Six complete310/301 namespaces share one930MB source set and one800MB output by hardlink; prior dummy inputs may be linked read-only. The new800MB output grows under the real storage callback; unique future live payload is reserved separately." });
        Console.WriteLine("Issue14 nonmedia rehearsal passed: " + p.Root); return 0;

        void Payload(string path, long bytes, Action check = null)
        {
            Require(timer.Elapsed.TotalSeconds < 900, "Rehearsal wall deadline."); sampler.Check(bytes);
            using var output = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = block.Length });
            long left = bytes;
            var cadence = Stopwatch.StartNew();
            while (left > 0) { int take = (int)Math.Min(left, block.Length); output.Write(block, 0, take); left -= take; sampler.Check();
                if (cadence.ElapsedMilliseconds >= 100) { output.Flush(); check?.Invoke(); cadence.Restart(); } }
            output.Flush();
        }
    }
}
