using System;
using System.Diagnostics;
using System.Globalization;

namespace F10Recorder
{
    // One acquisition epoch, independent of PCM delivery and FFmpeg startup.
    // UTC bridges the first ddagrab frame into this epoch once. Thereafter the
    // filter preserves the source's timestamp deltas instead of sampling RTC.
    internal sealed class MediaClock
    {
        internal readonly long QpcEpoch, QpcFrequency, UnixMicroseconds;
        internal readonly long BridgeUncertaintyTicks;
        internal MediaClock(long qpc, long frequency, long unixMicroseconds, long uncertainty)
        {
            if (frequency <= 0) throw new ArgumentOutOfRangeException("frequency");
            QpcEpoch = qpc; QpcFrequency = frequency; UnixMicroseconds = unixMicroseconds;
            BridgeUncertaintyTicks = uncertainty;
        }
        internal static MediaClock Capture()
        {
            long before = Stopwatch.GetTimestamp();
            long utc = (DateTime.UtcNow.Ticks - 621355968000000000L) / 10;
            long after = Stopwatch.GetTimestamp();
            return new MediaClock(before + (after - before) / 2, Stopwatch.Frequency, utc, after - before);
        }
        internal long SamplesAtQpc(long qpc, int sampleRate)
        {
            return (long)Math.Round((qpc - QpcEpoch) * (sampleRate / (double)QpcFrequency));
        }
        internal long SamplesAtReferenceTime(double reference100ns, int sampleRate)
        {
            double epoch100ns = QpcEpoch * (10000000.0 / QpcFrequency);
            return (long)Math.Round((reference100ns - epoch100ns) * (sampleRate / 10000000.0));
        }
        // The RTC bridge is used only for the first frame of an unclocked source.
        internal static string VideoTimestampExpression(string unixMicroseconds) =>
            "PTS+if(eq(N,0),(print(RTCTIME,24)-" + unixMicroseconds + ")/1000000/TB-PTS,PREV_OUTPTS-PREV_INPTS)";
        internal string VideoTimestampFilter =>
            "setpts='" + VideoTimestampExpression(UnixMicroseconds.ToString(CultureInfo.InvariantCulture)) + "'";
    }
}
