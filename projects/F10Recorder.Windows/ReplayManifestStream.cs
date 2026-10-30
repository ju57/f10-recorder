using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace F10Recorder;

// The segmenter streams one completed CSV entry on stdout beside progress records.
// Only this owner publishes the bounded on-disk list; its readers share the gate.
// No reader ever races a producer-owned MoveFileEx replacement of segments.csv.
internal sealed class ReplayManifestStream
{
    internal static string[] OutputArguments() => ["-segment_list", "pipe:1", "-segment_list_type", "csv", "-segment_list_size", "0"];
    readonly object gate = new();
    readonly string directory, manifest;
    readonly int maximumEntries;
    readonly Regex entry;
    readonly Queue<string> lines = new();
    readonly Func<string, string, List<Segment>> read;
    readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Exception failure;
    long lastIndex = -1;
    double lastStart = -1, lastEnd = -1;

    internal ReplayManifestStream(string directory, string extension, int maximumEntries,
        Func<string, string, List<Segment>> read = null)
    {
        if (maximumEntries is < 1 or > 4000 || extension is not (".ts" or ".mkv"))
            throw new ArgumentException("Invalid bounded replay manifest format.");
        this.directory = Path.GetFullPath(directory); manifest = Path.Combine(this.directory, "segments.csv");
        this.maximumEntries = maximumEntries; this.read = read ?? ReplaySelection.Read;
        entry = new Regex("^(chunk-([0-9]{9})" + Regex.Escape(extension) + "),([0-9]+(?:\\.[0-9]+)?),([0-9]+(?:\\.[0-9]+)?)$", RegexOptions.CultureInvariant);
    }

    internal bool Accept(string line)
    {
        if (line == null || (!line.StartsWith("chunk-", StringComparison.Ordinal) && !line.Contains(','))) return false;
        lock (gate)
        {
            if (failure != null) return true;
            try
            {
                if (completed.Task.IsCompleted || line.Length > 256) throw new IOException("Unexpected replay manifest entry after stream completion or above the line limit.");
                var match = entry.Match(line);
                if (!match.Success) throw new IOException("Malformed completed replay segment entry.");
                long index = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                double start = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), end = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start
                    || index <= lastIndex || start < lastStart || end <= lastEnd)
                    throw new IOException("Completed replay segment identity/timeline did not progress.");
                lines.Enqueue(line);
                while (lines.Count > maximumEntries) lines.Dequeue();
                string text = string.Join('\n', lines) + "\n";
                if (Encoding.UTF8.GetByteCount(text) > 1_048_576) throw new IOException("Replay manifest exceeds its bounded publication size.");
                string temporary = manifest + ".tmp";
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                File.Move(temporary, manifest, true);
                lastIndex = index; lastStart = start; lastEnd = end;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
            { failure = ex; }
        }
        return true;
    }

    internal List<Segment> Read()
    {
        lock (gate) { CheckHealthy(); return read(manifest, directory); }
    }
    internal void Complete() => completed.TrySetResult();
    internal void WaitForCompletion(int milliseconds)
    {
        if (!completed.Task.Wait(milliseconds))
        {
            var error = new IOException("Completed replay segment metadata did not drain before the shutdown deadline.");
            Interlocked.CompareExchange(ref failure, error, null);
            throw error;
        }
        CheckHealthy();
    }
    internal void CheckHealthy()
    {
        lock (gate)
            if (failure != null) throw new IOException("Replay manifest publication failed; footage is retained.", failure);
    }
}
