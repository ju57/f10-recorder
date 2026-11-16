using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

// Request-owned directory capability. Direct leaf operations resolve through
// the held native object and pinned ordinary ancestry. Its owner retains the
// capability until every file user and native operation has completed.
internal sealed class ReplayGuardDirectory : IDisposable
{
    const uint ReadData=1, WriteData=2, ReadAttributes=0x80, DeleteAccess=0x10000, Synchronize=0x100000;
    const uint OpenDisposition=1, CreateDisposition=2, OpenReparse=0x200000, NonDirectory=0x40, Synchronous=0x20;
    const int Pending=0x103;
    [StructLayout(LayoutKind.Sequential)] struct UnicodeString { internal ushort Length,MaximumLength;internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] struct ObjectAttributes { internal int Length;internal IntPtr RootDirectory,ObjectName;internal uint Attributes;internal IntPtr SecurityDescriptor,SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] struct StandardInformation { internal long AllocationSize,EndOfFile;internal uint Links;internal byte DeletePending,Directory; }
    [StructLayout(LayoutKind.Sequential)] struct AttributeInformation { internal uint Attributes,ReparseTag; }
    [DllImport("ntdll.dll")]static extern int NtCreateFile(IntPtr fileSlot,uint access,ref ObjectAttributes attributes,IntPtr status,IntPtr allocation,uint fileAttributes,uint share,uint disposition,uint options,IntPtr ea,uint eaLength);
    [DllImport("ntdll.dll")]static extern int NtSetInformationFile(IntPtr file,IntPtr status,IntPtr information,uint size,int kind);
    [DllImport("ntdll.dll")]static extern int NtWaitForSingleObject(IntPtr file,[MarshalAs(UnmanagedType.U1)]bool alertable,IntPtr timeout);
    [DllImport("ntdll.dll")]static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int kind,out StandardInformation information,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int kind,out AttributeInformation information,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int kind,byte[] information,uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder name,uint length,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool GetDiskFreeSpace(string path,out uint sectors,out uint bytes,out uint free,out uint total);
    readonly SafeFileHandle directory;
    static readonly object operationGate=new(); // Serialize native metadata operations; data reads remain outside this gate.
    static readonly Encoding strictUnicode=new UnicodeEncoding(false,false,true);
    readonly IDisposable ancestry;
    readonly ReplayGuardDirectory parent;
    readonly string volume;
    readonly long allocationUnit;
    bool disposed;
    int tickets;
    internal Action BeforeNativeOpenForChecks,AfterNativeOpenForChecks,BeforeNativeMutationForChecks,AfterNativeMutationForChecks;
    internal string Identity { get; }
    internal string OriginalPath { get; }
    internal bool CreatedNewDirectory { get; }

    ReplayGuardDirectory(ReplayGuardDirectory parent,SafeFileHandle handle,string leaf,string expectedIdentity,bool created)
    {
        directory=handle;OriginalPath=Path.Combine(parent.OriginalPath,leaf);volume=parent.volume;allocationUnit=parent.allocationUnit;
        CheckDirectory();Identity=Native.DirectoryIdentity(directory);
        if((expectedIdentity!=null&&Identity!=expectedIdentity)||Volume(directory)!=volume)throw new IOException("Child directory differs from its authorized native object.");
        this.parent=parent;CreatedNewDirectory=created;parent.tickets++;
    }

