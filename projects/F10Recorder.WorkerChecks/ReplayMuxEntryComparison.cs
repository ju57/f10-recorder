using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Unqualified standalone construction experiment. Never loads a calibration
// catalog, starts capture, reserves a product allowance, publishes, or emits Saved.
internal static class ReplayMuxEntryComparison
{
    static readonly JsonSerializerOptions Json = new(WorkerProtocol.Json) { WriteIndented = false };
    static void Need(bool value, string message) { if (!value) throw new IOException(message); }
    static string Hash(string path, Action check = null)
    {
        using var stream = File.OpenRead(path); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[131072]; long at = Stopwatch.GetTimestamp(); int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            hash.AppendData(buffer, 0, count);
            if (Stopwatch.GetElapsedTime(at).TotalMilliseconds >= 100) { check?.Invoke(); at = Stopwatch.GetTimestamp(); }
        }
        check?.Invoke(); return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static int Run(string planFile, string expectedHash, string label)
    {
        Need(new FileInfo(planFile).Length <= 65536 && Hash(planFile) == expectedHash, "Exact standalone comparison admission required.");
        using var plan = JsonDocument.Parse(File.ReadAllBytes(planFile)); var p = plan.RootElement;
        Need(p.GetProperty("kind").GetString() == "issue83-mux-entry-comparison-v1"
            && label is "control" or "candidate" && DateTime.UtcNow.AddSeconds(100) < p.GetProperty("expiresUtc").GetDateTime(), "Fresh bounded comparison required.");
        foreach (var f in p.GetProperty("files").EnumerateObject()) Need(Hash(f.Name) == f.Value.GetString(), "Comparison binding changed: " + f.Name);
        string package = p.GetProperty(label + "Package").GetString();
        Need(Path.GetDirectoryName(typeof(ReplayMuxEntryComparison).Assembly.Location) == package
            && Hash(typeof(CaptureSession).Assembly.Location) == p.GetProperty(label + "WindowsSha256").GetString()
            && Hash(typeof(Settings).Assembly.Location) == p.GetProperty("coreSha256").GetString(), "Exact selected experimental runtime required.");
        string root = p.GetProperty("root").GetString(), cell = Path.Combine(root, label);
        Need(root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(cell) && !File.Exists(cell), "Fresh comparison cell required.");
        Directory.CreateDirectory(cell);
        string oldAuthority = ProfileOwnership.RootDirectory, oldLog = Log.DirectoryPath;
        ProfileOwnership.RootDirectory = Path.Combine(cell, "authority"); Log.DirectoryPath = cell;
        MediaDependency.RootDirectory = p.GetProperty("engineRoot").GetString();
        object closedRootsBefore = CheckClosedRoots();
        using var previous = JsonDocument.Parse(File.ReadAllBytes(p.GetProperty("previousStage").GetString()));
        var selection = previous.RootElement.GetProperty("selection");
        var inputs = selection.GetProperty("Inputs").Deserialize<ReplayOutputInput[]>(Json);
        var priorArgs = previous.RootElement.GetProperty("exportArguments").EnumerateArray().Select(x => x.GetString()).ToArray();
        Need(inputs is { Length: > 0 and <= 305 } && inputs.Sum(i => i.Extent.Length) <= 1_000_000_000
            && priorArgs.Length is > 1 and < 256, "Bounded recorded input set/boundary command required.");
        var interval = new ReplayInterval(selection.GetProperty("Start").GetDouble(), selection.GetProperty("End").GetDouble(), 300);
        Need(Math.Abs(interval.Seconds - 300) < .000001, "Recorded five-minute interval required.");
        string sourceRoot = Path.GetDirectoryName(Path.GetDirectoryName(inputs[0].Path));
        var held = new List<FileStream>();
        var clock = Stopwatch.StartNew(); using var host = Process.GetCurrentProcess();
        var native = new ReplayFiles(); var timing = new ReplaySaveTiming { Frequency = Stopwatch.Frequency };
        ReplayExportRequest request = null; ReplayExportProcessReceipt decode = null;
        ReplayDecodeLease outputLease = null;
        object integrity = null, driftMutation = null; var controls = new List<object>(); Exception failure = null;
        double? exportMilliseconds = null, integrityMilliseconds = null;
        string outputHash = null;
        long peak = 0, callbacks = 0; double namespaceMilliseconds = 0, fullMilliseconds = 0;
        long lastCompleted = Stopwatch.GetTimestamp(); double maximumGap = 0;
        ReplayStorageObservationGate gate = null;
        void Guard()
        {
            Need(clock.Elapsed.TotalSeconds < 75, "75second per-arm experimental deadline.");
            gate.RequireFreshness(); long started = Stopwatch.GetTimestamp();
            var names = ReplayStorage.InventoryNamespaces([sourceRoot, cell], native);
            foreach (var input in inputs)
                Need(names.TryGetValue(input.Path, out var found) && found.Volume == input.Extent.Volume && found.Identity == input.Extent.Identity,
                    "Protected source namespace changed.");
            namespaceMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; callbacks++;
            gate.Check();
        }
        gate = new(() => {
            long started = Stopwatch.GetTimestamp();
            try
            {
                long now = Stopwatch.GetTimestamp(); maximumGap = Math.Max(maximumGap, Stopwatch.GetElapsedTime(lastCompleted, now).TotalMilliseconds);
                long bytes = ReplayStorage.Bytes(ReplayStorage.Inventory([root], native, 4096, 64)); peak = Math.Max(peak, bytes);
                host.Refresh(); Need(host.WorkingSet64 < 1_073_741_824 && bytes < 5_000_000_000
                    && ReplayStorage.ObserveCapacity(root).AvailableBytes >= 60_000_000_000, "Experimental RAM/storage/free-space bound.");
                lastCompleted = Stopwatch.GetTimestamp(); return true;
            }
            finally { fullMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
        }, 100, 500);
        try
        {
            foreach (var input in inputs)
            {
                Need(input.Path.StartsWith(p.GetProperty("previousRoot").GetString() + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Task-owned input required.");
                var handle = new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read); held.Add(handle);
                var current = ReplayStorage.ObserveInventory(handle.SafeFileHandle, input.Extent);
                Need(current.Length == input.Extent.Length, "Recorded source EOF changed.");
                Guard();
            }
            string boundary = priorArgs[^1];
            Need(boundary.StartsWith(p.GetProperty("previousRoot").GetString() + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && new FileInfo(boundary).Length is > 0 and < 2_000_000, "Bounded retained boundary required.");
            var boundaryLease = new FileStream(boundary, FileMode.Open, FileAccess.Read, FileShare.Read); held.Add(boundaryLease);
            string boundaryHash = Hash(boundary); Guard();
            var info = new ReplaySessionInfo(Path.GetDirectoryName(inputs[0].Path), "", 300, 20, 60, "h264_nvenc", ".ts", false, true);
            request = new(info, interval, inputs, Path.Combine(cell, "forbidden-fallback.txt"), Path.Combine(cell, "output.partial.mp4"), timing);
            int boundaryCopies = 0;
            long exportAt = Stopwatch.GetTimestamp();
            ReplaySaveExport.Run(request, (arguments, check) => {
                Need(++boundaryCopies == 1 && arguments.Count == priorArgs.Length && arguments.Take(arguments.Count - 1).SequenceEqual(priorArgs[..^1]),
                    "Boundary command changed; experiment cannot substitute its retained producer.");
                check(); File.Copy(boundary, arguments[^1]); check();
            }, Guard, ReplaySaveExport.DefaultConstruction);
            exportMilliseconds = Stopwatch.GetElapsedTime(exportAt).TotalMilliseconds;
            Need(boundaryCopies == 1 && request.ManagedMux?.Closed == true && request.Closure.SourcesClosed && request.IndexBatch?.Joined == true,
                "Closed managed producer/index/source ownership required.");
            long integrityAt = Stopwatch.GetTimestamp();
            integrity = ReplayOutputIntegrity.Read(request, Guard, retainForPublication: true);
            integrityMilliseconds = Stopwatch.GetElapsedTime(integrityAt).TotalMilliseconds;
            outputLease = new ReplayDecodeLease(request.Partial, request.ValidatedOutput);
            using (var job = new Native.ProcessJob())
            {
                decode = ReplayExportProcess.Run(MediaDependency.ResolveExecutable(), ReplayExactExport.DecodeArguments(request.Partial, true, 60), cell,
                    job, timeoutMilliseconds: 20000, checkStorage: Guard, inputLease: outputLease);
                if (!decode.ProducerAbsent) Hold("Fresh decoder closure unconfirmed.");
                ReplayExactExport.ReadValidation(decode, interval, 60); outputLease.VerifyUnchanged();
            }
            // Keep this exact checked-object and no-delete pin through hashing
            // and control derivation, not just the decoder child lifetime.
            outputHash = Hash(request.Partial, Guard); Guard();
            // A deep coded-body corruption and a small cumulative drift keep
            // track counts/endpoints plausible; both must fail the real gate.
            ReplayMp4Sample damaged;
            using (var output = File.OpenRead(request.Partial))
                damaged = ReplayMp4Reader.Read(output, output.Length, interval, 60, true, Guard).Tracks.Single(t => t.Kind == "vide").Samples
                    .Skip(10).First(s => s.Bytes > 1024);
            string corrupt = Path.Combine(cell, "corrupt.mp4"), drift = Path.Combine(cell, "drift.mp4");
            CopyGuarded(request.Partial, corrupt); Guard();
            using (var edit = new FileStream(corrupt, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                byte[] sample = new byte[damaged.Bytes]; edit.Position = damaged.Offset; edit.ReadExactly(sample);
                bool changed = false;
                for (int at = 0; at < sample.Length;)
                {
                    int count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(sample.AsSpan(at, 4))); int body = at + 4;
                    Need(count > 0 && count <= sample.Length - body, "Control NAL extent.");
                    if ((sample[body] & 31) is 1 or 5 && count > 512)
                    { edit.Position = damaged.Offset + body + 256; edit.Write(new byte[count - 256]); changed = true; break; }
                    at = body + count;
                }
                Need(changed, "No substantial VCL body for corrupt control.");
            }
            Reject(corrupt, "source bytes changed");
            CopyGuarded(request.Partial, drift);
            driftMutation = ReplayMp4DriftControl.Mutate(drift, Guard);
            Reject(drift, "cumulative picture timing/cadence drift");
            Need(boundaryHash == Hash(boundary), "Retained boundary changed."); outputLease.VerifyUnchanged(); Guard();
            // No growing producer remains. Rebind the closed-root allowances
            // outside the measured export, before the terminal receipt.
            object closedRootsAfter = CheckClosedRoots();
            Need(clock.Elapsed.TotalSeconds < 75, "Final per-arm deadline.");
            Write(new { result = "standalone-arm-completed", label, exportMilliseconds, integrityMilliseconds, outputHash,
                boundaryHash, boundaryCopies, timing, managedMux = request.ManagedMux, integrity, decode, driftMutation, controls,
                closedRootsBefore, closedRootsAfter, callbacks, namespaceMilliseconds, fullMilliseconds, maximumGap, peak, seconds = clock.Elapsed.TotalSeconds,
                scope = "Unqualified standalone export/native-guard comparison; fixed experiment quota, no catalog/ledger/capture/publication/Saved. Boundary encode factored out with identical retained bytes/command; full decode and corruption/drift controls included after measured export." });

            void Reject(string path, string reason)
            {
                var negative = new ReplayExportRequest(info, interval, inputs, "", path, new() { Frequency = Stopwatch.Frequency });
                negative.SelectRecipe(request.Recipe); negative.FreezeIntegrity(request.Integrity);
                string error = null;
                try { ReplayOutputIntegrity.Read(negative, Guard); }
                catch (IOException exception) { error = exception.ToString(); }
                Need(error?.Contains(reason, StringComparison.OrdinalIgnoreCase) == true, "Control rejected for wrong/no cause: " + reason);
                controls.Add(new { path, reason, error, sha256 = Hash(path, Guard), publicationAttempted = false }); Guard();
            }
        }
        catch (Exception error)
        {
            failure = error; Console.Error.WriteLine(error);
            if (Uncertain()) Hold("Managed producer/source/index closure unconfirmed after failure.");
            Write(new { result = "failed", label, error = error.ToString(), timing, managedMux = request?.ManagedMux,
                exportMilliseconds, integrityMilliseconds, outputHash, integrity, decode, driftMutation, controls, closedRootsBefore,
                callbacks, namespaceMilliseconds, fullMilliseconds, peak, seconds = clock.Elapsed.TotalSeconds });
        }
        finally
        {
            if (Uncertain()) Hold("Managed producer/source/index closure unconfirmed during cleanup.");
            outputLease?.Dispose(); request?.ValidatedOutput?.Dispose(); foreach (var handle in held) handle.Dispose();
            ProfileOwnership.RootDirectory = oldAuthority; Log.DirectoryPath = oldLog;
        }
        return failure == null ? 0 : 1;

        void Write(object report)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json); Need(bytes.Length <= 1_048_576, "1MiB arm report cap.");
            File.WriteAllBytes(Path.Combine(cell, "result.json"), bytes);
        }
        void CopyGuarded(string from, string to)
        {
            using var source = File.OpenRead(from);
            using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            byte[] buffer = new byte[131072]; long at = Stopwatch.GetTimestamp(); int count;
            while ((count = source.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, count);
                if (Stopwatch.GetElapsedTime(at).TotalMilliseconds >= 100) { Guard(); at = Stopwatch.GetTimestamp(); }
            }
        }
        void Hold(string reason)
        {
            Console.Error.WriteLine(reason + " All owners retained for bounded outer termination.");
            try { Write(new { result = "incomplete-accepted-work", label, reason, timing, managedMux = request?.ManagedMux,
                exportMilliseconds, integrityMilliseconds, outputHash, driftMutation, decode, closedRootsBefore }); }
            catch (Exception error) { Console.Error.WriteLine("Incomplete report: " + error.Message); }
            finally { while (true) Thread.Sleep(100); }
        }
        bool Uncertain() => request != null && (request.Closure.PacketStarted && request.ManagedMux?.Closed != true
            || request.Closure.AssemblyStarted && !request.Closure.AssemblyClosed || !request.Closure.SourcesClosed
            || request.IndexBatch is { Workers: > 0, Joined: false });
        object CheckClosedRoots()
        {
            long previousBytes = ReplayStorage.Bytes(ReplayStorage.Inventory([p.GetProperty("previousRoot").GetString()], new ReplayFiles(), 4096, 64));
            long traceBytes = ReplayStorage.Bytes(ReplayStorage.Inventory([p.GetProperty("traceRoot").GetString()], new ReplayFiles(), 4096, 64));
            long failedPairBytes = ReplayStorage.Bytes(ReplayStorage.Inventory([p.GetProperty("failedComparisonRoot").GetString()], new ReplayFiles(), 4096, 64));
            Need(previousBytes < 1_750_000_000 && traceBytes < 1_750_000_000 && failedPairBytes < 2_500_000_000,
                "Previously closed roots exceed the separate1.75+1.75+2.5GB allowances.");
            return new { previousBytes, traceBytes, failedPairBytes };
        }
    }
}
