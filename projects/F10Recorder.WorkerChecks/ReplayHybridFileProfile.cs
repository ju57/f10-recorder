namespace F10Recorder;

// The concat-only file profile belongs to the frozen comparison evidence.
// Do not silently reinterpret old plans against a different construction.
internal static class ReplayHybridFileProfile
{
    static int Retired() => throw new IOException(
        "Historical concat profiling requires its frozen helper/product and evidence. See tests/replay-latency.md.");
    internal static int Prepare(string path, string root, string priorPlan, bool diagnostic = false) => Retired();
    internal static int Run(string path, string expected) => Retired();
}
