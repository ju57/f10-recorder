namespace F10Recorder;

internal static class CalibrationPublicationChecks
{
    internal static int Run()
    {
        object gate = new();
        using var preparing = new ManualResetEventSlim();
        using var finishPreparation = new ManualResetEventSlim();
        using var observed = new ManualResetEventSlim();
        Exception failure = null;
        bool published = false, publicationHeldGate = false;
        var publisher = new Thread(() => {
            try
            {
                ReplayCalibrationRun.WithPublicationGate(gate, () => {
                    preparing.Set();
                    if (!finishPreparation.Wait(3000)) throw new IOException("Preparation control deadline.");
                }, () => { publicationHeldGate = Monitor.IsEntered(gate); published = true; });
            }
            catch (Exception error) { failure = error; }
        });
        Thread observer = null;
        bool observationDuringPreparation = false;
        try
        {
            publisher.Start();
            if (!preparing.Wait(2000)) throw new IOException("Publication control did not reach preparation.");
            observer = new Thread(() => { lock (gate) observed.Set(); });
            observer.Start();
            observationDuringPreparation = observed.Wait(1000);
        }
        finally
        {
            finishPreparation.Set();
            bool publisherJoined = publisher.Join(3000);
            bool observerJoined = observer?.Join(3000) != false;
            if (!publisherJoined || !observerJoined)
                throw new IOException("Publication control threads did not join.");
        }
        if (failure != null) throw failure;
        if (!observationDuringPreparation)
        {
            Console.Error.WriteLine("FAIL: slow publication preparation blocks the storage observation gate.");
            return 1;
        }
        if (!published || !publicationHeldGate) throw new IOException("Atomic publication must retain the observation gate.");

        bool renamedAfterFailure = false;
        try { ReplayCalibrationRun.WithPublicationGate(gate, () => throw new IOException("expected"), () => renamedAfterFailure = true); }
        catch (IOException error) when (error.Message == "expected") { }
        if (renamedAfterFailure) throw new IOException("Failed preparation must reject publication.");
        var expected = new ReplayFileExtent("volume", "checked", 4096, 123, 4096);
        ReplayCalibrationRun.VerifyPublicationObservation(expected, expected);
        foreach (var changed in new[] { expected with { Volume = "foreign" }, expected with { Identity = "foreign" },
            expected with { Length = 124 }, expected with { AllocationUnit = 8192 } })
        {
            bool rejected = false;
            try { ReplayCalibrationRun.VerifyPublicationObservation(changed, expected); }
            catch (IOException) { rejected = true; }
            if (!rejected) throw new IOException("Foreign native output observation must reject bookkeeping.");
        }
        Console.WriteLine("Passed: observer progresses during slow preparation; atomic publication holds the gate; preparation failure rejects publication; threads joined.");
        Console.WriteLine("Passed: native publication bookkeeping rejects foreign volume, identity, EOF and allocation unit.");
        return 0;
    }
}
