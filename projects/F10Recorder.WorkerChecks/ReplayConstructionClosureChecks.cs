namespace F10Recorder;

internal static class ReplayConstructionClosureChecks
{
    sealed class Close(Action action) : IDisposable { public void Dispose() => action(); }
    static ReplayExportRequest Request() => new(new("unused", "unused", 1, 20, 60, "h264_nvenc", ".ts", false, false),
        new(0, 1, 1), [], "unused", "unused", new());
    static bool Stopped(ReplayExportRequest request)
    {
        var lifetime = new ReplayExportLifetime();
        using (lifetime.Begin()) lifetime.ObserveRequest(request);
        return lifetime.Stopped;
    }
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    internal static int Run()
    {
        foreach (string uncertainty in new[] { "source", "writer", "assembly" })
        {
            var request = Request();
            if (uncertainty == "source") request.Closure.BeginSourceClose();
            if (uncertainty == "writer") request.Closure.BeginPacketMux();
            if (uncertainty == "assembly") request.Closure.BeginAssembly();
            Require(!Stopped(request), "Missing diagnostic timestamps erased actual " + uncertainty + " ownership uncertainty.");
        }
        var diagnosticOnly = Request();
        diagnosticOnly.Timing.ExportRecipe = ReplaySaveExport.PacketRecipe;
        diagnosticOnly.Timing.MuxStarted = diagnosticOnly.Timing.AssemblyStarted = diagnosticOnly.Timing.SourceCloseStarted = 1;
        Require(Stopped(diagnosticOnly), "Diagnostic timestamps created producer ownership without an operation.");
        foreach (bool index in new[] { false, true })
        {
            var request = Request();
            request.IndexBatch = new() { Joined = true };
            request.PacketMuxWork = new() { Joined = true };
            var readers = index ? request.IndexReaders : request.PacketReaders;
            readers.Begin();
            Require(!Stopped(request), "Diagnostic Joined released active source readers.");
            readers.Complete(true);
            request.IndexBatch.Joined = request.PacketMuxWork.Joined = false;
            Require(Stopped(request), "Diagnostic Joined=false invented active source readers.");
        }
        foreach (bool closeFails in new[] { false, true })
        {
            var request = Request(); int disposed = 0;
            request.Closure.SourceOpened();
            var guard = new IOException("Expected storage guard failure.");
            try
            {
                ReplaySaveExport.CloseSources([new Close(() => disposed++), new Close(() => {
                    disposed++; if (closeFails) throw new IOException("Expected owner-close failure."); })],
                    request.Timing, () => throw guard, null, request.Closure);
            }
            catch (IOException error) { Require(ReferenceEquals(error.InnerException, guard), "Original guard failure was replaced."); }
            Require(disposed == 2, "Guard/close failure skipped an owner.");
            request.Timing.SourceCloseStarted = request.Timing.SourceCloseCompleted = null;
            request.Timing.SourceHandlesClosed = null;
            Require(Stopped(request) == !closeFails, "Diagnostic erasure changed the real close outcome.");
            if (closeFails)
            {
                request.Closure.CompleteSourceClose(true);
                Require(!Stopped(request), "A later close report erased prior closure uncertainty.");
            }
        }
        Console.WriteLine("Passed: diagnostic presence/absence cannot create or release producer ownership; actual close/guard outcomes and sticky uncertainty preserved.");
        return 0;
    }
}
