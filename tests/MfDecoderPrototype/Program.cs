using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

// Capability probe only. No input samples, output buffers, D3D manager or decoder
// processing calls exist in this program; it cannot establish a latency result.
unsafe class Program
{
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint Value;
        [FieldOffset(8)] public short Boolean;
    }
    static void Require(bool ok, string why) { if (!ok) throw new IOException(why); }
    static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        var clock = Stopwatch.StartNew(); Require(IntPtr.Size == 8, "x64 probe required.");
        using var planPin = new FileStream(args[0], FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(planPin.Length < 32768, "Bounded probe plan required."); byte[] planBytes = new byte[(int)planPin.Length]; planPin.ReadExactly(planBytes);
        Require(Convert.ToHexString(SHA256.HashData(planBytes)) == args[1], "Reviewed probe plan required.");
        using var doc = JsonDocument.Parse(planBytes); var p = doc.RootElement;
        Require(p.GetProperty("kind").GetString() == "mf-capability-only-v1", "Wrong capability plan.");
        string root = p.GetProperty("root").GetString()!; Require(!Directory.Exists(root) && DateTime.UtcNow - p.GetProperty("createdUtc").GetDateTime() < TimeSpan.FromHours(4), "Fresh current probe required.");
        Directory.CreateDirectory(root); File.Copy(args[0], Path.Combine(root, "admitted-plan.json"));
        var pins = new List<FileStream>(); nint module = 0, factory = 0, transform = 0, codec = 0, mediaType = 0;
        bool com = false, mf = false; object? result = null; string? failure = null;
        try
        {
            foreach (var f in p.GetProperty("files").EnumerateObject())
            {
                var file = new FileStream(f.Name, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(file);
                Require(Convert.ToHexString(SHA256.HashData(file)) == f.Value.GetString(), "Pinned capability input changed:" + f.Name);
            }
            Require(SetDefaultDllDirectories(0x800), "System32-only native dependency search failed.");
            int hr = CoInitializeEx(0, 0); Require(hr >= 0, "MTA COM initialization failed:" + hr); com = true;
            hr = MFStartup(0x00020070, 1); Require(hr >= 0, "MF no-socket startup failed:" + hr); mf = true;
            string dll = p.GetProperty("decoderDll").GetString()!;
            Require(Path.GetFullPath(dll).Equals(Path.Combine(Environment.SystemDirectory, "msmpeg2vdec.dll"), StringComparison.OrdinalIgnoreCase), "Exact System32 decoder required.");
            module = NativeLibrary.Load(dll);
            var getFactory = (delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(module, "DllGetClassObject");
            Guid clsid = new("62CE7E72-4C71-4D20-B15D-452831A87D9D"), factoryId = new("00000001-0000-0000-C000-000000000046"), transformId = new("BF94C121-5B05-4E6F-8000-BA598961414D"), codecId = new("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA");
            Require(getFactory(&clsid, &factoryId, &factory) >= 0 && factory != 0, "Exact DLL class factory failed.");
            nint* ft = *(nint**)factory;
            Require(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)ft[3])(factory, 0, &transformId, &transform) >= 0 && transform != 0, "H264 transform creation failed.");
            nint* tt = *(nint**)transform;
            Require(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)tt[0])(transform, &codecId, &codec) >= 0 && codec != 0, "ICodecAPI unavailable.");
            Require(MFCreateMediaType(&mediaType) >= 0 && mediaType != 0, "H264 input type creation failed.");
            Guid majorKey = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F"), video = new("73646976-0000-0010-8000-00AA00389B71"),
                subtypeKey = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5"), h264 = new("34363248-0000-0010-8000-00AA00389B71");
            nint* mt = *(nint**)mediaType;
            Require(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)mt[24])(mediaType, &majorKey, &video) >= 0
                && ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)mt[24])(mediaType, &subtypeKey, &h264) >= 0
                && ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)tt[15])(transform, 0, mediaType, 0) >= 0,
                "Minimal documented H264 input type failed.");
            Release(mediaType); mediaType = 0;
            var properties = new List<object>(); bool ready = true;
            // 2 avoids the SDK/Learn discrepancy over whether1 means AUTO.
            foreach (var item in new[] {
                ("threads", new Guid("9561C3E8-EA9E-4435-9B1E-A93E691894D8"), (ushort)19, 2U, true),
                ("thumbnail", new Guid("2EFD8EEE-1150-4328-9CF5-66DCE933FCF4"), (ushort)11, 0U, true),
                ("concealment-drop", new Guid("ECECACE8-3436-462C-9294-CD7BACD758A9"), (ushort)19, 0U, true),
                ("fast-decode", new Guid("6B529F7D-D3B1-49C6-A999-9EC6911BEDBF"), (ushort)19, 0U, false) })
            {
                Require(clock.Elapsed.TotalSeconds < 15, "Capability probe internal deadline.");
                nint* ct = *(nint**)codec; Guid key = item.Item2; var before = new Variant(); var after = new Variant();
                int supported = ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)ct[3])(codec, &key);
                int get = supported == 0 ? ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)ct[8])(codec, &key, &before) : supported;
                ushort variantType = before.Type is 3 or 19 || item.Item3 == 11 && before.Type == 11 ? before.Type : item.Item3;
                var desired = new Variant { Type = variantType, Value = item.Item4 };
                int set = get == 0 ? ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)ct[9])(codec, &key, &desired) : get;
                int read = set == 0 ? ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)ct[8])(codec, &key, &after) : set;
                bool confirmed = supported == 0 && get == 0 && set == 0 && read == 0 && after.Type == variantType
                    && (variantType == 11 ? after.Boolean == 0 : after.Value == item.Item4);
                if (item.Item5 && !confirmed) ready = false;
                if (!item.Item5 && supported == 0 && !confirmed) ready = false;
                properties.Add(new { name = item.Item1, guid = key, mandatory = item.Item5, supported, get, set, read,
                    originalType = before.Type, originalValue = before.Value, returnedType = after.Type, returnedValue = after.Value, confirmed });
                VariantClear(ref before); VariantClear(ref after);
            }
            result = new { status = ready ? "required-properties-confirmed-only" : "required-properties-unavailable-stop", properties,
                // DROP is not an error-free certificate. Even all properties
                // confirmed would still require full sample/fault qualification.
                freshDecodeQualified = false, frameInputs = 0, decodedFrames = 0, noD3DManager = true, noQualityAdvice = true,
                modulePath = dll, loadMethod = "Pinned System32 DLL class factory; no COM registry resolution", secondsBeforeTeardown = clock.Elapsed.TotalSeconds };
        }
        catch (Exception e) { failure = e.ToString(); }
        finally
        {
            Release(mediaType); Release(codec); Release(transform); Release(factory);
            if (mf) { int stop = MFShutdown(); if (stop < 0) failure ??= "MF shutdown failed:" + stop; }
            if (com) CoUninitialize(); if (module != 0) NativeLibrary.Free(module);
            foreach (var file in pins.AsEnumerable().Reverse()) file.Dispose();
            Write(Path.Combine(root, "result.json"), new { result, failure, completeSecondsIncludingTeardown = clock.Elapsed.TotalSeconds,
                mediaExecution = false, gpuDecode = false, publication = false, installation = false });
        }
        return failure == null ? 0 : 1;
    }
    static void Release(nint value) { if (value != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)value)[2])(value); }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    [DllImport("oleaut32.dll")] static extern int VariantClear(ref Variant value);
    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(nint* mediaType);
    [DllImport("mfplat.dll")] static extern int MFShutdown();
}
