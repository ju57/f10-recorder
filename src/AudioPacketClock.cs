using System;
using System.IO;

namespace F10Recorder
{
    // Device frames order the PCM; QPC places that stream on the shared A/V epoch.
    // Small timestamp jitter must never insert/drop native samples. The downstream
    // resampler reconciles the slowly corrected acquisition PTS with sample count.
    internal sealed class AudioPacketClock
    {
        // Initialize resampling even at matching rates: lazy activation on the
        // first positive correction can reinitialize the resampler's PTS anchor.
        internal const string ResamplerFilter = "aresample=async=100:min_comp=0.001:min_hard_comp=0.1:comp_duration=1:flags=res";
        readonly int rate;
        bool initialized;
        ulong expectedDevice;
        long nominalNext, lastObservation, lastStart, lastEnd;
        double phase;
        long uncertainFrames;

        internal AudioPacketClock(int sampleRate)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException("sampleRate");
            rate = sampleRate;
        }

        internal AudioPacketTime Map(ulong devicePosition, int frames, long qpcSample, bool timestampValid, bool discontinuity)
        {
            if (frames <= 0 || frames > rate * 2) throw new IOException("Invalid native audio packet size.");
            if (discontinuity && !timestampValid) throw new IOException("Cannot locate an audio discontinuity with an invalid timestamp; capture must restart.");
            uncertainFrames = timestampValid ? 0 : uncertainFrames + frames;
            if (uncertainFrames > rate * 5L) throw new IOException("Audio timestamps remained uncertain for more than five seconds; capture must restart.");
            if (initialized && timestampValid && devicePosition < expectedDevice && !discontinuity) throw new IOException("Audio device position moved backwards without a discontinuity; capture must restart.");
            bool boundary = !initialized || (timestampValid && (devicePosition != expectedDevice || discontinuity));
            long start;
            if (boundary)
            {
                if (!timestampValid) throw new IOException("Cannot establish an audio acquisition epoch from an invalid timestamp.");
                start = qpcSample;
                if (initialized && start < lastEnd) throw new IOException("Audio device reset overlapped the preceding acquisition range; capture must restart.");
                nominalNext = start;
                lastObservation = start;
                phase = 0;
            }
            else
            {
                // Conservative policy for TIMESTAMP_ERROR: ignore both reported
                // timing fields and continue counted frames. The next trustworthy
                // packet must reconcile its counter; this is not a native guarantee.
                if (timestampValid && nominalNext - lastObservation >= rate)
                {
                    double error = qpcSample - (nominalNext + phase);
                    if (Math.Abs(error) > rate / 10.0) throw new IOException("Audio clock changed without a device discontinuity; capture must restart.");
                    double limit = Math.Min(rate / 1000.0, frames / 4.0);
                    phase += Math.Max(-limit, Math.Min(limit, error * .1));
                    lastObservation = nominalNext;
                }
                start = checked(nominalNext + (long)Math.Round(phase));
                if (start <= lastStart) throw new IOException("Audio acquisition timestamps did not advance; capture must restart.");
            }
            nominalNext = checked(nominalNext + frames);
            expectedDevice = checked((timestampValid ? devicePosition : expectedDevice) + (ulong)frames);
            lastStart = start;
            lastEnd = checked(start + frames);
            initialized = true;
            return new AudioPacketTime { Start = start, Boundary = boundary };
        }
    }

    internal struct AudioPacketTime
    {
        internal long Start;
        internal bool Boundary;
    }
}
