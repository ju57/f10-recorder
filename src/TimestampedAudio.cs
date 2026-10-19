using System;
using System.IO;
using System.Text;

namespace F10Recorder
{
    // Minimal streaming Matroska PCM transport: one timestamped block per write.
    // No seeking, indexing, compression or wall-clock timestamps are involved.
    internal sealed class TimestampedAudio
    {
        readonly Stream output;
        readonly int sampleRate, blockAlign;
        long nextSample = -1;
        long lastPacketStart = -1;
        internal TimestampedAudio(Stream output, int rate, int channels, string format)
        {
            this.output = output; sampleRate = rate;
            int bits;
            switch (format) { case "u8": bits = 8; break; case "s16le": bits = 16; break; case "s24le": bits = 24; break; case "s32le": case "f32le": bits = 32; break; default: throw new ArgumentException("Unsupported timestamped PCM format."); }
            if (rate <= 0 || channels <= 0) throw new ArgumentException("Invalid audio format.");
            blockAlign = checked(channels * bits / 8);
            byte[] header = Join(Element(0x1A45DFA3, Join(UInt(0x4286, 1), UInt(0x42F7, 1), UInt(0x42F2, 4), UInt(0x42F3, 8), Text(0x4282, "matroska"), UInt(0x4287, 4), UInt(0x4285, 2))), Id(0x18538067), new byte[] { 1, 255, 255, 255, 255, 255, 255, 255 });
            output.Write(header, 0, header.Length);
            var info = Element(0x1549A966, Join(UInt(0x2AD7B1, 1000), Text(0x4D80, "F10 PCM"), Text(0x5741, "F10 PCM")));
            byte[] frequency = BitConverter.GetBytes((double)rate); if (BitConverter.IsLittleEndian) Array.Reverse(frequency);
            var track = Element(0x1654AE6B, Element(0xAE, Join(UInt(0xD7, 1), UInt(0x73C5, 1), UInt(0x83, 2), UInt(0x9C, 0), Text(0x86, format == "f32le" ? "A_PCM/FLOAT/IEEE" : "A_PCM/INT/LIT"), Element(0xE1, Join(Element(0xB5, frequency), UInt(0x9F, channels), UInt(0x6264, bits))))));
            output.Write(info, 0, info.Length); output.Write(track, 0, track.Length);
        }
        internal void Write(byte[] data, int offset, int count, long samplePosition)
        {
            if (nextSample >= 0 && samplePosition != nextSample) throw new IOException("PCM timeline must be contiguous; fill gaps with silence before transport.");
            WriteAcquired(data, offset, count, samplePosition);
        }
        // Complete native packets may have slightly different PTS spacing from
        // their nominal duration. Preserve their bytes; let the resampler follow
        // acquisition time rather than manufacturing per-packet silence here.
        internal void WriteAcquired(byte[] data, int offset, int count, long samplePosition)
        {
            if (data == null || samplePosition < 0 || count <= 0 || count % blockAlign != 0 || offset < 0 || offset > data.Length - count) throw new ArgumentException("Invalid PCM block.");
            if (lastPacketStart >= 0 && samplePosition <= lastPacketStart) throw new IOException("PCM acquisition timestamps must advance.");
            long microseconds = checked(samplePosition * 1000000) / sampleRate;
            byte[] time = UInt(0xE7, microseconds), block = Join(Id(0xA3), Size(count + 4), new byte[] { 0x81, 0, 0, 0x80 });
            byte[] header = Join(Id(0x1F43B675), Size(time.Length + block.Length + count), time, block);
            output.Write(header, 0, header.Length); output.Write(data, offset, count);
            nextSample = samplePosition + count / blockAlign;
            lastPacketStart = samplePosition;
        }
        static byte[] Id(uint value) { int length = value > 0xFFFFFF ? 4 : value > 0xFFFF ? 3 : value > 0xFF ? 2 : 1; var b = new byte[length]; for (int i = length - 1; i >= 0; i--) { b[i] = (byte)value; value >>= 8; } return b; }
        static byte[] Size(long value) { int length = 1; while (value >= (1L << (7 * length)) - 1) length++; var b = new byte[length]; long encoded = value | (1L << (7 * length)); for (int i = length - 1; i >= 0; i--) { b[i] = (byte)encoded; encoded >>= 8; } return b; }
        static byte[] Element(uint id, byte[] data) { return Join(Id(id), Size(data.Length), data); }
        static byte[] UInt(uint id, long value) { int length = 1; for (long n = value; (n >>= 8) > 0;) length++; var b = new byte[length]; for (int i = length - 1; i >= 0; i--) { b[i] = (byte)value; value >>= 8; } return Element(id, b); }
        static byte[] Text(uint id, string text) { return Element(id, Encoding.UTF8.GetBytes(text)); }
        static byte[] Join(params byte[][] values) { using (var m = new MemoryStream()) { foreach (var b in values) m.Write(b, 0, b.Length); return m.ToArray(); } }
    }
}
