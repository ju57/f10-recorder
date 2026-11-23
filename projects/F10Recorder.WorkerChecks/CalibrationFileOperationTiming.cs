using System.Diagnostics;
using System.Runtime.InteropServices;

namespace F10Recorder;

internal sealed record CalibrationFileOperationTiming(string Path, string Operation, double Milliseconds,
    bool Succeeded, uint ThreadBefore, uint ThreadAfter, double? ThreadCpuMilliseconds, double GcPauseMilliseconds);

// One maximum and counter per fixed operation/media kind; never retain a row
// per file or queue work. CPU counters are coarse and cannot classify fast calls
// or identify the reason for off-CPU time.
internal sealed class CalibrationFileOperationSummary
{
    internal sealed record Entry(long Count, long Failures, CalibrationFileOperationTiming Slowest);
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    internal void Record(CalibrationFileOperationTiming row)
    {
        if (row.Operation is not ("open" or "observe" or "close" or "refresh"))
            throw new ArgumentException("Unknown metadata operation.");
        string kind = row.Path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? "ts" : "mp4";
        string key = kind + ":" + row.Operation;
        row = row with { Path = ReplayOutputDemand.Bounded(row.Path, 512) };
        entries.TryGetValue(key, out var previous);
        entries[key] = new((previous?.Count ?? 0) + 1, (previous?.Failures ?? 0) + (row.Succeeded ? 0 : 1),
            previous == null || row.Milliseconds >= previous.Slowest.Milliseconds ? row : previous.Slowest);
    }
    internal Dictionary<string, Entry> Snapshot() => new(entries, StringComparer.Ordinal);
}

internal static class CalibrationOperationClock
{
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    internal readonly record struct Reading(long At, uint Thread, long? Cpu, TimeSpan GcPause);
    internal static Reading Read()
    {
        long at = Stopwatch.GetTimestamp(); uint thread = GetCurrentThreadId();
        long? cpu = GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? kernel + user : null;
        return new(at, thread, cpu, GC.GetTotalPauseDuration());
    }
    internal static CalibrationFileOperationTiming Complete(Reading before, string path, string operation, bool succeeded)
    {
        var after = Read();
        return new(path, operation, Stopwatch.GetElapsedTime(before.At).TotalMilliseconds, succeeded,
            before.Thread, after.Thread, before.Thread == after.Thread && before.Cpu.HasValue && after.Cpu.HasValue
                ? (after.Cpu.Value - before.Cpu.Value) / 10000d : null,
            (after.GcPause - before.GcPause).TotalMilliseconds);
    }
}
