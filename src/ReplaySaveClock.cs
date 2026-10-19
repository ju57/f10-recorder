using System;

namespace F10Recorder
{
    // A save event uses the capture session's media epoch, independently of
    // FFmpeg's display-progress origin. A stopped session retains its first cutoff.
    internal sealed class ReplaySaveClock
    {
        readonly MediaClock clock;
        readonly object gate = new object();
        internal long EpochQpc { get { return clock.QpcEpoch; } }
        internal long Frequency { get { return clock.QpcFrequency; } }
        long? stoppedAt;
        internal ReplaySaveClock(MediaClock mediaClock)
        {
            if (mediaClock == null) throw new ArgumentNullException("mediaClock");
            clock = mediaClock;
        }
        internal double PositionAt(long triggerQpc)
        {
            lock (gate)
            {
                // A trigger predating a replacement session cannot save its footage.
                if (triggerQpc < clock.QpcEpoch) return 0;
                long end = stoppedAt.HasValue ? Math.Min(triggerQpc, stoppedAt.Value) : triggerQpc;
                return (end - clock.QpcEpoch) / (double)clock.QpcFrequency;
            }
        }
        internal void StopAt(long qpc)
        {
            lock (gate) if (!stoppedAt.HasValue) stoppedAt = Math.Max(clock.QpcEpoch, qpc);
        }
    }
}
