using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

// Keeps the inspected file immutable through claim release, then renames that
// same file object. Reopening a changed pathname must never publish a substitute.
internal sealed class ReplayPublicationLease : IDisposable
{
    internal sealed class OpenTiming
    {
        [JsonInclude] public double? ExtentObservationMilliseconds, ReadHandleOpenMilliseconds, IdentityMilliseconds, FailureCloseMilliseconds;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint size);
    internal readonly FileStream Stream;
    readonly ReplayFileExtent identity;
    readonly string partial;
    ReplayGuardDirectory nativeDirectory;
    readonly ReplayGuardDirectory.FileTicket nativeChecked;
    ReplayGuardDirectory.FileTicket nativePublication;
    bool published;
    ReplayPacketReadWork packetRead;
    internal ReplayFileExtent CheckedIdentity => identity;
    internal bool Published => published;
    internal ReplayGuardDirectory.Observation ObserveNative() => nativePublication?.Observe()
        ?? throw new IOException("Lease has no rooted namespace-pinning observer.");

    // Full decoding first retains its established immutable lease/path pin.
    // After the decoder joins, bind the exact checked object to the request's
    // rooted rename ticket; the checked stream continues to deny all writers.
    internal void BindRootedPublication(ReplayGuardDirectory directory, ReplayGuardDirectory.FileTicket publication)
    {
        try
        {
            if (published || nativeDirectory != null || publication == null || directory == null
                || !Path.GetDirectoryName(partial).Equals(directory.OriginalPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Checked output already owns publication or has a different parent.");
            var actual = publication.Observe().Extent;
            if (actual.Volume != identity.Volume || actual.Identity != identity.Identity || actual.Length != identity.Length
                || actual.AllocationUnit != identity.AllocationUnit || !Stream.CanRead)
                throw new IOException("Rooted publication differs from the immutable decoded object.");
            nativeDirectory = directory; nativePublication = publication;
        }
        catch { publication?.Dispose(); throw; }
    }

    // A path-consuming decoder must see this same object throughout its run.
    // READ_DATA plus no-delete sharing pins the pathname; the main read lease
    // independently prevents writes and survives this pin through publication.
    internal SafeFileHandle PinDecoderPath(string decoderPath = null)
    {
        if (nativeChecked != null) throw new IOException("Rooted packet-validation ownership cannot be used by a pathname decoder.");
        var pin = ReplayStorage.OpenPinnedInventory(decoderPath ?? partial);
        try
        {
            var current = ReplayStorage.ObserveInventory(pin, identity);
            if (current.Length != identity.Length) throw new IOException("Decode input extent changed before pathname protection.");
            return pin;
        }
        catch { pin.Dispose(); throw; }
    }

    internal ReplayPublicationLease(string partial, OpenTiming timing = null)
    {
        this.partial = Path.GetFullPath(partial);
        long at = Stopwatch.GetTimestamp(); ReplayFileExtent listed;
        try { listed = ReplayStorage.Observe(this.partial); }
        finally { if (timing != null) timing.ExtentObservationMilliseconds = (timing.ExtentObservationMilliseconds ?? 0) + Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
        at = Stopwatch.GetTimestamp();
        try { Stream = new(this.partial, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.SequentialScan); }
        finally { if (timing != null) timing.ReadHandleOpenMilliseconds = (timing.ReadHandleOpenMilliseconds ?? 0) + Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
        try
        {
            at = Stopwatch.GetTimestamp();
            try { identity = ReplayStorage.ObserveInventory(Stream.SafeFileHandle, listed); }
            finally { if (timing != null) timing.IdentityMilliseconds = (timing.IdentityMilliseconds ?? 0) + Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
        }
        catch
        {
            at = Stopwatch.GetTimestamp();
            try { Stream.Dispose(); }
            finally { if (timing != null) timing.FailureCloseMilliseconds = (timing.FailureCloseMilliseconds ?? 0) + Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
            throw;
        }
    }
    // Ownership of the actual immutable checked-read ticket transfers at
    // entry, including failure. The directory remains owned by the request.
    // Acquire the no-delete publication ticket BEFORE validation callbacks;
    // read protection alone permits rename/replacement of the checked leaf.
    internal ReplayPublicationLease(string partial, ReplayGuardDirectory directory,
        ReplayGuardDirectory.FileTicket checkedRead, ReplayFileExtent producerOutput)
    {
        try
        {
            this.partial = Path.GetFullPath(partial);
            if (directory == null || checkedRead == null || !checkedRead.IsImmutablePublicationReader
                || !Path.GetDirectoryName(this.partial).Equals(directory.OriginalPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Rooted publication requires a compatible immutable reader and admitted parent.");
            var actual = checkedRead.Observe().Extent;
            if (producerOutput == null || actual.Volume != producerOutput.Volume || actual.Identity != producerOutput.Identity
                || actual.Length != producerOutput.Length || actual.AllocationUnit != producerOutput.AllocationUnit)
                throw new IOException("Rooted checked reader differs from the completed producer.");
            nativeDirectory = directory; nativeChecked = checkedRead; identity = actual;
            nativePublication = directory.OpenForRemoval(Path.GetFileName(this.partial), identity);
            Stream = checkedRead.Data();
        }
        catch
        {
            try { nativePublication?.Dispose(); } finally { checkedRead?.Dispose(); }
            throw;
        }
    }
    internal void Publish(string destination, Action requireFreshness = null)
    {
        if (published) throw new IOException("Validated replay output was already published.");
        destination = Path.GetFullPath(destination);
        if (nativePublication != null)
        {
            if (!Path.GetDirectoryName(destination).Equals(nativeDirectory.OriginalPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Rooted replay publication cannot change its admitted output parent.");
            nativePublication.RenameTo(nativeDirectory, Path.GetFileName(destination), requireFreshness);
            published = true;
            return;
        }
        // The short-lived delete handle denies other renames/replacements. The
        // long-lived read lease still denies writes. Metadata/storage readers
        // need no delete access during validation or durable claim release.
        using var file = CreateFile(partial, 0x10080, 3, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (file.IsInvalid) throw new Win32Exception();
        var current = ReplayStorage.ObserveInventory(file, identity);
        if (current.Length != identity.Length) throw new IOException("Validated replay output changed before publication.");
        string directory = Path.GetDirectoryName(destination);
        using var anchor = Native.AnchorDirectory(directory);
        if (Native.DirectoryIdentity(anchor) != Native.DirectoryIdentity(directory))
            throw new IOException("Replay publication destination changed.");
        byte[] name = Encoding.Unicode.GetBytes(destination);
        int rootOffset = IntPtr.Size, lengthOffset = rootOffset + IntPtr.Size, nameOffset = lengthOffset + 4;
        byte[] information = new byte[checked(nameOffset + name.Length + 2)];
        // ReplaceIfExists=false, RootDirectory=NULL: use the documented Win32
        // absolute-path form while anchoring the admitted target directory.
        BitConverter.GetBytes(name.Length).CopyTo(information, lengthOffset); name.CopyTo(information, nameOffset);
        requireFreshness?.Invoke();
        if (!SetFileInformationByHandle(file, 3, information, (uint)information.Length)) throw new Win32Exception();
        published = true;
    }
    internal ReplayPacketReadWork OpenPacketReadWork()
    {
        if(nativeChecked==null||!nativeChecked.IsImmutablePublicationReader||!Stream.IsAsync)return null;
        if(packetRead!=null)throw new IOException("Checked offset-read borrow is single-use.");
        var handle=Stream.SafeFileHandle;
        return packetRead=new((memory,offset,token)=>
        {
            if(published||!Stream.CanRead)throw new IOException("Checked offset-read object is closed or published.");
            return RandomAccess.ReadAsync(handle,memory,offset,token);
        });
    }
    public void Dispose()
    {
        if(packetRead is {Joined:false})throw new IOException("Checked readers have not joined; immutable lease remains retained.");
        if (nativeChecked == null) { try { nativePublication?.Dispose(); } finally { Stream.Dispose(); } return; }
        try { nativePublication?.Dispose(); } finally { nativeChecked.Dispose(); }
    }
}
