using System.Diagnostics;

namespace F10Recorder;

// Native directory/ticket transfers with a simulated process transport. No
// codec/media/device/UI/capture; run only inside a reviewed fresh native plan.
internal static class ReplayStagedWorkspaceChecks
{
    static void Need(bool value, string text) { if (!value) throw new IOException(text); }
    static void Reject(Action action, string text) { try { action(); } catch (IOException) { return; } throw new IOException(text); }
    internal static object Run(string root, Action bound)
    {
        Directory.CreateDirectory(root);
        using var files = new ReplayRootedFiles(); files.Root(root);
        var info = new ReplaySessionInfo(root, Path.Combine(root, "unused.csv"), 300, 20, 60, "h264_nvenc", ".ts", false, true);
        var interval = new ReplayInterval(.25, 1.25, 300);
        var candidate = new Segment { Path = Path.Combine(root, "unused.ts"), Start = 0, End = 1 };
        string partial = Path.Combine(root, "owned.partial.mp4");
        using var preparation = new ReplaySavePreparation(files, info, interval, candidate, partial, bound);
        int launches = 0; bool childClosed = false;
        preparation.Start((_, retained) => new ReplayStagedProcess((guard, ready, _) => {
            launches++; guard(); using var sink = new MemoryStream();
            ready(sink, () => Volatile.Write(ref childClosed, true));
            while (!Volatile.Read(ref childClosed)) { guard(); Thread.Sleep(1); }
            return new() { ProducerAbsent = true, ActualExitObserved = true, ExitCode = 0, CaptureComplete = true, ResourcesFinal = true };
        }, () => { }, bound, 2000, retained));
        var clock = Stopwatch.StartNew();
        while (preparation.Timing.Ready == 0) { preparation.Pump(); Need(clock.ElapsedMilliseconds < 1000, "Fake launch readiness."); Thread.Sleep(1); }
        Need(launches == 1 && !preparation.Stopped && preparation.ObserveWork().Count == 1 && preparation.Timing.FedBytes == 0,
            "Prepared native workspace consumed data or lost live producer ownership.");
        var request = new ReplayExportRequest(info, interval, [], "unused", partial, new()); request.Preparation = preparation;
        request.NativeFiles = new(files, request); preparation.BindRequest(request);
        Reject(() => preparation.TransferClosed(request), "Live writer crossed closed-boundary capability.");
        Reject(() => preparation.BindRequest(request), "Workspace bound twice.");
        preparation.Cancel(); Need(preparation.Joined && preparation.Timing.FedBytes == 0, "Canceled unfed producer not joined.");
        request.NativeFiles.AdoptPreparedBoundary();
        Reject(() => request.NativeFiles.AdoptPreparedBoundary(), "Closed boundary adopted twice.");
        using (var observed = request.NativeFiles.Observe()) Need(observed.Work.Count == 1, "Closed boundary native observation lost owner.");
        request.NativeFiles.Dispose(); preparation.Dispose();
        files.DeleteDirectory(request.WorkDirectory);
        Need(!Directory.Exists(request.WorkDirectory), "Ordinary rooted prepared-work retirement failed.");
        string foreign = Path.Combine(root, "foreign.partial.mp4"); files.CreateDirectory(foreign + ".work");
        Reject(() => new ReplaySavePreparation(files, info, interval, candidate, foreign, bound), "Foreign workspace reused.");
        files.DeleteDirectory(foreign + ".work");
        bound();
        return new { result = "pass", launches, bytesFed = preparation.Timing.FedBytes, liveTransferRejected = true,
            duplicateBindRejected = true, duplicateTransferRejected = true, foreignWorkspaceRejected = true,
            actualWorkspaceAbsent = !Directory.Exists(request.WorkDirectory), realMediaProcesses = 0 };
    }
}
