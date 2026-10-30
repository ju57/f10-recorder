using System.Text.Json;

namespace F10Recorder;

// A name is a readiness signal only after all bytes are flushed and the writer
// is closed. The pending/final names are volatile native-directory observations,
// never cached leaf handles. One fixture publishes once into its fresh root.
internal static class CalibrationFixturePublication
{
    internal const string ReadyName = "fixture-ready.json", PendingName = ".fixture-ready.pending";
    internal const int MaximumBytes = 4096;
    internal static bool IsVolatile(string path) => Path.GetFileName(path).Equals(ReadyName, StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).Equals(PendingName, StringComparison.OrdinalIgnoreCase);
    internal static void Publish(string root, object value, Action<string> duringWrite = null)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new IOException("Fixture readiness exceeds its bounded document size.");
        string pending = Path.Combine(root, PendingName), ready = Path.Combine(root, ReadyName);
        using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough))
        {
            int half = bytes.Length / 2;
            output.Write(bytes.AsSpan(0, half)); duringWrite?.Invoke(pending);
            output.Write(bytes.AsSpan(half)); output.Flush(true);
        }
        File.Move(pending, ready); // No overwrite or replacement of a prior fixture.
    }
    internal static JsonDocument Read(string root)
    {
        using var input = new FileStream(Path.Combine(root, ReadyName), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length <= 0 || input.Length > MaximumBytes) throw new IOException("Fixture readiness document is empty or oversized.");
        return JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 8 });
    }
}
