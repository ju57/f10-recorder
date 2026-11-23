using System.Diagnostics;

namespace F10Recorder;

internal static class CalibrationValidationWorkChecks
{
    internal static int Run()
    {
        int observations = 0; CalibrationValidationWorkTiming receipt = null;
        var gate = new ReplayStorageObservationGate(() => { observations++; return true; }, 100, 500);
        gate.CheckNow();
        // The actual qualification observer can block outside the process
        // runner. A deterministic bounded delay models that observed seam,
        // not a claim to reproduce the original filesystem's exact cause.
        CalibrationValidationWork.Run(() => Thread.Sleep(650), gate.Check, value => receipt = value);
        gate.Check(); // Same first prelaunch freshness check as the failed path.
        if (receipt is not { Joined: true, Error: null } || observations < 2)
            throw new IOException("Qualification work did not service the existing product guard and join before decoder preparation.");
        var workError = new IOException("Expected observer error.");
        try { CalibrationValidationWork.Run(() => throw workError, () => { }); throw new Exception("Observer failure was lost."); }
        catch (IOException error) when (ReferenceEquals(error, workError)) { }
        bool ran = false; var expired = new IOException("Expected already-stale guard.");
        try { CalibrationValidationWork.Run(() => ran = true, () => throw expired); throw new Exception("Stale entry was accepted."); }
        catch (IOException error) when (ReferenceEquals(error, expired) && !ran) { }
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        int calls = 0; var guardError = new IOException("Expected guard failure during observer work.");
        try
        {
            CalibrationValidationWork.Run(() => {
                entered.Set(); if (!release.Wait(2000)) throw new IOException("Test observer release deadline."); finished.Set();
            }, () => {
                if (++calls == 1) return;
                if (!entered.Wait(2000)) throw new IOException("Test observer start deadline.");
                release.Set(); throw guardError;
            }, value => receipt = value);
            throw new Exception("Guard failure was lost.");
        }
        catch (IOException error) when (ReferenceEquals(error, guardError) && finished.IsSet && receipt.Joined) { }
        long now = 0;
        gate = new(() => { now += 501; return true; }, 100, 500, timestamp: () => now, frequency: 1000);
        try { CalibrationValidationWork.Run(() => { }, gate.Check); throw new Exception("Slow guard was excused."); }
        catch (IOException error) when (error.Message.Contains("exceeded 500 ms")) { }
        try { CalibrationValidationWork.Run(() => throw workError, () => { }, _ => throw new ApplicationException("Expected diagnostic error.")); throw new Exception("Diagnostic callback hid the primary observer failure."); }
        catch (IOException error) when (ReferenceEquals(error, workError)) { }
        var context = new CalibrationValidationWork.GuardContext(); var other = new CalibrationValidationWork.GuardContext();
        Action first = () => { }, second = () => { };
        using (context.Enter(first))
        {
            using (context.Enter(second))
                if (context.Current != second || other.Current != null) throw new IOException("Guard scope leaked across sources.");
            if (context.Current != first) throw new IOException("Nested guard scope was not restored.");
        }
        if (context.Current != null) throw new IOException("Guard scope remained after validation.");
        Console.WriteLine("Passed 8 handoff controls: SAME guard, stale/slow rejection, joined failure cleanup, primary error, diagnostic error and nested/per-source guard restoration.");
        return 0;
    }
}
