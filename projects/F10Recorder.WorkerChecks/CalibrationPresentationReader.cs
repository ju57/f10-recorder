#nullable enable
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace F10Recorder;

// Qualification-only; actual execution needs the separately reviewed admission.
// A future reviewed fixture must own this on ONE extra thread and keep it
// running from before recorder start through source/reference tail closure.
sealed class NativePresentationReader : IDisposable
{
    IntPtr device,context,duplication,staging;
    readonly int thread=Environment.CurrentManagedThreadId;
    long acquired,releasedCount,produced,committed;
    readonly Func<NativePresentationReader,CancellationToken,PresentationRow?>? fakeRead;
    byte[] roi=[];
    bool disposed,failed;
    public long AcquiredRows=>acquired;
    public long ReleasedRows=>releasedCount;
    public long ProducedRows=>produced;
    public long CommittedRows=>committed;
    public bool Failed=>failed;
    public bool CanSeal=>disposed&&!failed&&produced>0&&acquired==releasedCount&&acquired==produced&&produced==committed;
    // No native initialization: exercises the ACTUAL public failure/ledger path.
    internal NativePresentationReader(Func<NativePresentationReader,CancellationToken,PresentationRow?> fakeRead)
    {this.fakeRead=fakeRead;}
    internal void FakeAcquire(){Require(fakeRead!=null,"Generated backend only");acquired++;}
    internal void FakeRelease(){Require(fakeRead!=null,"Generated backend only");releasedCount++;}
    public void CommitThrough(long row)
    {
        try {Require(!disposed&&thread==Environment.CurrentManagedThreadId&&row>=committed&&row<=produced,"Trace cannot commit an unproduced row");committed=row;}
        catch {failed=true;throw;}
    }
    public OutputDescription Output { get; private set; }
    static void HR(int hr)=>Marshal.ThrowExceptionForHR(hr);
    static void Require(bool value,string message){if(!value)throw new IOException(message);}
    static T Method<T>(IntPtr self,int slot) where T:Delegate
        =>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self),slot*IntPtr.Size));
    void Check(){Require(!disposed&&!failed&&thread==Environment.CurrentManagedThreadId,"Observer owner/thread/failed state changed");}

    internal NativePresentationReader(uint outputIndex,int expectedLeft,int expectedTop,int expectedWidth,int expectedHeight,string expectedDeviceName)
    {
        Require(OperatingSystem.IsWindows()&&IntPtr.Size==8,"Windows x64 required");
        IntPtr dxgiDevice=IntPtr.Zero,adapter=IntPtr.Zero,output=IntPtr.Zero,output1=IntPtr.Zero;
        try
        {
            HR(CreateDevice(IntPtr.Zero,1,IntPtr.Zero,0x20,IntPtr.Zero,0,7,out device,out _,out context));
            var iid=new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");HR(Marshal.QueryInterface(device,in iid,out dxgiDevice));
            HR(Method<GetObject>(dxgiDevice,7)(dxgiDevice,out adapter));
            HR(Method<EnumOutput>(adapter,7)(adapter,outputIndex,out output));
            HR(Method<GetOutputDesc>(output,7)(output,out var description));Output=description;
            Require(description.Device==expectedDeviceName&&description.Attached!=0&&description.Rotation==1&&description.Bounds.Left==expectedLeft&&description.Bounds.Top==expectedTop
                &&description.Bounds.Right-description.Bounds.Left==expectedWidth&&description.Bounds.Bottom-description.Bounds.Top==expectedHeight,
                "Actual independent output must match reviewed capture desktop bounds/rotation");
            iid=new("00cddea8-939b-4b83-a340-a685226666cc");HR(Marshal.QueryInterface(output,in iid,out output1));
            HR(Method<Duplicate>(output1,22)(output1,device,out duplication));
            var desc=new TextureDesc{Width=576,Height=48,MipLevels=1,ArraySize=1,Format=87,SampleCount=1,Usage=3,CpuAccess=0x20000};
            HR(Method<CreateTexture>(device,5)(device,ref desc,IntPtr.Zero,out staging));
        }
        catch {failed=true;Dispose();throw;}
        finally {foreach(var value in new[]{output1,output,adapter,dxgiDevice})if(value!=IntPtr.Zero)Marshal.Release(value);}
    }
    public PresentationRow? Read(CancellationToken cancellation)
    {
        try
        {
            Check();cancellation.ThrowIfCancellationRequested();long before=acquired;
            var row=fakeRead==null?ReadOwnedFrame(cancellation):fakeRead(this,cancellation);
            if(row==null)Require(acquired==before,"Timeout cannot hide an acquired frame");
            else
            {
                Require(acquired==before+1&&releasedCount==acquired&&row.Ordinal==produced+1,"Acquired/released/produced row identity changed");
                produced++;
            }
            return row;
        }
        catch {failed=true;throw;}
    }
    PresentationRow? ReadOwnedFrame(CancellationToken cancellation)
    {
        Check();cancellation.ThrowIfCancellationRequested();
        long before=Stopwatch.GetTimestamp();
        int hr=Method<Acquire>(duplication,8)(duplication,25,out var info,out var resource);
        long after=Stopwatch.GetTimestamp();
        if(hr==unchecked((int)0x887A0027))return null; // WAIT_TIMEOUT; no acquired frame.
        HR(hr);acquired++;
        IntPtr texture=IntPtr.Zero;bool mapped=false,released=false;int id=-1;
        try
        {
            // The global flag may describe already-masked pixels elsewhere.
            // Qualify only this fixture's decoded complementary marker ROI.
            if(info.LastPresent!=0&&info.Accumulated!=0)
            {
                var iid=new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");HR(Marshal.QueryInterface(resource,in iid,out texture));
                Method<GetTextureDesc>(texture,10)(texture,out var desc);
                Require(desc.Format==87&&desc.Width>=592&&desc.Height>=56&&desc.SampleCount==1,"Reviewed BGRA8 desktop/ROI shape required");
                var box=new Box{Left=16,Top=8,Front=0,Right=592,Bottom=56,Back=1};
                Method<CopyRegion>(context,46)(context,staging,0,0,0,0,texture,0,ref box);
                Method<Flush>(context,111)(context);
                var wait=Stopwatch.StartNew();Mapped data;
                while(true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Require(wait.Elapsed.TotalMilliseconds<25,"Independent ROI readback deadline");
                    hr=Method<Map>(context,14)(context,staging,0,1,0x100000,out data);
                    if(hr!=unchecked((int)0x887A000A))
                    {
                        HR(hr);mapped=true;
                        Require(wait.Elapsed.TotalMilliseconds<25,"Independent ROI readback deadline");
                        cancellation.ThrowIfCancellationRequested();break;
                    }
                    Thread.Yield();
                }
                Require(data.Data!=IntPtr.Zero&&data.Pitch>=2304&&data.Pitch<=8192,"Bounded native ROI pitch/data");
                int pitch=checked((int)data.Pitch);
                if(roi.Length==0)roi=new byte[pitch*48];
                Require(roi.Length==pitch*48,"Native staging pitch changed");
                for(int y=0;y<48;y++)Marshal.Copy(IntPtr.Add(data.Data,y*pitch),roi,y*pitch,2304); // No desktop padding or pixels outside ROI.
                id=BarcodeRaster.Decode(roi,pitch);
            }
        }
        catch(Exception error)
        {
            failed=true;
            throw new IOException($"Fixture marker ROI failed; ProtectedContentMaskedOut={info.Protected}; LastPresent={info.LastPresent}; AccumulatedFrames={info.Accumulated}",error);
        }
        finally
        {
            try {if(mapped)Method<Unmap>(context,15)(context,staging,0);}
            finally
            {
                if(texture!=IntPtr.Zero)Marshal.Release(texture);
                if(resource!=IntPtr.Zero)Marshal.Release(resource);
                int release=Method<ReleaseFrame>(duplication,14)(duplication);
                if(release<0){failed=true;HR(release);}released=true;releasedCount++;
            }
        }
        return new(acquired,id,info.LastPresent,before,after,info.Accumulated,info.Coalesced!=0,info.Protected!=0,released);
    }
    public void Dispose()
    {
        if(disposed)return;
        Require(thread==Environment.CurrentManagedThreadId,"Native observer must close on owning joined thread");
        Require(acquired==releasedCount,"Native frame release unknown; device/duplication owners retained until fixture exit");
        foreach(var value in new[]{staging,duplication,context,device})if(value!=IntPtr.Zero)Marshal.Release(value);
        staging=duplication=context=device=IntPtr.Zero;disposed=true;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] internal struct Pointer {public Point Position;public uint Visible;}
    [StructLayout(LayoutKind.Sequential)] internal struct FrameInfo
    {public long LastPresent,LastMouse;public uint Accumulated,Coalesced,Protected;public Pointer Pointer;public uint MetadataBytes,PointerBytes;}
    [StructLayout(LayoutKind.Sequential)] internal struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct OutputDescription
    {[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Device;public Rect Bounds;public uint Attached,Rotation;public IntPtr Monitor;}
    [StructLayout(LayoutKind.Sequential)] internal struct TextureDesc
    {public uint Width,Height,MipLevels,ArraySize,Format,SampleCount,SampleQuality,Usage,Bind,CpuAccess,Misc;}
    [StructLayout(LayoutKind.Sequential)] internal struct Box {public uint Left,Top,Front,Right,Bottom,Back;}
    [StructLayout(LayoutKind.Sequential)] internal struct Mapped {public IntPtr Data;public uint Pitch,DepthPitch;}
    [DllImport("d3d11.dll",EntryPoint="D3D11CreateDevice")]static extern int CreateDevice(IntPtr adapter,uint driver,IntPtr software,uint flags,IntPtr levels,uint count,uint sdk,out IntPtr device,out uint level,out IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int GetObject(IntPtr self,out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int EnumOutput(IntPtr self,uint index,out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int GetOutputDesc(IntPtr self,out OutputDescription value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int Duplicate(IntPtr self,IntPtr device,out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int CreateTexture(IntPtr self,ref TextureDesc desc,IntPtr data,out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int Acquire(IntPtr self,uint timeout,out FrameInfo info,out IntPtr resource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate void GetTextureDesc(IntPtr self,out TextureDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate void CopyRegion(IntPtr self,IntPtr dest,uint sub,uint x,uint y,uint z,IntPtr source,uint sourceSub,ref Box box);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int Map(IntPtr self,IntPtr resource,uint sub,uint mode,uint flags,out Mapped value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate void Unmap(IntPtr self,IntPtr resource,uint sub);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate void Flush(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int ReleaseFrame(IntPtr self);
}
