using F10Recorder;

internal static class PortableReplayStorageSafetyChecks
{
    internal static void Run()
    {
        const string volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";
        var growth = new ReplayCaptureHeadroom(new string('A', 64), volume, 4096, 65536, 32768);
        ReplayStorageAssessment Assess(long bytes, ReplayCaptureHeadroom model) => ReplayStorageSafety.Assess(new(volume, 4096, bytes), model);
        void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
        var missing = Assess(20_000_000_000, null);
        Require(missing.ShouldStop && !missing.SafeToResume && !missing.HeadroomKnown, "Abundant space must not substitute for measured capture growth.");
        long boundary = 2_000_000_000 + 65536 + 32768;
        Require(Assess(boundary, growth).ShouldStop && !Assess(boundary, growth).SafeToResume,
            "The poll and stop growth promises must protect the two-decimal-GB floor at equality.");
        var warning = Assess(boundary + 1, growth);
        Require(warning.ShouldWarn && !warning.ShouldStop && warning.SafeToResume, "Qualified warning capacity must permit explicit safe Resume above the stop boundary.");
        Require(!Assess(5_000_000_000, growth).ShouldWarn && Assess(4_999_999_999, growth).ShouldWarn,
            "The warning must use decimal five GB and strict below semantics.");
        Require(Assess(long.MaxValue, growth with { PollGrowthBytes = long.MaxValue }).ShouldStop,
            "Overflowing future growth must reject admission rather than wrap to safe capacity.");
        Require(Assess(20_000_000_000, growth with { Volume = @"\\?\Volume{00000000-0000-0000-0000-000000000002}\" }).ShouldStop,
            "Qualification must remain bound to its actual volume.");
        Console.WriteLine("Passed: portable capture storage admission, warning/stop boundaries, overflow and volume binding.");
    }
}
