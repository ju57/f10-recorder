using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace F10Recorder;

internal static class ReplayWorkerPilotManifestChecks
{
    internal static int Run()
    {
        string first = Path.GetFullPath("runtime-fixture/a.dll"), second = Path.GetFullPath("runtime-fixture/b.dll");
        string[] selected = [first, second];
        byte[] good = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { [first] = "AA", [second] = "BB" });
        static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));
        if (ReplayWorkerLatencyPilot.ParseRuntime(good, Hash(good), selected).Count != 2) throw new IOException("Complete bound manifest rejected.");
        int rejected = 0;
        void Reject(byte[] raw, string hash, string[] paths)
        {
            try { ReplayWorkerLatencyPilot.ParseRuntime(raw, hash, paths); }
            catch (IOException) { rejected++; return; }
            throw new IOException("Unbound or incomplete runtime manifest accepted.");
        }
        Reject(good, new string('0', 64), selected);
        Reject(good, Hash(good), [first]);
        Reject(good, Hash(good), [first, second, Path.GetFullPath("runtime-fixture/c.dll")]);
        foreach (string duplicate in new[] { first, first.ToUpperInvariant() })
        {
            byte[] raw = Encoding.UTF8.GetBytes("{" + JsonSerializer.Serialize(first) + ":\"AA\"," + JsonSerializer.Serialize(duplicate) + ":\"BB\"}");
            Reject(raw, Hash(raw), [first]);
        }
        Reject([], Hash([]), selected);
        byte[] oversized = new byte[262145]; Reject(oversized, Hash(oversized), selected);
        Console.WriteLine("Passed: one bound complete runtime manifest; " + rejected + " hash/scope/duplicate/size rejection controls. No file, device or process action.");
        return 0;
    }
}
