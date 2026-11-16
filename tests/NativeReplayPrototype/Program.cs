// Experimental closed-output decoder. No capture, writer or product integration.
using System.Collections.Concurrent;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FFmpeg.AutoGen;
using Microsoft.Win32.SafeHandles;

unsafe class Program
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    static readonly ConcurrentQueue<string> Errors = new();
    static av_log_set_callback_callback? logCallback;
    static int errorCount;
    static void Require([DoesNotReturnIf(false)] bool ok, string why) { if (!ok) throw new IOException(why); }
    static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    static void Check(int result, string operation) { if (result < 0) throw new IOException(operation + ":" + result); }
    static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    static int Main(string[] args)
    {
        if (args.Length != 5 || args[0] != "--decode" || !int.TryParse(args[3], out int mode) || mode is not (0 or 1 or 4 or 8 or 16 or 32 or 48)) return 2;
        bool gpu = mode == 0; int workers = gpu ? 1 : mode;
        var total = Stopwatch.StartNew();
        string planFile = Path.GetFullPath(args[1]), report = Path.GetFullPath(args[4]);
        Require(new FileInfo(planFile).Length < 65536 && Hash(planFile) == args[2], "Reviewed plan changed.");
        using var doc = JsonDocument.Parse(File.ReadAllText(planFile)); var p = doc.RootElement;
        int decoderThreads = p.TryGetProperty("decoderThreads", out var threadValue) ? threadValue.GetInt32() : 1;
        string? openH264 = p.TryGetProperty("openH264Path", out var openValue) ? openValue.GetString() : null;
        bool coalesceIndependentPictures = p.TryGetProperty("coalesceIndependentPictures", out var coalesceValue) && coalesceValue.GetBoolean();
        Require(!coalesceIndependentPictures || !gpu && workers == 32 && openH264 == null, "Coalesced all-IDR diagnostic requires the frozen software32-context mode.");
        Require(openH264 == null || !gpu && decoderThreads == 1 && workers is 16 or 32, "Declared OpenH264 mode required.");
        Require(openH264 == null || p.GetProperty("runtimeFiles").TryGetProperty(Path.GetFullPath(openH264), out _), "OpenH264 must be one of the pinned runtime files.");
        Require(decoderThreads is 1 or 4 or 8 && decoderThreads * workers <= 64 && (!gpu || decoderThreads == 1), "Bounded decoder thread configuration required.");
        Require(p.GetProperty("kind").GetString() == "native-full-decode-pilot-v1", "Wrong native pilot plan.");
        string root = Path.GetFullPath(p.GetProperty("root").GetString()!);
        Require(report == Path.Combine(root, "native-" + mode + ".json") && !File.Exists(report), "Fresh owned report required.");
        var pins = new List<IDisposable>();
        try
        {
            foreach (var f in p.GetProperty("runtimeFiles").EnumerateObject())
            {
                var pin = new FileStream(f.Name, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(pin);
                Require(Convert.ToHexString(SHA256.HashData(pin)) == f.Value.GetString(), "Pinned input changed:" + f.Name);
            }
            pins.Add(new FileStream(p.GetProperty("movie").GetString()!, FileMode.Open, FileAccess.Read, FileShare.Read));
            string libraries = Path.GetFullPath(p.GetProperty("libraryRoot").GetString()!);
            for (string? directory = libraries; directory != null; directory = Path.GetDirectoryName(directory))
            {
                var pin = CreateFile(directory, 0x80, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                Require(!pin.IsInvalid, "Directory pin failed:" + directory); pins.Add(pin);
            }
            if (openH264 != null)
                for (string? directory = Path.GetDirectoryName(openH264); directory != null; directory = Path.GetDirectoryName(directory))
                {
                    var pin = CreateFile(directory, 0x80, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                    Require(!pin.IsInvalid, "OpenH264 directory pin failed."); pins.Add(pin);
                }
            Require(SetDefaultDllDirectories(0x800 | 0x400), "Safe DLL search configuration failed.");
            nint cookie = AddDllDirectory(libraries); Require(cookie != 0, "Private DLL directory failed.");
            double authenticatedMs = total.Elapsed.TotalMilliseconds;
            object result = Decode(p.GetProperty("movie").GetString()!, libraries, workers, total, gpu, decoderThreads, openH264, coalesceIndependentPictures);
            Require(errorCount == 0, "Native decoder logged errors.");
            Write(report, new { result = "native-full-decode-complete", workers, authenticatedMs,
                secondsIncludingAuthenticationLoadAndDecode = total.Elapsed.TotalSeconds, details = result,
                errors = Errors.ToArray(), publication = false, capture = false, gpu });
            return 0;
        }
        catch (Exception e) { Write(report, new { result = "failed-native-full-decode", workers, seconds = total.Elapsed.TotalSeconds, error = e.ToString(), errors = Errors.ToArray() }); Console.Error.WriteLine(e); return 1; }
        finally { foreach (var pin in pins.AsEnumerable().Reverse()) pin.Dispose(); }
    }
    sealed class Gop : IDisposable
    {
        internal int Start;
        internal long Bytes;
        internal readonly List<nint> Packets = [];
        public void Dispose() { foreach (nint value in Packets) { AVPacket* p = (AVPacket*)value; ffmpeg.av_packet_free(&p); } Packets.Clear(); }
    }
    sealed record GopResult(int Start, int Frames, long FirstPts, long LastPts, double WorkMs, double FlushMs = 0, double DrainMs = 0);
    interface IVideoDecoder : IDisposable { GopResult Run(Gop gop); }
    sealed class OpenDecoder : IVideoDecoder
    {
        // Independent x64 Windows bindings to Cisco v2.6.0 public C ABI.
        [StructLayout(LayoutKind.Explicit, Size = 32)] struct Parameters
        {
            [FieldOffset(0)] public nint DebugFile;
            [FieldOffset(8)] public uint CpuLoad;
            [FieldOffset(12)] public byte TargetLayer;
            [FieldOffset(16)] public int Concealment;
            [FieldOffset(20)] public byte ParseOnly;
            [FieldOffset(24)] public uint PropertySize;
            [FieldOffset(28)] public int BitstreamType;
        }
        [StructLayout(LayoutKind.Explicit, Size = 72)] struct BufferInfo
        {
            [FieldOffset(0)] public int Status;
            [FieldOffset(8)] public ulong InputTimestamp;
            [FieldOffset(16)] public ulong OutputTimestamp;
            [FieldOffset(24)] public int Width;
            [FieldOffset(28)] public int Height;
            [FieldOffset(32)] public int Format;
            [FieldOffset(36)] public int StrideY;
            [FieldOffset(40)] public int StrideUv;
            [FieldOffset(48)] public nint Y;
            [FieldOffset(56)] public nint U;
            [FieldOffset(64)] public nint V;
        }
        static nint module;
        static delegate* unmanaged[Cdecl]<nint*, int> create;
        static delegate* unmanaged[Cdecl]<nint, void> destroy;
        readonly long origin;
        readonly int nalLength;
        readonly byte[] header;
        nint decoder;
        nint* table;
        byte[] buffer = [];
        internal static void Load(string path)
        {
            Require(IntPtr.Size == 8 && module == 0, "One x64 codec load required."); module = NativeLibrary.Load(Path.GetFullPath(path));
            create = (delegate* unmanaged[Cdecl]<nint*, int>)NativeLibrary.GetExport(module, "WelsCreateDecoder");
            destroy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(module, "WelsDestroyDecoder");
            uint* version = stackalloc uint[4];
            ((delegate* unmanaged[Cdecl]<uint*, void>)NativeLibrary.GetExport(module, "WelsGetCodecVersionEx"))(version);
            Require(version[0] == 2 && version[1] == 6 && version[2] == 0, "Unexpected OpenH264 codec version.");
        }
        int Option(int index, int option, int* value) => ((delegate* unmanaged[Cdecl]<nint, int, int*, int>)table[index])(decoder, option, value);
        internal OpenDecoder(long origin, int nalLength, List<byte[]> parameterSets)
        {
            this.origin = origin; this.nalLength = nalLength;
            header = parameterSets.SelectMany(x => new byte[] { 0, 0, 0, 1 }.Concat(x)).ToArray();
            nint created = 0; Require(create(&created) == 0 && created != 0, "OpenH264 create failed."); decoder = created; table = *(nint**)decoder;
            try
            {
                int threads = 1; Require(Option(8, 19, &threads) == 0, "OpenH264 thread setting failed before initialization.");
                var parameters = new Parameters { TargetLayer = 255, Concealment = 0, ParseOnly = 0, PropertySize = 8, BitstreamType = 0 };
                Require(((delegate* unmanaged[Cdecl]<nint, Parameters*, int>)table[0])(decoder, &parameters) == 0, "OpenH264 initialize failed.");
                int concealment = -1; threads = -1;
                Require(Option(9, 8, &concealment) == 0 && concealment == 0 && Option(9, 19, &threads) == 0 && threads == 1, "OpenH264 strict/software thread configuration not confirmed.");
            }
            catch { Dispose(); throw; }
        }
        public GopResult Run(Gop gop)
        {
            var work = Stopwatch.StartNew(); int count = 0; long first = 0, last = 0; int eos = 0;
            nint* planes = stackalloc nint[3];
            Require(Option(8, 1, &eos) == 0, "OpenH264 EOS reset failed.");
            void Output(BufferInfo info, nint* planes)
            {
                if (info.Status == 0) return;
                Require(info.Status == 1 && info.Width == 1920 && info.Height == 1080 && info.Format == 23
                    && info.StrideY >= 1920 && info.StrideUv >= 960 && planes[0] != 0 && planes[1] != 0 && planes[2] != 0
                    && info.Y == planes[0] && info.U == planes[1] && info.V == planes[2], "Incomplete OpenH264 reconstructed I420 frame.");
                long pts = checked((long)info.OutputTimestamp);
                Require(count < gop.Packets.Count && Math.Abs(pts - (origin + (gop.Start + count) * 800L)) <= 1, "OpenH264 global frame clock/count failed.");
                if (count == 0) first = pts; last = pts; count++;
            }
            foreach (nint value in gop.Packets)
            {
                AVPacket* packet = (AVPacket*)value; Require(packet->pts >= 0, "Bounded nonnegative input timestamp required.");
                int capacity = checked(packet->size * 4 + header.Length + 64); Require(capacity <= 64_100_000, "AnnexB conversion cap.");
                if (buffer.Length < capacity) { if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer); buffer = ArrayPool<byte>.Shared.Rent(capacity); }
                int written = 0; if (value == gop.Packets[0]) { header.CopyTo(buffer, 0); written = header.Length; }
                var source = new ReadOnlySpan<byte>(packet->data, packet->size); int off = 0;
                while (off < source.Length)
                {
                    Require(off + nalLength <= source.Length, "AVCC prefix truncated."); int size = 0;
                    for (int i = 0; i < nalLength; i++) size = checked(size * 256 + source[off++]);
                    Require(size > 0 && off + size <= source.Length, "AVCC payload truncated.");
                    buffer[written++] = 0; buffer[written++] = 0; buffer[written++] = 0; buffer[written++] = 1;
                    source.Slice(off, size).CopyTo(buffer.AsSpan(written)); off += size; written += size;
                }
                var info = new BufferInfo { InputTimestamp = (ulong)packet->pts }; planes[0] = planes[1] = planes[2] = 0;
                fixed (byte* bytes = buffer)
                {
                    int state = ((delegate* unmanaged[Cdecl]<nint, byte*, int, nint*, BufferInfo*, int>)table[3])(decoder, bytes, written, planes, &info);
                    Require(state is 0 or 1, "OpenH264 decoding error/state:" + state);
                }
                Output(info, planes);
            }
            eos = 1; Require(Option(8, 1, &eos) == 0, "OpenH264 EOS failed.");
            for (int drained = 0; drained <= 120; drained++)
            {
                int remaining = -1; Require(Option(9, 18, &remaining) == 0 && remaining is >= 0 and <= 120, "OpenH264 buffered-frame query failed.");
                if (remaining == 0) break;
                var info = new BufferInfo(); planes[0] = planes[1] = planes[2] = 0;
                Require(((delegate* unmanaged[Cdecl]<nint, nint*, BufferInfo*, int>)table[5])(decoder, planes, &info) == 0 && info.Status == 1, "OpenH264 complete drain failed."); Output(info, planes);
            }
            Require(count == gop.Packets.Count, "OpenH264 full GOP count mismatch.");
            return new(gop.Start, count, first, last, work.Elapsed.TotalMilliseconds);
        }
        public void Dispose()
        {
            if (decoder != 0) { var value = decoder; decoder = 0; ((delegate* unmanaged[Cdecl]<nint, int>)table[1])(value); destroy(value); }
            if (buffer.Length > 0) { ArrayPool<byte>.Shared.Return(buffer); buffer = []; }
        }
    }
    sealed class VideoDecoder : IVideoDecoder
    {
        AVCodecContext* context;
        AVFrame* frame;
        readonly long origin;
        readonly bool gpu;
        nint cudaContext;
        internal VideoDecoder(AVCodecParameters* parameters, AVRational timebase, long origin, bool gpu, int threads)
        {
            this.origin = origin; this.gpu = gpu;
            AVCodec* codec = ffmpeg.avcodec_find_decoder_by_name("h264"); Require(codec != null, "Software H264 decoder absent.");
            context = ffmpeg.avcodec_alloc_context3(codec); Require(context != null, "Codec allocation failed.");
            Check(ffmpeg.avcodec_parameters_to_context(context, parameters), "video parameters");
            context->pkt_timebase = timebase; context->thread_count = threads;
            if (threads > 1) context->thread_type = ffmpeg.FF_THREAD_SLICE;
            context->err_recognition = ffmpeg.AV_EF_EXPLODE;
            context->skip_frame = context->skip_idct = context->skip_loop_filter = AVDiscard.AVDISCARD_DEFAULT;
            if (gpu)
            {
                Check(ffmpeg.av_hwdevice_ctx_create(&context->hw_device_ctx, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, null, null, 0), "CUDA device creation");
                var device = (AVHWDeviceContext*)context->hw_device_ctx->data;
                // Public n9.0.2 AVCUDADeviceContext begins with CUcontext.
                cudaContext = *(nint*)device->hwctx; Require(cudaContext != 0, "CUDA context absent.");
                context->extra_hw_frames = 0;
            }
            Check(ffmpeg.avcodec_open2(context, codec, null), "video decoder open");
            Require(threads == 1 || context->active_thread_type == ffmpeg.FF_THREAD_SLICE && context->thread_count == threads, "Slice threading did not activate exactly as requested.");
            frame = ffmpeg.av_frame_alloc(); Require(frame != null, "Frame allocation failed.");
        }
        public GopResult Run(Gop gop)
        {
            var work = Stopwatch.StartNew();
            long flushAt = Stopwatch.GetTimestamp(); ffmpeg.avcodec_flush_buffers(context);
            double flushMs = Stopwatch.GetElapsedTime(flushAt).TotalMilliseconds;
            int count = 0; long first = 0, last = 0;
            int Receive()
            {
                int result = ffmpeg.avcodec_receive_frame(context, frame);
                if (result < 0) return result;
                Require((frame->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) == 0 && frame->decode_error_flags == 0
                    && frame->width == 1920 && frame->height == 1080 && frame->data[0] != null
                    && (gpu ? frame->hw_frames_ctx != null && frame->format == (int)AVPixelFormat.AV_PIX_FMT_CUDA
                        : frame->hw_frames_ctx == null && frame->format == (int)AVPixelFormat.AV_PIX_FMT_YUV420P && frame->data[1] != null && frame->data[2] != null),
                    "Corrupt, incomplete or unexpected decoded video.");
                long pts = frame->best_effort_timestamp;
                Require(pts != ffmpeg.AV_NOPTS_VALUE && Math.Abs(pts - (origin + (gop.Start + count) * 800L)) <= 1, "Global decoded-frame timing failed.");
                if (count == 0) first = pts; last = pts; count++;
                Require(count <= 120 && errorCount == 0, "GOP frame/error bound."); ffmpeg.av_frame_unref(frame); return 0;
            }
            foreach (nint value in gop.Packets)
            {
                int sent;
                while ((sent = ffmpeg.avcodec_send_packet(context, (AVPacket*)value)) == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    Check(Receive(), "video receive while send EAGAIN");
                Check(sent, "video packet send");
                int received; while ((received = Receive()) >= 0) { }
                Require(received == ffmpeg.AVERROR(ffmpeg.EAGAIN), "Unexpected video receive result:" + received);
            }
            long drainAt = Stopwatch.GetTimestamp();
            Check(ffmpeg.avcodec_send_packet(context, null), "video drain start");
            int drained; while ((drained = Receive()) >= 0) { }
            Require(drained == ffmpeg.AVERROR_EOF && count == gop.Packets.Count, "Incomplete GOP drain or frame/packet count.");
            double drainMs = Stopwatch.GetElapsedTime(drainAt).TotalMilliseconds;
            if (gpu) Synchronize();
            return new(gop.Start, count, first, last, work.Elapsed.TotalMilliseconds, flushMs, drainMs);
        }
        void Synchronize()
        {
            Require(cuCtxPushCurrent(cudaContext) == 0, "CUDA context push failed.");
            try { Require(cuCtxSynchronize() == 0, "CUDA completion failed."); }
            finally { Require(cuCtxPopCurrent(out _) == 0, "CUDA context pop failed."); }
        }
        public void Dispose()
        {
            AVFrame* oldFrame = frame; frame = null; if (oldFrame != null) ffmpeg.av_frame_free(&oldFrame);
            AVCodecContext* oldContext = context; context = null;
            try { if (oldContext != null && gpu) Synchronize(); }
            finally { if (oldContext != null) ffmpeg.avcodec_free_context(&oldContext); }
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static object Decode(string movie, string libraries, int workers, Stopwatch total, bool gpu, int decoderThreads, string? openH264, bool coalesceIndependentPictures)
    {
        ffmpeg.RootPath = libraries;
        uint codecVersion = ffmpeg.avcodec_version(), formatVersion = ffmpeg.avformat_version(), utilVersion = ffmpeg.avutil_version();
        Require(codecVersion >> 16 == 63 && formatVersion >> 16 == 63 && utilVersion >> 16 == 61, "Unexpected native ABI.");
        logCallback = (_, level, format, values) =>
        {
            if (level > ffmpeg.AV_LOG_ERROR) return;
            byte* line = stackalloc byte[1024]; int prefix = 1;
            ffmpeg.av_log_format_line(_, level, format, values, line, 1024, &prefix);
            Interlocked.Increment(ref errorCount); if (Errors.Count < 16) Errors.Enqueue(Marshal.PtrToStringUTF8((nint)line) ?? "Native error.");
        };
        ffmpeg.av_log_set_callback(logCallback); ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            if (module.ModuleName.StartsWith("avcodec-", StringComparison.OrdinalIgnoreCase) || module.ModuleName.StartsWith("avformat-", StringComparison.OrdinalIgnoreCase)
                || module.ModuleName.StartsWith("avutil-", StringComparison.OrdinalIgnoreCase))
                Require(string.Equals(Path.GetDirectoryName(module.FileName), libraries, StringComparison.OrdinalIgnoreCase), "Foreign native codec module loaded.");
        AVFormatContext* input = null; AVCodecContext* audio = null; AVFrame* audioFrame = null; AVPacket* packet = null; AVDictionary* options = null;
        var queues = Enumerable.Range(0, workers).Select(_ => new BlockingCollection<Gop>(2)).ToArray();
        var decoders = new List<IVideoDecoder>(); var tasks = new List<Task>();
        var decoded = new ConcurrentBag<GopResult>(); using var stop = new CancellationTokenSource(); Exception? workerFailure = null;
        Gop? pending = null; int videoPackets = 0, audioPackets = 0, gops = 0, actualIdrs = 0; long audioFrames = 0, audioSamples = 0, origin = 0, queuedPeakBytes = 0, queueBytes = 0;
        long demuxTicks = 0, inspectTicks = 0, audioTicks = 0, queueTicks = 0;
        int minimumVclNals = int.MaxValue, maximumVclNals = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            Check(ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0), "protocol allowlist");
            Check(ffmpeg.avformat_open_input(&input, movie, ffmpeg.av_find_input_format("mov"), &options), "closed MP4 open");
            Require(input->nb_streams == 2, "Exactly video/system-audio tracks required.");
            int vi = -1, ai = -1;
            for (int i = 0; i < input->nb_streams; i++)
            {
                var stream = input->streams[i];
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO) { Require(vi == -1, "Duplicate video."); vi = i; }
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO) { Require(ai == -1, "Duplicate audio."); ai = i; }
            }
            Require(vi >= 0 && ai >= 0, "Required tracks absent.");
            AVStream* video = input->streams[vi]; AVStream* sound = input->streams[ai];
            Require(video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_H264 && video->codecpar->width == 1920 && video->codecpar->height == 1080
                && video->time_base.num == 1 && video->time_base.den == 48000 && sound->codecpar->codec_id == AVCodecID.AV_CODEC_ID_AAC
                && sound->codecpar->sample_rate == 48000 && sound->codecpar->ch_layout.nb_channels == 2, "Reference stream schema changed.");
            byte[] avcc = new ReadOnlySpan<byte>(video->codecpar->extradata, video->codecpar->extradata_size).ToArray();
            Require(avcc.Length is >= 7 and <= 65536 && avcc[0] == 1, "AVCC configuration required."); int nalLength = (avcc[4] & 3) + 1;
            Require(nalLength is 1 or 2 or 4, "Unsupported AVC length size.");
            var parameters = new List<byte[]>(); int at = 6;
            for (int i = 0; i < (avcc[5] & 31); i++) { Require(at + 2 <= avcc.Length, "SPS length absent."); int size = avcc[at] * 256 + avcc[at + 1]; at += 2; Require(size > 0 && at + size <= avcc.Length, "SPS truncated."); parameters.Add(avcc.AsSpan(at, size).ToArray()); at += size; }
            Require(at < avcc.Length, "PPS count absent."); int pps = avcc[at++];
            for (int i = 0; i < pps; i++) { Require(at + 2 <= avcc.Length, "PPS length absent."); int size = avcc[at] * 256 + avcc[at + 1]; at += 2; Require(size > 0 && at + size <= avcc.Length, "PPS truncated."); parameters.Add(avcc.AsSpan(at, size).ToArray()); at += size; }
            if (openH264 != null) OpenDecoder.Load(openH264);
            bool HasIdr(AVPacket* p)
            {
                var bytes = new ReadOnlySpan<byte>(p->data, p->size); bool idr = false; int off = 0, vcl = 0;
                while (off < bytes.Length)
                {
                    Require(off + nalLength <= bytes.Length, "NAL length truncated."); int size = 0;
                    for (int k = 0; k < nalLength; k++) size = checked(size * 256 + bytes[off++]);
                    Require(size > 0 && off + size <= bytes.Length, "NAL payload truncated."); int type = bytes[off] & 31;
                    if (type is 7 or 8)
                    {
                        bool known = false; foreach (var parameter in parameters) if (parameter.AsSpan().SequenceEqual(bytes.Slice(off, size))) { known = true; break; }
                        Require(known, "Parameter-set change is unqualified.");
                    }
                    if (type == 5) idr = true;
                    if (type is >= 1 and <= 5) vcl++;
                    if ((p->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0 && type is >= 1 and <= 4) throw new IOException("Key packet contains non-IDR VCL.");
                    off += size;
                }
                Require(vcl > 0, "Video access unit has no coded slices.");
                minimumVclNals = Math.Min(minimumVclNals, vcl); maximumVclNals = Math.Max(maximumVclNals, vcl); return idr;
            }
            AVCodec* audioCodec = ffmpeg.avcodec_find_decoder_by_name("aac"); Require(audioCodec != null, "AAC decoder absent.");
            audio = ffmpeg.avcodec_alloc_context3(audioCodec); Require(audio != null, "Audio allocation.");
            Check(ffmpeg.avcodec_parameters_to_context(audio, sound->codecpar), "audio parameters"); audio->pkt_timebase = sound->time_base;
            audio->err_recognition = ffmpeg.AV_EF_EXPLODE; audio->thread_count = 1; Check(ffmpeg.avcodec_open2(audio, audioCodec, null), "AAC open");
            audioFrame = ffmpeg.av_frame_alloc(); packet = ffmpeg.av_packet_alloc(); Require(audioFrame != null && packet != null, "Packet/audio frame allocation.");
            int ReceiveAudio()
            {
                int result = ffmpeg.avcodec_receive_frame(audio, audioFrame); if (result < 0) return result;
                Require((audioFrame->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) == 0 && audioFrame->decode_error_flags == 0
                    && audioFrame->sample_rate == 48000 && audioFrame->ch_layout.nb_channels == 2 && audioFrame->nb_samples > 0, "Corrupt/incomplete audio decode.");
                audioFrames++; audioSamples += audioFrame->nb_samples; ffmpeg.av_frame_unref(audioFrame); return 0;
            }
            void Queue(Gop item)
            {
                long current = Interlocked.Add(ref queueBytes, item.Bytes); queuedPeakBytes = Math.Max(queuedPeakBytes, current);
                try
                {
                    Require(current <= workers * 3L * 16_000_000, "Queued packet byte cap.");
                    long queuedAt = Stopwatch.GetTimestamp();
                    queues[gops % workers].Add(item, stop.Token); gops++;
                    queueTicks += Stopwatch.GetTimestamp() - queuedAt;
                }
                catch { Interlocked.Add(ref queueBytes, -item.Bytes); item.Dispose(); throw; }
            }
            while (true)
            {
                Require(total.Elapsed.TotalSeconds < (gpu ? 40 : workers == 1 ? 115 : workers == 4 ? 55 : 40) && errorCount == 0 && workerFailure == null, "Native deadline/error.");
                long readAt = Stopwatch.GetTimestamp(); int read = ffmpeg.av_read_frame(input, packet);
                demuxTicks += Stopwatch.GetTimestamp() - readAt;
                if (read == ffmpeg.AVERROR_EOF) break; Check(read, "Full MP4 packet read");
                try
                {
                    if (packet->stream_index == vi)
                    {
                        Require(packet->size is > 0 and <= 16_000_000 && videoPackets < 18001, "Video packet bound.");
                        if (videoPackets == 0)
                        {
                            origin = packet->pts; Require(origin is >= -1 and <= 801, "Wrong video origin.");
                            for (int i = 0; i < workers; i++)
                            {
                                IVideoDecoder decoder = openH264 == null ? new VideoDecoder(video->codecpar, video->time_base, origin, gpu, decoderThreads) : new OpenDecoder(origin, nalLength, parameters);
                                decoders.Add(decoder); int index = i;
                                tasks.Add(Task.Factory.StartNew(() =>
                                {
                                    try { foreach (Gop item in queues[index].GetConsumingEnumerable(stop.Token))
                                        using (item) { Interlocked.Add(ref queueBytes, -item.Bytes); decoded.Add(decoder.Run(item)); } }
                                    catch (Exception e) { Interlocked.CompareExchange(ref workerFailure, e, null); stop.Cancel(); }
                                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                            }
                        }
                        Require(packet->pts == packet->dts && Math.Abs(packet->pts - (origin + videoPackets * 800L)) <= 1
                            && Math.Abs(packet->duration - 800) <= 1, "Global demux packet clock failed.");
                        long inspectedAt = Stopwatch.GetTimestamp();
                        bool key = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0; bool idr = HasIdr(packet);
                        inspectTicks += Stopwatch.GetTimestamp() - inspectedAt;
                        Require(key == idr, "Container key flag does not match an actual IDR.");
                        if (idr) actualIdrs++;
                        Require(!coalesceIndependentPictures || idr, "All-IDR coalescing rejects any dependent coded picture.");
                        // A coalesced group contains60 independently decodable pictures.
                        // Grouping changes scheduling only; every access unit is still
                        // inspected, fully decoded and checked against the global clock.
                        bool beginGroup = key && (!coalesceIndependentPictures || pending == null || pending.Packets.Count == 60);
                        if (beginGroup) { if (pending != null) { var completed = pending; pending = null; Queue(completed); } pending = new Gop { Start = videoPackets }; }
                        Require(pending != null, "Video does not begin with an independent IDR.");
                        AVPacket* clone = ffmpeg.av_packet_clone(packet); Require(clone != null, "Packet reference allocation.");
                        pending.Packets.Add((nint)clone); pending.Bytes += packet->size;
                        Require(pending.Packets.Count <= 120 && pending.Bytes <= 16_000_000, "GOP packet/byte cap."); videoPackets++;
                    }
                    else if (packet->stream_index == ai)
                    {
                        long audioAt = Stopwatch.GetTimestamp();
                        int sent; while ((sent = ffmpeg.avcodec_send_packet(audio, packet)) == ffmpeg.AVERROR(ffmpeg.EAGAIN)) Check(ReceiveAudio(), "Audio EAGAIN receive");
                        Check(sent, "AAC packet send"); int received; while ((received = ReceiveAudio()) >= 0) { }
                        Require(received == ffmpeg.AVERROR(ffmpeg.EAGAIN), "Unexpected AAC receive result."); audioPackets++;
                        audioTicks += Stopwatch.GetTimestamp() - audioAt;
                    }
                    else throw new IOException("Unexpected track.");
                }
                finally { ffmpeg.av_packet_unref(packet); }
            }
            if (pending != null) { var completed = pending; pending = null; Queue(completed); }
            foreach (var queue in queues) queue.CompleteAdding(); Task.WhenAll(tasks).GetAwaiter().GetResult();
            if (workerFailure != null) throw new IOException("Video decode failed.", workerFailure);
            Check(ffmpeg.avcodec_send_packet(audio, null), "AAC drain start"); int audioEnd; while ((audioEnd = ReceiveAudio()) >= 0) { }
            Require(audioEnd == ffmpeg.AVERROR_EOF && audioPackets > 0 && audioFrames > 0 && errorCount == 0, "Incomplete enabled audio drain.");
            var groups = decoded.OrderBy(x => x.Start).ToArray(); int next = 0;
            foreach (var group in groups) { Require(group.Start == next && group.FirstPts == origin + next * 800L
                && Math.Abs(group.LastPts - (origin + (next + group.Frames - 1) * 800L)) <= 1, "Decoded GOP seam/count mismatch."); next += group.Frames; }
            Require(videoPackets == 18000 && next == 18000 && Math.Abs(origin / 48000.0 + next / 60.0 - 300) <= 1.0 / 60 + .000022, "Full output count/endpoint mismatch.");
            Require(!coalesceIndependentPictures || actualIdrs == 18000 && gops == 300, "Expected18k actual IDRs in300 independent-picture decoder groups.");
            return new { videoPackets, decodedFrames = next, gops = actualIdrs, decoderGroups = gops, actualIdrs, coalesceIndependentPictures,
                decoderResetDrainGroups = gops, audioPackets, audioFrames, audioSamples, origin,
                nativeBodySeconds = watch.Elapsed.TotalSeconds, queuedPeakBytes, codecVersion, formatVersion, utilVersion,
                demuxMs = demuxTicks * 1000.0 / Stopwatch.Frequency, inspectionMs = inspectTicks * 1000.0 / Stopwatch.Frequency,
                audioMs = audioTicks * 1000.0 / Stopwatch.Frequency, queueWaitMs = queueTicks * 1000.0 / Stopwatch.Frequency,
                summedDecoderWorkMs = groups.Sum(x => x.WorkMs), maximumGopMs = groups.Max(x => x.WorkMs),
                summedFlushWallMs = openH264 == null ? groups.Sum(x => x.FlushMs) : (double?)null,
                summedDrainWallMs = openH264 == null ? groups.Sum(x => x.DrainMs) : (double?)null,
                hardwareFrames = gpu ? next : 0, deviceSynchronizations = gpu ? gops : 0,
                decoderThreads, minimumVclNals, maximumVclNals,
                backend = openH264 == null ? "FFmpeg" : "OpenH264-2.6.0-error-concealment-disabled",
                allGopsDrained = true, globalTiming = true };
        }
        finally
        {
            stop.Cancel(); foreach (var queue in queues) queue.CompleteAdding();
            try { Task.WhenAll(tasks).GetAwaiter().GetResult(); } catch { }
            pending?.Dispose(); foreach (var queue in queues) { while (queue.TryTake(out Gop? item)) item.Dispose(); queue.Dispose(); }
            foreach (var decoder in decoders) decoder.Dispose();
            if (packet != null) ffmpeg.av_packet_free(&packet);
            AVFrame* oldAudioFrame = audioFrame; audioFrame = null; if (oldAudioFrame != null) ffmpeg.av_frame_free(&oldAudioFrame);
            AVCodecContext* oldAudio = audio; audio = null; if (oldAudio != null) ffmpeg.avcodec_free_context(&oldAudio);
            if (input != null) ffmpeg.avformat_close_input(&input); ffmpeg.av_dict_free(&options);
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("nvcuda.dll", EntryPoint = "cuCtxPushCurrent_v2")] static extern int cuCtxPushCurrent(nint context);
    [DllImport("nvcuda.dll", EntryPoint = "cuCtxPopCurrent_v2")] static extern int cuCtxPopCurrent(out nint context);
    [DllImport("nvcuda.dll")] static extern int cuCtxSynchronize();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint AddDllDirectory(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