    internal ReplayGuardDirectory(string path,string expectedDirectoryIdentity,string expectedVolume)
    {
        OriginalPath=Path.GetFullPath(path);
        if(string.IsNullOrWhiteSpace(expectedDirectoryIdentity)||string.IsNullOrWhiteSpace(expectedVolume))throw new ArgumentException("Expected native directory identity and volume are required.");
        try
        {
            ancestry=TrustedStorage.PinDirectories(OriginalPath);
            directory=CreateFile(OriginalPath,0x81,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
            if(directory.IsInvalid)throw new Win32Exception();
            CheckDirectory();Identity=Native.DirectoryIdentity(directory);
            if(Identity!=expectedDirectoryIdentity)throw new IOException("Authorized operation directory changed.");
            volume=Volume(directory);
            if(!volume.Equals(expectedVolume,StringComparison.OrdinalIgnoreCase))throw new IOException("Authorized operation volume changed.");
            if(!GetDiskFreeSpace(volume,out uint sectors,out uint bytes,out _,out _))throw new Win32Exception();
            allocationUnit=checked((long)sectors*bytes);
            if(allocationUnit<=0)throw new IOException("Unknown operation volume geometry.");
        }
        catch{directory?.Dispose();ancestry?.Dispose();throw;}
    }

    internal static void ValidateLeaf(string leaf)
    {
        if(string.IsNullOrEmpty(leaf)||leaf.Length>255||leaf is "." or ".."||leaf.IndexOfAny(['\\','/',':','\0'])>=0||leaf.EndsWith('.')||leaf.EndsWith(' '))
            throw new IOException("A direct ordinary leaf name is required.");
        for(int n=0;n<leaf.Length;n++)
        {
            if(char.IsHighSurrogate(leaf[n])){if(n+1>=leaf.Length||!char.IsLowSurrogate(leaf[++n]))throw new IOException("Malformed UTF16 leaf name.");}
            else if(char.IsLowSurrogate(leaf[n]))throw new IOException("Malformed UTF16 leaf name.");
        }
    }
    static void Status(int value){if(value<0)throw new Win32Exception(checked((int)RtlNtStatusToDosError(value)));}
    static string Volume(SafeFileHandle file)
    {
        var name=new StringBuilder(32768);uint count=GetFinalPathNameByHandle(file,name,(uint)name.Capacity,1);
        if(count==0||count>=name.Capacity)throw new Win32Exception();
        string path=name.ToString();int end=path.IndexOf("}\\",StringComparison.Ordinal);
        if(!path.StartsWith(@"\\?\Volume{",StringComparison.OrdinalIgnoreCase)||end<0)throw new IOException("Unknown native volume name.");
        return path[..(end+2)];
    }
    void CheckDirectory()
    {
        if(disposed)throw new ObjectDisposedException(nameof(ReplayGuardDirectory));
        if(!GetFileInformationByHandleEx(directory,9,out AttributeInformation attributes,(uint)Marshal.SizeOf<AttributeInformation>()))throw new Win32Exception();
        if((attributes.Attributes&(uint)FileAttributes.Directory)==0||(attributes.Attributes&(uint)FileAttributes.ReparsePoint)!=0||attributes.ReparseTag!=0)
            throw new IOException("Operation directory is no longer ordinary.");
    }
    internal static void CompleteForChecks(int status,IntPtr file,IntPtr block,Func<IntPtr,int> wait)=>Complete(status,file,block,wait:wait);
    static void Complete(int status,IntPtr file,IntPtr block,IntPtr fileSlot=default,Func<IntPtr,int> wait=null)
    {
        int waitFailure=0;
        if(status==Pending)
        {
            // IOSB, the returned-handle slot, names, buffers and root references
            // stay live through completion. No pending status is a success.
            while((status=Marshal.ReadInt32(block))==Pending)
            {
                if(fileSlot!=IntPtr.Zero)file=Marshal.ReadIntPtr(fileSlot);
                if(file!=IntPtr.Zero&&file!=new IntPtr(-1))
                {
                    int waited=wait==null?NtWaitForSingleObject(file,false,IntPtr.Zero):wait(file);
                    if(waited!=0)waitFailure=waited;
                }
                // A wait failure is not proof the I/O ended. Poll the still-owned
                // IOSB through completion; never unwind pending native memory.
                if(Marshal.ReadInt32(block)==Pending){try{Thread.Sleep(1);}catch(ThreadInterruptedException){waitFailure=unchecked((int)0xC0000001);}}
            }
        }
        Status(status);
        if(waitFailure!=0)throw new IOException("Native wait failed (0x"+unchecked((uint)waitFailure).ToString("X8")+"); operation drained and owners retained through completion.");
    }
    internal FileTicket OpenRead(string leaf,ReplayFileExtent expected)=>Open(leaf,ReadData,OpenDisposition,1,expected);
    internal FileTicket OpenAsyncRead(string leaf,ReplayFileExtent expected)=>Open(leaf,ReadData,OpenDisposition,1,expected,asynchronous:true);
    internal FileTicket OpenPublicationRead(string leaf,ReplayFileExtent expected)=>Open(leaf,ReadData,OpenDisposition,5,expected);
    internal FileTicket OpenAsyncPublicationRead(string leaf,ReplayFileExtent expected)=>Open(leaf,ReadData,OpenDisposition,5,expected,asynchronous:true);
    internal FileTicket OpenObservation(string leaf,ReplayFileExtent expected)=>Open(leaf,ReadData,OpenDisposition,3,expected);
    internal FileTicket OpenForRemoval(string leaf,ReplayFileExtent expected)=>Open(leaf,DeleteAccess,OpenDisposition,3,expected);
    internal FileTicket CreateNew(string leaf)=>Open(leaf,ReadData|WriteData,CreateDisposition,1,null);
    // Shared authority mutex name: open-or-create, exclusive, relative to the
    // held ordinary profiles directory. No data write/truncate occurs here.
    internal FileTicket OpenAuthorityLock(string leaf)=>Open(leaf,ReadData|WriteData,3,0,null);
    // Explicitly uncertified as a managed single-writer main output. The
    // external producer integration still needs its own same-object/pipe proof.
    internal FileTicket CreateExternalScratch(string leaf)=>Open(leaf,ReadData|WriteData,CreateDisposition,3,null);
    FileTicket Open(string leaf,uint access,uint disposition,uint share,ReplayFileExtent expected,bool asynchronous=false)
    {lock(operationGate)return OpenCore(leaf,access,disposition,share,expected,asynchronous);}
    FileTicket OpenCore(string leaf,uint access,uint disposition,uint share,ReplayFileExtent expected,bool asynchronous)
    {
        ValidateLeaf(leaf);if(disposition!=CreateDisposition&&disposition!=3&&expected==null)throw new ArgumentNullException(nameof(expected));
        SafeFileHandle file=OpenNative(leaf,access,disposition,share,false,asynchronous);
        try
        {
            var ticket=new FileTicket(this,file,leaf,(access&ReadData)!=0,(access&WriteData)!=0,(access&DeleteAccess)!=0,disposition==CreateDisposition,share,asynchronous);file=null;
            try{var actual=ticket.Observe();if(expected!=null&&(actual.Extent.Volume!=expected.Volume||actual.Extent.Identity!=expected.Identity||actual.Extent.Length!=expected.Length||actual.Extent.AllocationUnit!=expected.AllocationUnit))throw new IOException("Operation target differs from its expected native object.");CheckDirectory();return ticket;}
            catch{ticket.Dispose();throw;}
        }
        finally{file?.Dispose();}
    }
    SafeFileHandle OpenNative(string leaf,uint access,uint disposition,uint share,bool isDirectory,bool asynchronous=false)
    {
        ValidateLeaf(leaf);
        bool rootRef=false;IntPtr text=IntPtr.Zero,name=IntPtr.Zero,block=IntPtr.Zero,slot=IntPtr.Zero;SafeFileHandle file=null;
        try
        {
            directory.DangerousAddRef(ref rootRef);CheckDirectory();
            text=Marshal.StringToHGlobalUni(leaf);name=Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());block=Marshal.AllocHGlobal(2*IntPtr.Size);slot=Marshal.AllocHGlobal(IntPtr.Size);Marshal.WriteIntPtr(slot,IntPtr.Zero);
            for(int n=0;n<2*IntPtr.Size;n++)Marshal.WriteByte(block,n,0);
            Marshal.WriteInt32(block,Pending);
            Marshal.StructureToPtr(new UnicodeString{Length=checked((ushort)(leaf.Length*2)),MaximumLength=checked((ushort)(leaf.Length*2+2)),Buffer=text},name,false);
            var attributes=new ObjectAttributes{Length=Marshal.SizeOf<ObjectAttributes>(),RootDirectory=directory.DangerousGetHandle(),ObjectName=name,Attributes=0x1040}; // CASE_INSENSITIVE|DONT_REPARSE
            BeforeNativeOpenForChecks?.Invoke();
            int status=NtCreateFile(slot,access|ReadAttributes|Synchronize,ref attributes,block,IntPtr.Zero,isDirectory?0x10u:0x80u,share,disposition,OpenReparse|(isDirectory?1u:NonDirectory)|(asynchronous?0u:Synchronous),IntPtr.Zero,0);
            try{Complete(status,IntPtr.Zero,block,slot);}finally{AfterNativeOpenForChecks?.Invoke();}
            file=new SafeFileHandle(Marshal.ReadIntPtr(slot),true);Marshal.WriteIntPtr(slot,IntPtr.Zero);
            if(file.IsInvalid)throw new IOException("Completed create has no valid owned handle.");
            if(disposition==CreateDisposition&&Marshal.ReadIntPtr(block,IntPtr.Size).ToInt64()!=2)throw new IOException("New-file origin was not certified by FILE_CREATED.");
            CheckDirectory();var result=file;file=null;return result;
        }
        finally{file?.Dispose();if(slot!=IntPtr.Zero){IntPtr remaining=Marshal.ReadIntPtr(slot);if(remaining!=IntPtr.Zero&&remaining!=new IntPtr(-1))new SafeFileHandle(remaining,true).Dispose();Marshal.FreeHGlobal(slot);}if(block!=IntPtr.Zero)Marshal.FreeHGlobal(block);if(name!=IntPtr.Zero)Marshal.FreeHGlobal(name);if(text!=IntPtr.Zero)Marshal.FreeHGlobal(text);if(rootRef)directory.DangerousRelease();}
    }

