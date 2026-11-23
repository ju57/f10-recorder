namespace F10Recorder;

// Metadata belongs to one request. No cache arrays/configuration escape their
// owner, and only genuine misses start parser workers.
internal sealed class ReplayPreparedIndexAcquisition
{
    internal bool Complete { get; private set; }
    internal int Hits { get; private set; }
    internal int Misses { get; private set; }

    internal ReplayTransportIndex[] Read(IReadOnlyList<ReplayTransportSource> sources,
        IReadOnlyList<ReplayTransportIndex> prepared, int fps, bool audio, Action check,
        ReplayTransportBatchTiming timing, ReplayWorkerLifetime readers)
    {
        if (Complete || sources.Count == 0 || sources.Count != prepared.Count)
            throw new IOException("Prepared replay acquisition requires one complete request selection.");
        var result = new ReplayTransportIndex[sources.Count];
        var missing = new List<int>();
        var guard = new ReplayPlanningGuard(check);
        guard.CheckNow();
        for (int n = 0; n < result.Length; n++)
        {
            guard.Check();
            if (prepared[n] is { } index)
            {
                if (index.Bytes != sources[n].Bytes) throw new IOException("Prepared replay extent differs from its protected source.");
                result[n] = index; Hits++;
            }
            else missing.Add(n);
        }
        Misses = missing.Count;
        if (missing.Count != 0)
        {
            var parsed = ReplayTransportBatchIndex.Read(missing.Select(n => sources[n]).ToArray(),
                fps, audio, 4, check, timing, cancellableReads: true,
                readDeadline: TimeSpan.FromSeconds(30), lifetime: readers);
            for (int n = 0; n < missing.Count; n++) result[missing[n]] = parsed[n];
        }
        guard.CheckNow();
        if (!readers.Joined || result.Any(i => i == null)) throw new IOException("Prepared replay acquisition did not join every real reader.");
        Complete = true;
        return result;
    }

    internal static ReplayTransportIndex Copy(ReplayTransportIndex index) => index with
    {
        Pictures = Array.AsReadOnly(index.Pictures.ToArray()),
        Audio = Array.AsReadOnly(index.Audio.ToArray()),
        Timing = Array.AsReadOnly(index.Timing.ToArray()),
        Sps = index.Sps.ToArray(), Pps = index.Pps.ToArray(),
        AudioFrames = index.AudioFrames == null ? null : Array.AsReadOnly(index.AudioFrames.ToArray())
    };

    // Conservative retained-data accounting, not an assertion about GC heap
    // residency. Entries above the allowance remain ordinary parser misses.
    internal static long Allowance(ReplayTransportIndex index) => checked(4096L
        + index.Pictures.Count * 512L + index.Audio.Count * 64L
        + index.Timing.Count * 64L + (index.AudioFrames?.Count ?? 0) * 256L
        + index.Sps.Length + index.Pps.Length);
}
