using System.Diagnostics;

namespace F10Recorder;

internal sealed record CalibrationOutputObservationTiming(long Started, long WorkspaceObserved, long OutputObserved,
    long GateEntered, long Completed, string Error);

internal static class CalibrationOutputObservation
{
    // Shared by the actual qualification sampler and the file-only handoff
    // probe. No copied stand-in for native workspace/extent/identity checks.
    internal static void Run(string partial, object gate,
        Action<Dictionary<string, ReplayFileExtent>, ReplayFileExtent, long> merge,
        ReplayFileExtent expected = null, Action<CalibrationOutputObservationTiming> observed = null)
    {
        long started = Stopwatch.GetTimestamp(), workspaceAt = 0, outputAt = 0, gateAt = 0;
        Exception failure = null;
        try
        {
            var work = ReplayOutputWorkspace.Observe(partial, new ReplayFiles()); workspaceAt = Stopwatch.GetTimestamp();
            var extent = ReplayStorage.Observe(partial); outputAt = Stopwatch.GetTimestamp();
            ReplayCalibrationRun.VerifyPublicationObservation(extent, expected);
            long bytes = ReplayStorage.Bytes(work);
            lock (gate) { gateAt = Stopwatch.GetTimestamp(); merge(work, extent, bytes); }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { observed?.Invoke(new(started, workspaceAt, outputAt, gateAt, Stopwatch.GetTimestamp(), failure?.ToString())); }
            catch (Exception) when (failure != null) { }
        }
    }
}