    internal ReplayGuardDirectory CreateDirectory(string leaf)=>ChildDirectory(leaf,null,true);
    internal IReadOnlyList<ReplayStorage.DirectoryEntry> EnumerateLeaves(int maximumEntries)
    {
        lock(operationGate)
        {
            if(maximumEntries<0||maximumEntries>4096)throw new ArgumentOutOfRangeException(nameof(maximumEntries));
            CheckDirectory();
            using var storage=new ReplayDirectoryBuffer();
            byte[] buffer=storage.Bytes;bool restart=true;var result=new List<ReplayStorage.DirectoryEntry>();
            while(true)
            {
                storage.Prepare();
                if(!GetFileInformationByHandleEx(directory,restart?11:10,buffer,ReplayDirectoryBuffer.Size))
                {
                    int error=Marshal.GetLastWin32Error();if(error==18)break;throw new Win32Exception(error);
                }
                restart=false;int offset=0;
                while(true)
                {
                    if(offset<0||offset>ReplayDirectoryBuffer.Size-104)throw new IOException("Invalid rooted directory offset.");
                    uint next=BitConverter.ToUInt32(buffer,offset),bytes=BitConverter.ToUInt32(buffer,offset+60);
                    if(bytes==0||bytes%2!=0||bytes>ReplayDirectoryBuffer.Size-offset-104
                        ||(next!=0&&(next%8!=0||next<104+bytes||next>ReplayDirectoryBuffer.Size-offset-104)))throw new IOException("Invalid rooted directory record.");
                    string leaf;
                    try { leaf=strictUnicode.GetString(buffer,offset+104,(int)bytes); }
                    catch(DecoderFallbackException error) { throw new IOException("Uncertain UTF16 namespace entry.",error); }
                    if(leaf is not("." or ".."))
                    {
                        ValidateLeaf(leaf);if(result.Count>=maximumEntries)throw new IOException("Rooted directory membership exceeds its declared bound.");
                        ulong id=BitConverter.ToUInt64(buffer,offset+96);if(id==0)throw new IOException("Rooted directory member has no identity.");
                        result.Add(new(Path.Combine(OriginalPath,leaf),(FileAttributes)BitConverter.ToUInt32(buffer,offset+56),
                            new(volume,Identity[..8]+":"+id.ToString("X16"),BitConverter.ToInt64(buffer,offset+48),BitConverter.ToInt64(buffer,offset+40),allocationUnit)));
                    }
                    if(next==0)break;offset=checked(offset+(int)next);
                }
            }
            CheckDirectory();return result.AsReadOnly();
        }
    }
    internal ReplayGuardDirectory OpenDirectory(string leaf,string expectedIdentity)
    {
        if(string.IsNullOrWhiteSpace(expectedIdentity))throw new ArgumentException("Expected child directory identity required.");
        return ChildDirectory(leaf,expectedIdentity,false);
    }
    ReplayGuardDirectory ChildDirectory(string leaf,string expectedIdentity,bool create)
    {
        lock(operationGate)
        {
            SafeFileHandle child=OpenNative(leaf,ReadData,create?CreateDisposition:OpenDisposition,3,true);
            try{var result=new ReplayGuardDirectory(this,child,leaf,expectedIdentity,create);child=null;return result;}
            finally{child?.Dispose();}
        }
    }
    internal void DeleteEmptyDirectory(string leaf,string expectedIdentity)
    {
        lock(operationGate)
        {
            if(string.IsNullOrWhiteSpace(expectedIdentity))throw new ArgumentException("Expected directory identity required.");
            using var removal=OpenNative(leaf,DeleteAccess,OpenDisposition,3,true);
            if(Native.DirectoryIdentity(removal)!=expectedIdentity||Volume(removal)!=volume||!GetFileInformationByHandleEx(removal,9,out AttributeInformation tag,(uint)Marshal.SizeOf<AttributeInformation>())||(tag.Attributes&(uint)FileAttributes.Directory)==0||(tag.Attributes&(uint)FileAttributes.ReparsePoint)!=0||tag.ReparseTag!=0)
                throw new IOException("Cleanup directory identity/type is uncertain.");
            IntPtr block=IntPtr.Zero,info=IntPtr.Zero;
            try
            {
                block=Marshal.AllocHGlobal(2*IntPtr.Size);for(int n=0;n<2*IntPtr.Size;n++)Marshal.WriteByte(block,n,0);Marshal.WriteInt32(block,Pending);info=Marshal.AllocHGlobal(1);Marshal.WriteByte(info,1);
                BeforeNativeMutationForChecks?.Invoke();
                try{Complete(NtSetInformationFile(removal.DangerousGetHandle(),block,info,1,13),removal.DangerousGetHandle(),block);}
                finally{AfterNativeMutationForChecks?.Invoke();}
                // After disposition the removal owner is close-only. Never
                // enumerate/requery it, even to verify that deletion completed.
            }
            finally{if(info!=IntPtr.Zero)Marshal.FreeHGlobal(info);if(block!=IntPtr.Zero)Marshal.FreeHGlobal(block);}
        }
    }

