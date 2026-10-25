// THROWAWAY generated-fixture transport reader; no production integration.
using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

internal record Pes(int row, int pid, int offset, long pts, int? dts_offset = null, long? dts = null);
internal record Pcr(int row, long @base);
internal record Chunk(string name, int bytes, long origin, Pes[] pes, Pcr[] pcr);
internal record Request(string target, string kind, int first, int last, long handle, Dictionary<string, Chunk> boundaries);

internal static class Program
{
    private const long Mask = (1L << 33) - 1;
    private const long Epoch = 126000;

    private static void PutPts(Span<byte> bytes, long value)
    {
        value &= Mask;
        bytes[0] = (byte)((long)(bytes[0] & 0xf0) | ((value >> 29) & 14) | 1);
        bytes[1] = (byte)(value >> 22);
        bytes[2] = (byte)(((value >> 14) & 254) | 1);
        bytes[3] = (byte)(value >> 7);
        bytes[4] = (byte)(((value << 1) & 254) | 1);
    }

    private static void Rewrite(Span<byte> data, Chunk chunk, long shift)
    {
        foreach (Pes pes in chunk.pes)
        {
            PutPts(data.Slice(pes.row * 188 + pes.offset, 5), pes.pts + shift);
            if (pes.dts_offset is int offset && pes.dts is long dts)
                PutPts(data.Slice(pes.row * 188 + offset, 5), dts + shift);
        }
        foreach (Pcr pcr in chunk.pcr)
        {
            Span<byte> dst = data.Slice(pcr.row * 188 + 6, 5);
            long value = (pcr.@base + shift) & Mask;
            dst[0] = (byte)(value >> 25);
            dst[1] = (byte)(value >> 17);
            dst[2] = (byte)(value >> 9);
            dst[3] = (byte)(value >> 1);
            dst[4] = (byte)((long)(dst[4] & 127) | ((value & 1) << 7));
        }
    }

    // mode: 0 all packets; 1 tables/control; 2 video; 3 audio.
    private static int Emit(Span<byte> data, Span<byte> scratch, int mode, int[] counters, Stream output)
    {
        int count = 0;
        for (int off = 0; off < data.Length; off += 188)
        {
            Span<byte> packet = data.Slice(off, 188);
            int pid = ((packet[1] & 31) << 8) | packet[2];
            bool selected = mode switch
            {
                0 => true, 1 => pid != 256 && pid != 257,
                2 => pid == 256, 3 => pid == 257, _ => false
            };
            if (!selected) continue;
            bool payload = (packet[3] & 0x10) != 0;
            int cc = payload ? counters[pid]++ : counters[pid] - 1;
            packet[3] = (byte)((packet[3] & 0xf0) | (cc & 15));
            if (mode != 0) packet.CopyTo(scratch.Slice(count, 188));
            count += 188;
        }
        output.Write(mode == 0 ? data : scratch[..count]);
        return count;
    }

    private static void Read(string path, Chunk chunk, Span<byte> destination)
    {
        using FileStream input = File.OpenRead(path);
        if (input.Length != chunk.bytes || chunk.bytes % 188 != 0)
            throw new InvalidDataException("Pinned fixture chunk size changed");
        input.ReadExactly(destination[..chunk.bytes]);
    }

    private static object Serve(Request request, string allowedRoot, Dictionary<string, Dictionary<string, Chunk>> index)
    {
        using SafeFileHandle handle = new((IntPtr)request.handle, ownsHandle: true);
        using FileStream output = new(handle, FileAccess.Write, 1024 * 1024, isAsync: false);
        string target = Path.GetFullPath(request.target);
        if (!target.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || request.first < 0 || request.last < request.first || request.last - request.first >= 108000)
            throw new InvalidDataException("Invalid fixture request");
        Dictionary<string, Chunk> chunks = index[request.kind];
        int capacity = Math.Max(chunks.Values.Max(x => x.bytes), request.boundaries.Values.Select(x => x.bytes).DefaultIfEmpty().Max());
        byte[] main = ArrayPool<byte>.Shared.Rent(capacity);
        byte[] boundary = ArrayPool<byte>.Shared.Rent(capacity);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(capacity);
        int[] counters = new int[8192];
        long sent = 0;
        Stopwatch timer = Stopwatch.StartNew();
        TimeSpan before = Process.GetCurrentProcess().TotalProcessorTime;
        try
        {
            int low = request.first / 60, high = (request.last + 60) / 60 + 1;
            for (int second = low; second < high; second++)
            {
                string name = $"chunk-{second:D6}.ts";
                Chunk chunk = chunks[name];
                Span<byte> data = main.AsSpan(0, chunk.bytes);
                Read(Path.Combine(target, name), chunk, data);
                Rewrite(data, chunk, Epoch + second * 90000L - request.first * 1500L - chunk.origin);
                int left = Math.Max(request.first, second * 60), right = Math.Min(request.last + 1, (second + 1) * 60);
                if (left == second * 60 && right == (second + 1) * 60)
                    sent += Emit(data, scratch, 0, counters, output);
                else
                {
                    sent += Emit(data, scratch, 1, counters, output);
                    if (left < right)
                    {
                        string boundaryName = $"boundary-{second:D6}.ts";
                        Chunk info = request.boundaries[boundaryName];
                        Span<byte> video = boundary.AsSpan(0, info.bytes);
                        Read(Path.Combine(target, boundaryName), info, video);
                        Rewrite(video, info, Epoch + (left - request.first) * 1500L - info.origin);
                        sent += Emit(video, scratch, 2, counters, output);
                    }
                    sent += Emit(data, scratch, 3, counters, output);
                }
            }
            output.Flush();
            return new { status = "done", bytes_served = sent,
                seconds_including_pipe_backpressure = timer.Elapsed.TotalSeconds,
                reader_process_cpu_seconds = (Process.GetCurrentProcess().TotalProcessorTime - before).TotalSeconds };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(main);
            ArrayPool<byte>.Shared.Return(boundary);
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public static int Main(string[] args)
    {
        try
        {
            Stopwatch startup = Stopwatch.StartNew();
            var index = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Chunk>>>(File.ReadAllText(args[0]))!;
            string root = Path.GetFullPath(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "ready", pid = Environment.ProcessId, startup_seconds = startup.Elapsed.TotalSeconds }));
            for (string? line; (line = Console.ReadLine()) != null;)
            {
                if (line == "quit") return 0;
                Request request = JsonSerializer.Deserialize<Request>(line)!;
                Console.WriteLine(JsonSerializer.Serialize(Serve(request, root, index)));
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
