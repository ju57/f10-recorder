using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using F10Recorder;

internal static class ReplayMp4SourceProofChecks
{
    static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static void Run(string path, string sha)
    {
        byte[] raw = File.ReadAllBytes(path);
        Require(raw.Length <= 262144 && Convert.ToHexString(SHA256.HashData(raw)).Equals(sha, StringComparison.OrdinalIgnoreCase), "Frozen source-proof plan changed.");
        using var document = JsonDocument.Parse(raw); var plan = document.RootElement;
        string root = Path.GetFullPath(plan.GetProperty("root").GetString());
        Require(plan.GetProperty("kind").GetString() == "issue83-video-source-proof-file-only-v1" && !Directory.Exists(root)
            && root.StartsWith(Path.GetFullPath("artifacts/issue83") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow.AddSeconds(75) < plan.GetProperty("expires_utc").GetDateTimeOffset(), "Fresh bounded source-proof admission required.");
        var files = plan.GetProperty("bindings").EnumerateObject().ToArray();
        Require(files.Length <= 600 && files.Any(f => Path.GetFullPath(f.Name).Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            && files.Any(f => Path.GetFullPath(f.Name).Equals(typeof(ReplayMp4SourceProofChecks).Assembly.Location, StringComparison.OrdinalIgnoreCase))
            && files.Any(f => Path.GetFullPath(f.Name).Equals(typeof(ReplayMp4Reader).Assembly.Location, StringComparison.OrdinalIgnoreCase)), "Current source-proof runtime/helper unbound.");
        Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "admitted-plan.json"), raw);
        var clock = Stopwatch.StartNew(); long last = 0, maximumRam = 0;
        void Guard()
        {
            Require(clock.Elapsed.TotalSeconds < 60, "Source-proof deadline.");
            if (Stopwatch.GetElapsedTime(last).TotalMilliseconds < 50) return;
            last = Stopwatch.GetTimestamp(); using var process = Process.GetCurrentProcess(); maximumRam = Math.Max(maximumRam, process.WorkingSet64);
            Require(maximumRam < 268435456 && new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace >= 60000000000L, "Source-proof resource/free-space bound.");
        }
        var streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        string failure = null; double indexSeconds = 0, outputSeconds = 0, comparisonSeconds = 0; int sourceCount = 0; long? matchedPictures = null;
        ReplayMp4Index output = null;
        try
        {
            long bytes = 0; byte[] buffer = new byte[65536];
            foreach (var file in files)
            {
                Guard(); var stream = new FileStream(file.Name, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                streams.Add(Path.GetFullPath(file.Name), stream); bytes = checked(bytes + stream.Length); Require(bytes <= 2000000000, "Source-proof input byte cap.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); int count;
                while ((count = stream.Read(buffer)) != 0) { hash.AppendData(buffer, 0, count); Guard(); }
                Require(Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Value.GetString(), StringComparison.OrdinalIgnoreCase), "Protected source-proof binding changed.");
            }
            FileStream Bound(string role) => streams[Path.GetFullPath(plan.GetProperty(role).GetString())];
            var selectionStream = Bound("selection"); selectionStream.Position = 0;
            using var baseline = JsonDocument.Parse(selectionStream); var selection = baseline.RootElement.GetProperty("selection");
            var inputs = selection.GetProperty("Inputs").EnumerateArray().ToArray(); Require(inputs.Length == 301, "Frozen301-source reference required.");
            var interval = new ReplayInterval(selection.GetProperty("Start").GetDouble(), selection.GetProperty("End").GetDouble(), 300);
            Require(Math.Abs(interval.Seconds - 300) < .000001, "Frozen reference interval changed.");
            long at = Stopwatch.GetTimestamp(); var indexed = new List<ReplayTransportInput>();
            foreach (var input in inputs)
            {
                Guard(); var stream = streams[Path.GetFullPath(input.GetProperty("Path").GetString())]; stream.Position = 0;
                indexed.Add(new(input.GetProperty("Start").GetDouble(), input.GetProperty("End").GetDouble(), ReplayTransportReader.Read(stream, stream.Length, 60, true, Guard)));
            }
            sourceCount = indexed.Count; var hybrid = ReplayHybridPlan.Create(indexed, interval, 60, true);
            var head = Bound("boundary"); head.Position = 0; var encoded = ReplayTransportReader.Read(head, head.Length, 60, false, Guard);
            var expected = new List<ReplayExpectedPicture>(); int partials = 0;
            foreach (var part in hybrid.Parts.Where(p => p.Count > 0))
            {
                var pictures = part.First == 0 ? indexed[part.Input].Index.Pictures.Take(part.Count) : encoded.Pictures;
                if (part.First != 0) Require(++partials == 1 && encoded.Pictures.Count == part.Count, "Expected exactly one retained partial-head encoder result.");
                foreach (var picture in pictures) expected.Add(new(picture.Idr, picture.CodedSha256, picture.PacketSha256));
            }
            Require(partials == 1 && expected.Count == 18000, "Exact selected physical picture proof count differs.");
            var proof = new ReplayMp4Expectation(60, hybrid.FirstFrame / 60d - interval.Start, indexed[0].Index.Sps.Span, indexed[0].Index.Pps.Span, expected);
            indexSeconds = Stopwatch.GetElapsedTime(at).TotalSeconds;
            at = Stopwatch.GetTimestamp(); var movie = Bound("output");
            output = ReplayMp4Reader.Read(movie, movie.Length, interval, 60, true, Guard); outputSeconds = Stopwatch.GetElapsedTime(at).TotalSeconds;
            at = Stopwatch.GetTimestamp(); matchedPictures = proof.VerifyVideo(output.Tracks.Single(t => t.Kind == "vide"), Guard);
            comparisonSeconds = Stopwatch.GetElapsedTime(at).TotalSeconds;
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            foreach (var stream in streams.Values) stream.Dispose();
            string report = JsonSerializer.Serialize(new { status = failure == null ? "passed-video-source-proof-only" : "failed", error = failure,
                process_id = Environment.ProcessId, executable = Environment.ProcessPath, sourceCount, matchedPictures, indexSeconds, outputSeconds, comparisonSeconds,
                audioPacketsInspected = output?.Tracks.SingleOrDefault(t => t.Kind == "soun")?.Samples.Count, sourceAudioProofMatched = false,
                maximum_observed_working_set = maximumRam, seconds_including_input_hashes = clock.Elapsed.TotalSeconds,
                capture = false, publication = false, media_processes = 0,
                limitation = "Protected original/head byte comparison for video only. Audio final packets inspected, not matched to pre-mux producer proof. Warm file-only experiment, no normal validation/publication/Saved claim." });
            Require(System.Text.Encoding.UTF8.GetByteCount(report) < 1048576, "Source-proof report bound."); File.WriteAllText(Path.Combine(root, "outcome.json"), report);
        }
    }
}