    internal sealed record Observation(ReplayFileExtent Extent,uint Links,uint Attributes);
    internal sealed class FileTicket : IDisposable
    {
        readonly ReplayGuardDirectory root;
        readonly SafeFileHandle file;
        readonly bool readable,writable,removable,asynchronous;
        readonly string identity;
        FileStream stream;
        bool retired,disposed;
        internal string Leaf { get; }
        internal bool CreatedNew { get; }
        internal bool AllowsOtherWriters { get; }
        internal bool DataOwnerClosed => stream == null || file.IsClosed;
        readonly uint shareMode;
        internal bool IsImmutablePublicationReader => readable && !writable && !removable && !AllowsOtherWriters && (shareMode & 4) != 0;
        internal Stream OpenHistoryRead(CancellationToken cancellation)
        {
            lock(operationGate)
            {
                Require();
                if(!readable || writable || removable || shareMode != 1 || !asynchronous)
                    throw new IOException("Completed history requires an immutable asynchronous read owner.");
                return new HistoryReadView(file, cancellation);
            }
        }
        // Offset-local parser view; it never owns/closes the history ticket or
        // shares a mutable FileStream position with request readers.
        sealed class HistoryReadView(SafeFileHandle handle, CancellationToken cancellation) : Stream
        {
            long position;
            bool closed;
            public override bool CanRead => !closed;
            public override bool CanSeek => !closed;
            public override bool CanWrite => false;
            public override long Length => RandomAccess.GetLength(handle);
            public override long Position { get => position; set { if(value < 0)throw new ArgumentOutOfRangeException(nameof(value)); position=value; } }
            public override int Read(byte[] buffer,int offset,int count)
            {
                ObjectDisposedException.ThrowIf(closed,this);
                var read=RandomAccess.ReadAsync(handle,buffer.AsMemory(offset,count),position,cancellation);
                int length=read.IsCompleted ? read.GetAwaiter().GetResult() : read.AsTask().GetAwaiter().GetResult();
                position=checked(position+length);return length;
            }
            public override long Seek(long offset,SeekOrigin origin)
            {Position=checked(offset+(origin==SeekOrigin.Begin?0:origin==SeekOrigin.Current?position:Length));return position;}
            public override void Flush()=>throw new NotSupportedException();
            public override void SetLength(long length)=>throw new NotSupportedException();
            public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
            protected override void Dispose(bool disposing){closed=true;base.Dispose(disposing);}
        }
        internal FileTicket(ReplayGuardDirectory root,SafeFileHandle file,string leaf,bool readable,bool writable,bool removable,bool created,uint share,bool asynchronous)
        {this.root=root;this.file=file;Leaf=leaf;this.readable=readable;this.writable=writable;this.removable=removable;this.asynchronous=asynchronous;shareMode=share;CreatedNew=created;AllowsOtherWriters=(share&2)!=0;identity=Native.DirectoryIdentity(file);if(Volume(file)!=root.volume)throw new IOException("Operation file volume changed.");root.tickets++;}
        void Require(){if(disposed||retired)throw new ObjectDisposedException(nameof(FileTicket));root.CheckDirectory();}
        internal Observation Observe()
        {lock(operationGate)return ObserveCore();}
        Observation ObserveCore()
        {
            Require();
            if(!GetFileInformationByHandleEx(file,1,out StandardInformation standard,(uint)Marshal.SizeOf<StandardInformation>())||!GetFileInformationByHandleEx(file,9,out AttributeInformation attributes,(uint)Marshal.SizeOf<AttributeInformation>()))throw new Win32Exception();
            if(standard.Directory!=0||standard.DeletePending!=0||standard.Links==0||(attributes.Attributes&(uint)(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0||attributes.ReparseTag!=0)throw new IOException("Operation file is not an available ordinary object.");
            return new(ReplayStorage.Validate(new(root.volume,identity,standard.AllocationSize,standard.EndOfFile,root.allocationUnit)),standard.Links,attributes.Attributes);
        }
        internal FileStream Data()
        {
            lock(operationGate){
            Require();if(!readable&&!writable)throw new IOException("Metadata/removal ticket cannot expose data.");
            return stream??=new FileStream(file,readable&&writable?FileAccess.ReadWrite:writable?FileAccess.Write:FileAccess.Read,65536,asynchronous);
            }
        }
        void Set(int kind,byte[] information,ReplayGuardDirectory destination=null)
        {
            Require();bool targetRef=false,sourceRef=false;IntPtr block=IntPtr.Zero,buffer=IntPtr.Zero;
            try
            {
                if(destination!=null){destination.directory.DangerousAddRef(ref targetRef);destination.CheckDirectory();}
                file.DangerousAddRef(ref sourceRef);
                block=Marshal.AllocHGlobal(2*IntPtr.Size);for(int n=0;n<2*IntPtr.Size;n++)Marshal.WriteByte(block,n,0);
                Marshal.WriteInt32(block,Pending);
                buffer=Marshal.AllocHGlobal(information.Length);Marshal.Copy(information,0,buffer,information.Length);
                if(kind is 10 or 13)retired=true; // From this point only close is allowed, including exceptional completion.
                var hooks=destination??root;hooks.BeforeNativeMutationForChecks?.Invoke();
                try{Complete(NtSetInformationFile(file.DangerousGetHandle(),block,buffer,(uint)information.Length,kind),file.DangerousGetHandle(),block);}
                finally{hooks.AfterNativeMutationForChecks?.Invoke();}
            }
            catch{retired=true;throw;}
            finally{if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);if(block!=IntPtr.Zero)Marshal.FreeHGlobal(block);if(sourceRef)file.DangerousRelease();if(targetRef)destination.directory.DangerousRelease();}
        }
        static byte[] NameInformation(ReplayGuardDirectory destination,string leaf)
        {
            ValidateLeaf(leaf);byte[] text=strictUnicode.GetBytes(leaf);int rootOffset=IntPtr.Size,lengthOffset=2*IntPtr.Size,nameOffset=lengthOffset+4;
            byte[] data=new byte[Math.Max(nameOffset+text.Length+2,3*IntPtr.Size)];
            if(IntPtr.Size==8)BitConverter.GetBytes(destination.directory.DangerousGetHandle().ToInt64()).CopyTo(data,rootOffset);else BitConverter.GetBytes(destination.directory.DangerousGetHandle().ToInt32()).CopyTo(data,rootOffset);
            BitConverter.GetBytes(text.Length).CopyTo(data,lengthOffset);text.CopyTo(data,nameOffset);return data; // ReplaceIfExists=false
        }
        internal void LinkTo(ReplayGuardDirectory destination,string leaf){lock(operationGate){_ = ObserveCore();Set(11,NameInformation(destination,leaf),destination);}}
        void RequireRemoval(){if(!removable||stream is{CanRead:true}||stream is{CanWrite:true})throw new IOException("Removal requires a dedicated metadata ticket and closed data users.");}
        internal void RenameTo(ReplayGuardDirectory destination,string leaf){lock(operationGate){RequireRemoval();_ = ObserveCore();Set(10,NameInformation(destination,leaf),destination);retired=true;}}
        internal void Delete(){lock(operationGate){RequireRemoval();_ = ObserveCore();Set(13,new byte[]{1});retired=true;}}
        public void Dispose(){lock(operationGate){if(disposed)return;try{stream?.Dispose();}finally{file.Dispose();if(file.IsClosed){disposed=true;root.tickets--;}}}}
    }
    public void Dispose(){lock(operationGate){if(disposed)return;if(tickets!=0)throw new IOException("Operation directory still has live file/directory users.");directory.Dispose();ancestry?.Dispose();disposed=true;if(parent!=null)parent.tickets--;}}
}
