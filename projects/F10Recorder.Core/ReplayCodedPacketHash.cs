using System.Buffers.Binary;
using System.Security.Cryptography;

namespace F10Recorder;

// Ordered, length-delimited NAL evidence. Each digest includes the NAL header
// and complete coded body, with AnnexB framing/trailing_zero_8bits excluded.
// This is byte provenance, not a proof of semantic decodability.
internal static class ReplayCodedPacketHash
{
    internal static void Append(IncrementalHash packet, long bytes, ReadOnlySpan<byte> digest)
    {
        if (bytes <= 0 || bytes > uint.MaxValue || digest.Length != 32) throw new IOException("Invalid coded NAL evidence.");
        Span<byte> framed = stackalloc byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)bytes); digest.CopyTo(framed[4..]);
        packet.AppendData(framed);
    }
}
