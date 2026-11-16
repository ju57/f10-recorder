using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using F10Recorder;

// Read-only, prospectively bounded attribution. No media process, media output,
// capture, encoder or publication is involved; admission hashes warm inputs.
internal static class ReplayPacketAttribution
{
    static void Require(bool value, string why) { if (!value) throw new IOException(why); }
    internal static void Run(string path, string expected)
    {
        byte[] raw;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        { Require(input.Length <= 262144, "Attribution plan-byte bound."); raw = new byte[(int)input.Length]; input.ReadExactly(raw); }
        Require(Convert.ToHexString(SHA256.HashData(raw)) == expected, "Frozen attribution plan changed.");
        using var json = JsonDocument.Parse(raw); var plan = json.RootElement;
        string root = Path.GetFullPath(plan.GetProperty("root").GetString());
        Require(plan.GetProperty("kind").GetString() == "issue83-packet-file-attribution-v1" && !Directory.Exists(root)
            && root.StartsWith(Path.GetFullPath("artifacts/issue83") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow >= plan.GetProperty("created_utc").GetDateTimeOffset()
            && DateTimeOffset.UtcNow.AddSeconds(60) < plan.GetProperty("expires_utc").GetDateTimeOffset(), "Fresh file-only attribution required.");
        var bindings = plan.GetProperty("bindings").EnumerateObject().ToArray();
        Require(bindings.Length <= 768 && plan.GetProperty("trials").GetArrayLength() == 5, "Attribution role/trial bound.");
        foreach (string required in new[] { Environment.ProcessPath, typeof(ReplayPacketAttribution).Assembly.Location, typeof(ReplayMp4Reader).Assembly.Location })
            Require(bindings.Any(b => Path.GetFullPath(b.Name).Equals(Path.GetFullPath(required), StringComparison.OrdinalIgnoreCase)), "Actual runtime/helper unbound.");
        var clock = Stopwatch.StartNew(); double lastSample = -1, lastObservation = -1, maxGap = 0; long peakRam = 0; int peakHandles = 0;
        var streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase); var rows = new List<object>();
        var uncertainSourceOwners = new List<FileStream>();
        int inProgressReplacements = 0; double finalDisposalMilliseconds = 0; bool finalOwnersClosed = false;
        int inProgressExtraHandles = 0;
        string phase = "admission", bindingPath = null; long observedRam = 0; int observedHandles = 0;
        string packetReference = null;
        void Checkpoint()
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { phase, bindingPath, bindingsOpened = streams.Count,
                rowsCompleted = rows.Count, inProgressExtraHandles, inProgressReplacements, seconds = clock.Elapsed.TotalSeconds,
                observedRam, observedHandles, peakRam, peakHandles, maxGap });
            Require(bytes.Length <= 65536, "Attribution checkpoint size bound.");
            File.WriteAllBytes(Path.Combine(root, "checkpoint.json"), bytes);
        }
        void Guard()
        {
            double now = clock.Elapsed.TotalSeconds;
            if (lastSample >= 0) maxGap = Math.Max(maxGap, (now - lastSample) * 1000);
            lastSample = now;
            if (now >= 45 || maxGap > 500) { Checkpoint(); throw new IOException("Attribution deadline/guard bound."); }
            if (now - lastObservation < .05) return;
            lastObservation = now;
            using var process = Process.GetCurrentProcess(); observedRam = process.WorkingSet64; observedHandles = process.HandleCount;
            peakRam = Math.Max(peakRam, observedRam); peakHandles = Math.Max(peakHandles, observedHandles); Checkpoint();
            Require(peakRam < 268435456 && peakHandles < 1024 && new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace >= 60000000000L,
                "Attribution RAM/handle/free-space bound.");
        }
        Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "admitted-plan.json"), raw);
        string failure = null; long totalBytes = 0;
        ReplayTransportBatchTiming inProgressBatch = null; string inProgressLabel = null;
        string extraCloseError = null;
        try
        {
            byte[] buffer = new byte[65536];
            foreach (var binding in bindings)
            {
                bindingPath = binding.Name;
                Guard(); var stream = new FileStream(binding.Name, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                streams.Add(Path.GetFullPath(binding.Name), stream); totalBytes = checked(totalBytes + stream.Length);
                Require(totalBytes <= 3200000000L, "Attribution admitted input-byte cap.");
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); int read;
                while ((read = stream.Read(buffer)) != 0) { digest.AppendData(buffer, 0, read); Guard(); }
                Require(Convert.ToHexString(digest.GetHashAndReset()) == binding.Value.GetString(), "Attribution input changed.");
            }
            var originals = plan.GetProperty("sources").EnumerateArray().ToArray();
            Require(originals.Length == 301, "Attribution original source cardinality.");
            foreach (var trial in plan.GetProperty("trials").EnumerateArray())
            {
                Guard(); string label = trial.GetProperty("label").GetString();
                phase = label; bindingPath = null; Checkpoint();
                long at = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
                using var process = Process.GetCurrentProcess(); double cpu = process.TotalProcessorTime.TotalMilliseconds;
                if (trial.GetProperty("kind").GetString() == "mp4")
                {
                    string input = Path.GetFullPath(trial.GetProperty("path").GetString()); var stream = streams[input];
                    var timing = new ReplayMp4ReadTiming();
                    bool videoRawDigest = !trial.TryGetProperty("include_video_payload_digest", out var rawDigest) || rawDigest.GetBoolean();
                    var index = ReplayMp4Reader.Read(stream, stream.Length, new(0, 300, 300), 60, true, Guard, timing,
                        includeVideoPayloadDigest: videoRawDigest);
                    process.Refresh();
                    double milliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    var tracks = index.Tracks.Select(t => new { t.Kind, t.MediaStart, t.DelaySeconds, t.PresentationTicks,
                        count = t.Samples.Count, codedBytes = t.Samples.Sum(s => (long)s.Bytes), packetProof = Digest(t.Samples.Select(s => s.PacketSha256)) }).ToArray();
                    string fingerprint = string.Join("/", tracks.Select(t => t.Kind + ":" + t.count + ":" + t.packetProof));
                    packetReference ??= fingerprint;
                    bool matchedCanonicalPackets = packetReference == fingerprint;
                    Require(index.Tracks.Single(t => t.Kind == "vide").Samples.All(s => videoRawDigest ? s.PayloadSha256?.Length == 64 : s.PayloadSha256 == null)
                        && index.Tracks.Single(t => t.Kind == "soun").Samples.All(s => s.PayloadSha256?.Length == 64), "Attribution digest mode lost required evidence.");
                    rows.Add(new { label, kind = "mp4", videoRawDigest, milliseconds,
                        cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds - cpu, allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
                        timing, tracks, matchedCanonicalPackets });
                    Require(matchedCanonicalPackets, "Matched layout files changed canonical coded packets/counts."); Guard();
                }
                else if (trial.GetProperty("kind").GetString() == "index")
                {
                    int workers = trial.GetProperty("workers").GetInt32();
                    Require(workers is 1 or 4, "Attribution index worker bound.");
                    bool cancellableReads = trial.TryGetProperty("cancellable_reads", out var cancellable) && cancellable.GetBoolean();
                    bool freshHandles = trial.TryGetProperty("fresh_handles", out var fresh) && fresh.GetBoolean();
                    bool replaceLeases = trial.TryGetProperty("replace_leases", out var replace) && replace.GetBoolean();
                    Require(!(replaceLeases && freshHandles) && (!cancellableReads || (freshHandles || replaceLeases) && workers == 4),
                        "Cancellable attribution needs an explicit fresh/replacement async handle mode and4workers.");
                    inProgressLabel = label; inProgressBatch = new ReplayTransportBatchTiming();
                    long processAllocation = GC.GetTotalAllocatedBytes(precise: true);
                    var opened = new List<FileStream>(); var ownedHandles = new List<Microsoft.Win32.SafeHandles.SafeFileHandle>();
                    ReplayTransportIndex[] indices; Exception primary = null; bool extraHandlesClosed = false;
                    double openMilliseconds = 0, closeMilliseconds = 0;
                    try
                    {
                        phase = "opening:" + label;
                        long openAt = Stopwatch.GetTimestamp(); var inputs = new List<ReplayTransportSource>();
                        foreach (var item in originals)
                        {
                            Guard(); string sourcePath = Path.GetFullPath(item.GetProperty("path").GetString());
                            FileStream stream;
                            if (replaceLeases)
                            {
                                stream = ReplayIndexLeaseReplacement.Replace(streams, sourcePath, cancellableReads, uncertainSourceOwners);
                                inProgressReplacements++;
                            }
                            else if (freshHandles)
                            {
                                stream = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                                    FileOptions.SequentialScan | (cancellableReads ? FileOptions.Asynchronous : FileOptions.None));
                                opened.Add(stream); ownedHandles.Add(stream.SafeFileHandle); inProgressExtraHandles = opened.Count;
                                Require(stream.IsAsync == cancellableReads, "Actual source handle mode differs from the plan.");
                            }
                            else { stream = streams[sourcePath]; stream.Position = 0; }
                            inputs.Add(new(stream, stream.Length));
                        }
                        openMilliseconds = Stopwatch.GetElapsedTime(openAt).TotalMilliseconds;
                        phase = "index:" + label; Checkpoint();
                        indices = workers == 1
                            ? inputs.Select(item => ReplayTransportReader.Read(item.Stream, item.Bytes, 60, true, Guard)).ToArray()
                            : ReplayTransportBatchIndex.Read(inputs, 60, true, workers, Guard, inProgressBatch, cancellableReads: cancellableReads);
                    }
                    catch (Exception error) { primary = error; throw; }
                    finally
                    {
                        phase = "closing:" + label;
                        long closeAt = Stopwatch.GetTimestamp(); Exception closing = null;
                        foreach (var stream in opened)
                        {
                            try { stream.Dispose(); } catch (Exception error) { closing ??= error; }
                            try { Guard(); } catch (Exception error) { closing ??= error; }
                        }
                        extraHandlesClosed = ownedHandles.All(h => h.IsClosed); closeMilliseconds = Stopwatch.GetElapsedTime(closeAt).TotalMilliseconds;
                        if (closing != null || !extraHandlesClosed)
                        {
                            extraCloseError = closing?.ToString() ?? "Source handle closure unconfirmed.";
                            if (primary == null) throw new IOException("Attribution source closure/guard failed.", closing);
                        }
                    }
                    double indexMilliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    long processAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - processAllocation;
                    process.Refresh(); double indexCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds - cpu;
                    long proofAt = Stopwatch.GetTimestamp();
                    phase = "proof:" + label; Checkpoint();
                    var proofs = new List<string>();
                    foreach (var index in indices)
                    {
                        Guard(); proofs.Add(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(index))));
                    }
                    string fingerprint = Digest(proofs); packetReference ??= fingerprint;
                    rows.Add(new { label, kind = "index", workers, cancellableReads, freshHandles, replaceLeases,
                        replacementMilliseconds = replaceLeases ? (double?)openMilliseconds : null,
                        openMilliseconds = replaceLeases ? (double?)null : openMilliseconds, closeMilliseconds,
                        replacedReadHandles = inProgressReplacements,
                        additionalReadHandles = opened.Count, extraHandlesClosed, indexMilliseconds, indexCpuMilliseconds, processAllocatedBytes,
                        ownerAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated, batch = workers == 4 ? inProgressBatch : null,
                        sources = indices.Length, pictures = indices.Sum(i => i.Pictures.Count), audioPackets = indices.Sum(i => i.AudioFrames.Count),
                        sourceBytes = indices.Sum(i => i.Bytes), fingerprint, matchedCompleteEvidence = fingerprint == packetReference,
                        proofMilliseconds = Stopwatch.GetElapsedTime(proofAt).TotalMilliseconds });
                    Require(fingerprint == packetReference, "Batch source indexing changed complete ordered evidence.");
                    inProgressBatch = null; inProgressLabel = null; inProgressExtraHandles = inProgressReplacements = 0;
                }
                else
                {
                    Require(trial.GetProperty("kind").GetString() == "extract", "Unknown attribution trial kind.");
                    var indexed = new List<(FileStream Stream, ReplayTransportIndex Index)>();
                    foreach (var item in originals)
                    {
                        Guard(); var stream = streams[Path.GetFullPath(item.GetProperty("path").GetString())]; stream.Position = 0;
                        indexed.Add((stream, ReplayTransportReader.Read(stream, stream.Length, 60, true, Guard)));
                    }
                    double indexMilliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    long extractAt = Stopwatch.GetTimestamp(), extractAllocation = GC.GetAllocatedBytesForCurrentThread(), pictures = 0, audio = 0, payloadBytes = 0;
                    var timing = new ReplayPacketExtractionTiming(); using var workspace = new ReplayPacketSampleWorkspace();
                    foreach (var input in indexed)
                    {
                        input.Stream.Position = 0;
                        ReplayTransportPacketSamples.Read(input.Stream, input.Index,
                            (_, payload) => { pictures++; payloadBytes += payload.Length; }, (_, payload) => { audio++; payloadBytes += payload.Length; },
                            Guard, workspace, timing);
                    }
                    process.Refresh();
                    rows.Add(new { label, kind = "extract-to-null", indexMilliseconds, extractMilliseconds = Stopwatch.GetElapsedTime(extractAt).TotalMilliseconds,
                        cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds - cpu, allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
                        extractionAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - extractAllocation,
                        pictures, audio, payloadBytes, timing, workspace.CapacityBytes,
                        limitation = "All301original chunks to null, not selected/head mux, file writing, validation, worker or Saved." });
                }
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            bool hadFailure = failure != null;
            phase = "final-owner-disposal"; long disposalAt = Stopwatch.GetTimestamp(); Exception closing = null;
            var owners = streams.Values.Concat(uncertainSourceOwners).Distinct().ToArray();
            var handles = owners.Select(s =>
            {
                try { return s.SafeFileHandle; }
                catch (ObjectDisposedException) { return null; }
                catch (Exception error) { closing ??= error; return null; }
            }).ToArray();
            foreach (var stream in owners)
                try { stream.Dispose(); } catch (Exception error) { closing ??= error; }
            finalOwnersClosed = closing == null && handles.All(h => h == null || h.IsClosed);
            finalDisposalMilliseconds = Stopwatch.GetElapsedTime(disposalAt).TotalMilliseconds;
            if (closing != null || !finalOwnersClosed)
            { extraCloseError ??= closing?.ToString() ?? "Final input owner closure unconfirmed."; failure ??= extraCloseError; }
            byte[] report = JsonSerializer.SerializeToUtf8Bytes(new { status = failure == null ? "passed-file-attribution" : "failed", error = failure,
                parentPid = Environment.ProcessId, totalBytes, peakRam, peakHandles, maxGap, seconds = clock.Elapsed.TotalSeconds, rows,
                inProgressLabel, inProgressBatch, inProgressExtraHandles, inProgressReplacements, extraCloseError, finalOwnersClosed, finalDisposalMilliseconds,
                children = 0, capture = false, publication = false, limitation = "Warm read-only diagnostic, no semantic decoding or complete export performance." });
            Require(report.Length <= 1048576, "Attribution report cap."); File.WriteAllBytes(Path.Combine(root, "outcome.json"), report);
            if (!hadFailure && (closing != null || !finalOwnersClosed)) throw new IOException("Final source lease cleanup failed; report retained.", closing);
        }
    }
    static string Digest(IEnumerable<string> values)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string value in values) digest.AppendData(Convert.FromHexString(value));
        return Convert.ToHexString(digest.GetHashAndReset());
    }
}
