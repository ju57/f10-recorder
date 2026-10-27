using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace F10Recorder;

internal sealed record PilotFixtureAdmission(string Root, string RootIdentity, string Nonce, string Mode, string InputSha256, string PlanSha256);

// Closed nonmedia fixture. No process start, recorder, window, engine or device calls.
internal static class PilotStageFixture
{
    internal static int Run(string path)
    {
        string full = Path.GetFullPath(path), root = Path.GetDirectoryName(full);
        if (full != path || Path.GetFileName(full) != "fixture.json" || !full.StartsWith(PilotStorageObservation.Artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture admission path rejected.");
        using var anchor = Native.AnchorDirectory(root);
        if (!string.Equals(Native.FinalDirectoryPath(anchor), root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture root alias rejected.");
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 8192) throw new IOException("Fixture admission too large.");
        var admission = JsonSerializer.Deserialize<PilotFixtureAdmission>(stream, PilotStageRunner.Json) ?? throw new IOException("Missing fixture admission.");
        if (admission.Root != root || admission.RootIdentity != Native.DirectoryIdentity(anchor) || admission.Nonce != Environment.GetEnvironmentVariable("F10_PILOT_STAGE_NONCE") || admission.Nonce?.Length != 32
            || admission.Mode is not ("success" or "missing-product" or "exit7" or "wait" or "flood")) throw new IOException("Fixture binding/route rejected.");
        using var inputFile = new FileStream(Path.Combine(root, "input.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (inputFile.Length > 4096 || Convert.ToHexString(SHA256.HashData(inputFile)) != admission.InputSha256) throw new IOException("Fixture input changed.");
        void Write(string name, string text)
        {
            using var file = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.Write(Encoding.UTF8.GetBytes(text));
            file.Flush(true);
        }
        Write("progress.txt", "partial\n");
        if (admission.Mode == "exit7") { Console.Error.WriteLine("fixture-exit7"); return 7; }
        if (admission.Mode == "wait") { Console.WriteLine("partial-ready"); Thread.Sleep(10_000); return 9; }
        if (admission.Mode == "flood")
        {
            string block = new('x', 4096);
            for (int i = 0; i < 32; i++) { Console.Out.Write(block); Console.Out.Flush(); Console.Error.Write(block); Console.Error.Flush(); }
            return 8;
        }
        if (admission.Mode == "success") Write("product.txt", "stage-ok\n");
        Console.WriteLine("fixture-done");
        return 0;
    }
}
