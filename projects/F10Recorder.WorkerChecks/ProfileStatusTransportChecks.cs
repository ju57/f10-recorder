using System.Text;
using System.Text.Json;

namespace F10Recorder;

// Production status projection, framed protocol and headless worker-client seam.
// Real Snapshot/pipe wiring is covered separately by ProfileWorkerChecks.
internal static class ProfileStatusTransportChecks
{
    internal static int Run(string root) => RunAsync(root).GetAwaiter().GetResult();
    static async Task<int> RunAsync(string root)
    {
        Directory.CreateDirectory(root); var checks = new List<object>(); int failed = 0;
        foreach (string name in new[] { "small-positive", "literal-unicode", "near-limit-ascii" })
        {
            try
            {
                string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
                const string prefix = "{\"VideoCodec\":\"hevc_nvenc\",\"SystemAudio\":false,\"DisplayIndex\":0,\"SaveModifiers\":0,\"OpaquePreference\":\"";
                const string suffix = "\"}";
                string opaque = name == "small-positive" ? "preserve-small" : name == "literal-unicode" ? new string('é', 180000) : new string('a', 1048576 - 128 - Encoding.UTF8.GetByteCount(prefix + suffix));
                byte[] original = Encoding.UTF8.GetBytes(prefix + opaque + suffix);
                string settingsPath = Path.Combine(folder, "settings.json"), statePath = Path.Combine(folder, "worker-state.json");
                File.WriteAllBytes(settingsPath, original); File.WriteAllText(statePath, "{\"Paused\":true,\"UserStopped\":true}");
                byte[] originalState = File.ReadAllBytes(statePath);
                var profile = RecordingProfile.Load(folder);
                Require(WorkerProtocol.MaxBytes == 1048576 && profile.Status.CanWrite && profile.Settings.AdditionalPreferences["OpaquePreference"].GetString() == opaque, "Load-admitted input or the unchanged 1 MiB limit was lost.");
                var source = new WorkerStatus { Instance = "original-worker", Revision = 1, Config = profile.Settings, ProfileStatus = profile.Status, Paused = true, Phase = CapturePhase.Paused, Metrics = new RecorderMetrics() };
                var projected = source.ForClient();
                Require(projected.Config.AdditionalPreferences.Count == 0 && projected.RetainedPreferenceCount == 1 && projected.ForClient().RetainedPreferenceCount == 1, "Opaque preference receipt/projection is missing or not stable.");
                using var stream = new MemoryStream();
                await WorkerProtocol.WriteAsync(stream, new WorkerReply { Status = projected }, CancellationToken.None);
                long framedBytes = stream.Length; stream.Position = 0;
                var received = await WorkerProtocol.ReadAsync<WorkerReply>(stream, CancellationToken.None);
                Require(framedBytes <= 1048576 + 4 && received.Status.Config.VideoCodec == "hevc_nvenc" && !received.Status.Config.SystemAudio && received.Status.Config.DisplayIndex == 0 && received.Status.Config.SaveModifiers == 0 && received.Status.Paused && received.Status.ProfileStatus.ActivationRequired, "Bounded roundtrip changed represented intent or activation.");
                using (var client = new WorkerRecorder(received.Status, _ => Task.FromResult(received)))
                {
                    var replacement = source.ForClient(); replacement.Instance = "replacement-worker"; replacement.Revision = 2;
                    client.Update(replacement); client.Update(received.Status);
                    Require(client.EngineCapabilityContext.StartsWith("replacement-worker:") && client.Config.AdditionalPreferences.Count == 0 && client.Config.VideoCodec == "hevc_nvenc" && client.Paused, "Reconnect accepted stale status or transferred opaque data.");
                }
                bool rawRejected = false; int rawBytes = JsonSerializer.SerializeToUtf8Bytes(new WorkerReply { Status = source }, WorkerProtocol.Json).Length;
                if (name != "small-positive")
                {
                    Require(rawBytes > 1048576, "The raw-envelope sensitivity fixture no longer exceeds the specified bound.");
                    using var rawStream = new MemoryStream();
                    try { await WorkerProtocol.WriteAsync(rawStream, new WorkerReply { Status = source }, CancellationToken.None); }
                    catch (InvalidDataException) { rawRejected = true; }
                    Require(rawRejected && rawStream.Length == 0, "An unprojected oversized payload must still reject before writing any header/body.");
                }
                Require(source.Config.AdditionalPreferences["OpaquePreference"].GetString() == opaque && File.ReadAllBytes(settingsPath).SequenceEqual(original) && File.ReadAllBytes(statePath).SequenceEqual(originalState), "Status projection/reconnect changed worker-owned settings or original bytes.");
                if (name == "small-positive")
                {
                    profile.Commit(profile.Settings, paused: true, userStopped: true, activationRequired: true);
                    Require(RecordingProfile.Load(folder).Settings.AdditionalPreferences["OpaquePreference"].GetString() == opaque, "Disk serialization stopped retaining opaque preferences.");
                }
                checks.Add(new { name, result = "passed", inputBytes = original.Length, rawReplyBytes = rawBytes, projectedReplyBytes = framedBytes - 4, rawRejected });
            }
            catch (Exception ex) { failed++; checks.Add(new { name, result = "failed", error = ex.ToString() }); }
        }
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = failed == 0 ? "passed" : "failed", checks, effects = "Local files, production projection/framed protocol/headless client only; no native windows, shortcuts, pipes, recorder, engine, GPU, capture or startup." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Profile status transport: {checks.Count - failed}/{checks.Count} passed. {root}");
        return failed == 0 ? 0 : 1;
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
