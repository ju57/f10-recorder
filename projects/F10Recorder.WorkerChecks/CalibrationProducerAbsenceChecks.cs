namespace F10Recorder;

internal static class CalibrationProducerAbsenceChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool expected, ReplayExportProcessReceipt receipt, string name)
        {
            if (ReplayCalibrationRun.ConfirmedProducerAbsent(receipt) != expected)
                throw new IOException("Producer-absence classification failed: " + name);
            checks++;
        }
        var start = new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
        Check(false, null, "missing receipt");
        Check(false, new(), "default receipt is not a no-launch proof");
        Check(true, new() { Failure = "launch-failed" }, "explicit never-launched guard failure");
        Check(false, new() { Failure = "launch-failed", ProducerAbsent = false }, "never-launched shape with uncertainty");
        Check(false, new() { Failure = "process-failed" }, "missing PID alone is insufficient");
        Check(false, new() { Failure = "launch-failed", ProcessId = 123 }, "partial child identity");
        Check(false, new() { Failure = "launch-failed", ProcessStartedUtc = start }, "partial child start");
        Check(false, new() { Failure = "launch-failed", ActualExitObserved = true }, "missing launched identity");
        Check(false, new() { ProcessId = 123, ProcessStartedUtc = start }, "launched child without observed exit");
        Check(false, new() { ProcessId = 123, ProcessStartedUtc = start, ActualExitObserved = true, ProducerAbsent = false }, "uncertain producer remains blocked");
        Check(true, new() { ProcessId = 123, ProcessStartedUtc = start, ActualExitObserved = true, ExitCode = 0 }, "confirmed successful child exit");
        Check(true, new() { ProcessId = 123, ProcessStartedUtc = start, ActualExitObserved = true, ExitCode = 1, Failure = "exit-nonzero" }, "failed child can still be absent");
        Check(false, new() { ProcessId = 0, ProcessStartedUtc = start, ActualExitObserved = true }, "invalid launched PID");
        Console.WriteLine(checks + " pure producer-absence controls passed; no process, filesystem, capture or catalog action.");
        return 0;
    }
}
