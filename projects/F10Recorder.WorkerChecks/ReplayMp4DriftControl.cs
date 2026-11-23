using System.Buffers.Binary;
using System.Text;

namespace F10Recorder;

// Controlled mutation of our reserved-header MP4 layout only. Mdat, co64,
// configurations, audio and exact endpoints remain in place. Not a media writer.
internal static class ReplayMp4DriftControl
{
    readonly record struct Box(string Kind, int Start, int Size)
    {
        internal int Data => Start + 8;
        internal int End => checked(Start + Size);
    }
    internal static object Mutate(string path, Action check)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        long eof = file.Length;
        using var prefix = new MemoryStream();
        byte[] header = new byte[8];
        while (true)
        {
            check(); file.ReadExactly(header);
            string kind = Encoding.ASCII.GetString(header, 4, 4);
            if (kind == "mdat") break;
            uint bytes = BinaryPrimitives.ReadUInt32BigEndian(header);
            Need(bytes is >= 8 and <= 4_194_304 && prefix.Length + bytes <= 4_194_304, "Bounded ordinary MP4 prefix required.");
            prefix.Write(header);
            byte[] body = new byte[checked((int)bytes - 8)]; file.ReadExactly(body); prefix.Write(body);
        }
        byte[] before = prefix.ToArray(); int mdatOffset = before.Length;
        var roots = Children(before, 0, before.Length);
        Box movie = One(roots, "moov"), free = One(roots, "free");
        Need(movie.End == free.Start && free.End == mdatOffset && free.Size >= 24, "Contiguous reserved moov/free prefix required.");
        Box track = Children(before, movie.Data, movie.End).Where(b => b.Kind == "trak").Single(b => {
            Box media = One(Children(before, b.Data, b.End), "mdia");
            Box handler = One(Children(before, media.Data, media.End), "hdlr");
            return handler.Size >= 20 && Encoding.ASCII.GetString(before, handler.Data + 8, 4) == "vide";
        });
        Box mdia = One(Children(before, track.Data, track.End), "mdia");
        Box minf = One(Children(before, mdia.Data, mdia.End), "minf");
        Box stbl = One(Children(before, minf.Data, minf.End), "stbl");
        Box stts = One(Children(before, stbl.Data, stbl.End), "stts");
        uint count = Read(before, stts.Data + 8), duration = Read(before, stts.Data + 12);
        Need(stts.Size == 24 && Read(before, stts.Data) == 0 && Read(before, stts.Data + 4) == 1
            && count == 18000 && duration == 800, "Expected one18000x800 video timing run.");
        byte[] after = before.ToArray();
        // Insert16bytes in stts; all later metadata shifts inside the existing
        // free reservation. Physical media offsets and file EOF do not move.
        Array.Copy(before, stts.End, after, stts.End + 16, free.Start - stts.End);
        Put(after, stts.Start, 40); Put(after, stts.Data + 4, 3);
        Put(after, stts.Data + 8, 2); Put(after, stts.Data + 12, 801);
        Put(after, stts.Data + 16, 2); Put(after, stts.Data + 20, 799);
        Put(after, stts.Data + 24, count - 4); Put(after, stts.Data + 28, duration);
        foreach (var parent in new[] { movie, track, mdia, minf, stbl }) Put(after, parent.Start, checked((uint)(parent.Size + 16)));
        Put(after, free.Start + 16, checked((uint)(free.Size - 16)));
        Encoding.ASCII.GetBytes("free").CopyTo(after, free.Start + 20);
        long originalTicks = count * (long)duration;
        long changedTicks = 2L * 801 + 2L * 799 + (count - 4L) * 800;
        Need(originalTicks == changedTicks && after.Length == mdatOffset, "Timing mutation changed total ticks or media offset.");
        file.Position = 0;
        for (int at = 0; at < after.Length; at += 65536)
        { check(); file.Write(after, at, Math.Min(65536, after.Length - at)); }
        file.Flush(); Need(file.Length == eof, "Metadata control changed file EOF.");
        check();
        return new { videoSamples = count, originalRuns = new[] { new { count, duration } },
            changedRuns = new[] { new { count = 2u, duration = 801u }, new { count = 2u, duration = 799u }, new { count = count - 4, duration } },
            originalTicks, changedTicks, firstDts = 0, originalLastDts = (count - 1L) * duration,
            changedLastDts = changedTicks - duration, maximumCumulativeDriftTicks = 2, timescale = 48000,
            metadataGrowth = 16, freeConsumed = 16, mdatOffset, eof, payloadWritten = false };
    }
    static uint Read(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    static void Put(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
    static List<Box> Children(byte[] data, int start, int end)
    {
        var boxes = new List<Box>();
        for (int at = start; at < end;)
        {
            Need(end - at >= 8 && boxes.Count < 128, "Bounded MP4 child table required.");
            uint size = Read(data, at); Need(size >= 8 && size <= end - at, "MP4 child extent.");
            var box = new Box(Encoding.ASCII.GetString(data, at + 4, 4), at, checked((int)size));
            boxes.Add(box); at = box.End;
        }
        return boxes;
    }
    static Box One(List<Box> boxes, string kind) => boxes.Single(b => b.Kind == kind);
    static void Need(bool value, string message) { if (!value) throw new IOException(message); }
}
