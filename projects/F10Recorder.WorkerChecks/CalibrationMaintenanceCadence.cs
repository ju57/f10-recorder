namespace F10Recorder;

// Qualification caller scheduling only. Product Maintain/500ms storage guards
// stay unchanged. Match ReplayEngine's completion-then-250ms maintenance, while
// independent source/foreground observations remain due every100ms.
internal sealed class CalibrationMaintenanceCadence
{
    readonly TimeProvider time;
    long observationDue, maintenanceDue, last;
    internal CalibrationMaintenanceCadence(TimeProvider time)
    { this.time = time; last = observationDue = maintenanceDue = time.GetTimestamp(); }
    long Now()
    {
        long now = time.GetTimestamp();
        if (now < last) throw new IOException("Qualification maintenance clock moved backwards.");
        return last = now;
    }
    long After(int milliseconds) => checked(Now() + (time.TimestampFrequency * milliseconds + 999) / 1000);
    internal bool ObservationDue => Now() >= observationDue;
    internal bool MaintenanceDue => Now() >= maintenanceDue;
    internal void ObservationCompleted() => observationDue = After(100);
    internal void MaintenanceCompleted() => maintenanceDue = After(250);
    internal int DelayMilliseconds => (int)Math.Clamp(Math.Ceiling(
        (Math.Min(observationDue, maintenanceDue) - Now()) * 1000d / time.TimestampFrequency), 0, 100);

    internal static int Checks()
    {
        static void Need(bool value) { if (!value) throw new IOException("Completion-based maintenance schedule changed."); }
        var clock = new Clock(); var schedule = new CalibrationMaintenanceCadence(clock);
        Need(schedule.ObservationDue && schedule.MaintenanceDue && schedule.DelayMilliseconds == 0);
        clock.Now = 20; schedule.ObservationCompleted(); clock.Now = 80; schedule.MaintenanceCompleted();
        Need(!schedule.ObservationDue && !schedule.MaintenanceDue && schedule.DelayMilliseconds == 40);
        clock.Now = 120; Need(schedule.ObservationDue && !schedule.MaintenanceDue);
        clock.Now = 130; schedule.ObservationCompleted(); Need(schedule.DelayMilliseconds == 100);
        clock.Now = 330; Need(schedule.ObservationDue && schedule.MaintenanceDue);
        schedule.ObservationCompleted(); clock.Now = 410; schedule.MaintenanceCompleted();
        Need(schedule.DelayMilliseconds == 20); // Maintenance does not postpone observation.
        clock.Now = 500; Need(schedule.ObservationDue && !schedule.MaintenanceDue);
        schedule.ObservationCompleted(); Need(schedule.DelayMilliseconds == 100);
        clock.Now = 600; schedule.ObservationCompleted(); Need(schedule.DelayMilliseconds == 60);
        clock.Now = 660; Need(schedule.MaintenanceDue && !schedule.ObservationDue);
        clock.Now = 730; schedule.MaintenanceCompleted();
        clock.Now = 979; Need(!schedule.MaintenanceDue);clock.Now = 980;Need(schedule.MaintenanceDue);
        clock.Now = 2000; Need(schedule.MaintenanceDue);clock.Now = 2050;schedule.MaintenanceCompleted();
        Need(!schedule.MaintenanceDue); // No catch-up burst after a delayed call.
        clock.Now = 2299;Need(!schedule.MaintenanceDue);clock.Now = 2300;Need(schedule.MaintenanceDue);
        bool backwardsRejected = false;clock.Now = 2299;
        try { _ = schedule.DelayMilliseconds; } catch (IOException) { backwardsRejected = true; }
        Need(backwardsRejected);
        Console.WriteLine("Maintenance cadence: due-time observations, exact250ms after completion, no catch-up, no100ms quantization, backwards-clock rejection passed.");
        return 0;
    }
    sealed class Clock : TimeProvider
    {
        internal long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }
}
