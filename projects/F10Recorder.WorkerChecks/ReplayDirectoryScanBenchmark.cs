using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// The same source compiles against control/candidate product DLLs. Only native
// directory/output/workspace metadata primitives are timed; no numeric model,
// accepted save, codec, capture, or publication is involved.
internal static class ReplayDirectoryScanBenchmark
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    static void Need(bool value, string text) { if (!value) throw new IOException(text); }
    static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    internal static int Run(string planFile, string expectedSha)
    {
        Need(new FileInfo(planFile).Length <= 65536 && Hash(planFile) == expectedSha, "Exact metadata benchmark plan required.");
        using var plan = JsonDocument.Parse(File.ReadAllText(planFile)); var p = plan.RootElement;
        Need(p.GetProperty("kind").GetString() == "issue83-directory-scan-component-v1"
            && p.GetProperty("trials").GetInt32() == 20 && p.GetProperty("callbacks").GetInt32() == 100
            && DateTime.UtcNow.AddSeconds(45) < p.GetProperty("expiresUtc").GetDateTime(), "Fresh finite metadata comparison scope required.");
        foreach (var f in p.GetProperty("files").EnumerateObject()) Need(Hash(f.Name) == f.Value.GetString(), "Changed benchmark binding.");
        foreach (string file in new[] { Environment.ProcessPath, typeof(ReplayDirectoryScanBenchmark).Assembly.Location,
            typeof(CaptureSession).Assembly.Location, typeof(Settings).Assembly.Location })
            Need(p.GetProperty("files").TryGetProperty(file, out _), "Actual component binary unbound.");
        string root = Path.GetFullPath(p.GetProperty("root").GetString());
        Need(root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root), "Fresh isolated benchmark root required.");
        Directory.CreateDirectory(root); string source = Path.Combine(root, "inputs"); Directory.CreateDirectory(source);
        string partial = Path.Combine(root, "output.partial.mp4"), work = partial + ".work"; Directory.CreateDirectory(work);
        var clock = Stopwatch.StartNew(); using var process = Process.GetCurrentProcess();
        var rows = new List<object>(); var fixtureNames = new List<string>(); Exception failure = null;
        long peakRam = 0; int peakHandles = 0;
        void Bound()
        {
            process.Refresh(); peakRam = Math.Max(peakRam, process.WorkingSet64); peakHandles = Math.Max(peakHandles, process.HandleCount);
            Need(clock.Elapsed.TotalSeconds < 30 && peakRam <= 268435456 && peakHandles <= 1024, "Component time/RAM/handle limit.");
            Need(ReplayStorage.ObserveCapacity(root).AvailableBytes >= 60000000000, "Component free-space floor.");
        }
        try
        {
            var block = new byte[4096]; Array.Fill(block, (byte)0x5a);
            for (int n = 0; n < 301; n++) { string path = Path.Combine(source, "chunk-" + n.ToString("D9") + ".ts"); File.WriteAllBytes(path, block); fixtureNames.Add(path); }
            File.WriteAllBytes(partial, block); fixtureNames.Add(partial);
            string boundary = Path.Combine(work, "boundary-000000.ts"); File.WriteAllBytes(boundary, block); fixtureNames.Add(boundary);
            var files = new ReplayFiles(); var expectedOutput = ReplayStorage.Observe(partial); var expectedBoundary = ReplayStorage.Observe(boundary);
            void Callback()
            {
                var names = ReplayStorage.InventoryNamespaces([source], files);
                var output = ReplayStorage.DirectoryLeaf(partial);
                var extent = ReplayStorage.Observe(partial);
                var scratch = ReplayOutputWorkspace.Observe(partial, files);
                Need(names.Count == 301 && output.Extent.Identity == expectedOutput.Identity && extent == expectedOutput
                    && scratch.Count == 1 && scratch[boundary] == expectedBoundary
                    && !names.Values.Any(e => e.Identity == extent.Identity || e.Identity == expectedBoundary.Identity), "Fresh metadata/identity expectations failed.");
            }
            for (int n = 0; n < 5; n++) Callback(); // Explicit warm-up, included in whole resources but not timed rows.
            for (int trial = 0; trial < 20; trial++)
            {
                Bound(); long allocated = GC.GetAllocatedBytesForCurrentThread(); long at = Stopwatch.GetTimestamp();
                for (int n = 0; n < 100; n++) Callback();
                rows.Add(new { trial, callbacks = 100, milliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds,
                    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated });
            }
            // Fresh next-call namespace changes and early DirectoryLeaf iterator
            // disposal are exercised here; broader rejection remains in suites.
            string alias = Path.Combine(source, "late-alias.ts");
            Need(Native.CreateHardLink(alias, partial, IntPtr.Zero), "Private late-alias setup failed."); fixtureNames.Add(alias);
            var changed = ReplayStorage.InventoryNamespaces([source], files);
            Need(changed.Count == 302 && changed[alias].Identity == expectedOutput.Identity, "Fresh census missed a late alias.");
            File.Delete(alias); fixtureNames.Remove(alias);
            Need(ReplayStorage.InventoryNamespaces([source], files).Count == 301, "Fresh census retained a removed alias.");
            Bound();
        }
        catch (Exception error) { failure = error; Console.Error.WriteLine(error); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { result = failure == null ? "metadata-component-collected" : "failed", error = failure?.ToString(),
            rows, fixtureNames, seconds = clock.Elapsed.TotalSeconds, peakRam, peakHandles,
            scope = "20warm100-callback metadata bursts after5declaredwarmupcalls; fresh namespace census/outputleaf+extent/workspace queries eachcallback; private latealias add/remove visibility. NONMEDIA4KiBfixtures, no capture/media read/codec/catalog/Cache model/admission/publication/Saved. Allocation/wall component results do not establish replay acceptance latency." }, Json);
        Need(bytes.Length <= 262144, "Component report256KiB bound."); Bound();
        File.WriteAllBytes(Path.Combine(root, "result.json"), bytes); Bound();
        return failure == null ? 0 : 1;
    }
}
