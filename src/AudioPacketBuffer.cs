using System;
using System.Collections.Generic;
using System.IO;

namespace F10Recorder
{
    // Bounded native packet delivery, independently testable without an endpoint.
    // A device boundary/real inactivity may need silence; clock jitter may not.
    internal sealed class AudioPacketBuffer
    {
        sealed class Packet { internal long Start; internal byte[] Data; internal bool Boundary; }
        readonly Queue<Packet> packets = new Queue<Packet>();
        readonly AudioPacketClock clock;
        readonly TimestampedAudio transport;
        readonly int rate, align;
        readonly long initial;
        readonly byte[] silence;
        readonly bool unsigned;
        long written, idleEnd;
        bool emittedIdle;

        internal AudioPacketBuffer(Stream output, int sampleRate, int channels, string format, long initialSample)
        {
            rate = sampleRate;
            int bytes = format == "u8" ? 1 : format == "s16le" ? 2 : format == "s24le" ? 3 : 4;
            align = checked(bytes * channels);
            clock = new AudioPacketClock(rate);
            transport = new TimestampedAudio(output, rate, channels, format);
            initial = written = Math.Max(0, initialSample);
            unsigned = format == "u8";
            silence = new byte[Math.Max(align, (rate / 100) * align)];
            FillSilence(silence);
        }

        internal void Add(byte[] data, ulong devicePosition, long qpcSample, uint flags, bool timestampValid)
        {
            if (data == null || data.Length == 0 || data.Length % align != 0) throw new IOException("Invalid native PCM payload.");
            // Some loopback endpoints pause their device frame counter while
            // idle. Once idle PCM has been emitted, a trustworthy QPC must
            // reanchor resumed audio even if the counter resumes contiguously.
            var time = clock.Map(devicePosition, data.Length / align, qpcSample, timestampValid, (flags & 1) != 0 || emittedIdle);
            emittedIdle = false;
            if ((flags & 2) != 0) FillSilence(data);
            packets.Enqueue(new Packet { Start = time.Start, Data = data, Boundary = time.Boundary });
            if (packets.Count > 1000) throw new IOException("Audio output cannot keep up with capture.");
        }

        internal void Advance(long target)
        {
            if (target - Math.Max(written, initial) > rate * 2L) throw new IOException("Audio capture fell behind the system clock; capture must restart.");
            while (packets.Count > 0 && packets.Peek().Start <= target)
            {
                Packet packet = packets.Dequeue();
                int frames = packet.Data.Length / align;
                int offset = packet.Start < 0 ? (int)Math.Min(frames, -packet.Start) : 0;
                if (offset == frames) continue;
                long start = packet.Start + offset;
                if (start < idleEnd) throw new IOException("Native audio arrived after its idle range was emitted; capture must restart.");
                if (packet.Boundary) WriteSilence(start);
                transport.WriteAcquired(packet.Data, offset * align, (frames - offset) * align, start);
                written = start + frames - offset;
            }
            long silenceTarget = packets.Count == 0 ? target : packets.Peek().Boundary ? Math.Min(target, packets.Peek().Start) : written;
            if (silenceTarget > written)
            {
                WriteSilence(silenceTarget);
                idleEnd = written;
                emittedIdle = true;
            }
        }

        void WriteSilence(long end)
        {
            while (written < end)
            {
                int count = (int)Math.Min(end - written, silence.Length / align);
                transport.WriteAcquired(silence, 0, count * align, written);
                written += count;
            }
        }
        void FillSilence(byte[] data)
        {
            if (unsigned) for (int i = 0; i < data.Length; i++) data[i] = 128;
        }
    }
}
