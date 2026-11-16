using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

// One accepted save's main output only. Metadata access is not a data owner or
// a no-delete promise: match the current namespace on EACH side of every fresh
// native EOF/allocation query. Never reuse numeric observations across writes.
// Other claims and custom filesystem semantics still use the supplied provider.
internal sealed class ReplayActiveOutputObservation(ReplayFiles files, string partial) : ReplayFiles, IDisposable
{
    internal enum ClosePhase { BeforeHandle, AfterHandle, BeforeParents, AfterParents }
    internal Action<ClosePhase> ClosingForChecks;
    internal SafeFileHandle OwnedHandleForChecks => owned;
    readonly string path = Path.GetFullPath(partial);
    SafeFileHandle owned;
    ReplayFileExtent identity;
    IDisposable parents;
    ReplayPublicationLease validated;
    bool completed, disposed;
    internal bool OwnsHandle => owned is { IsClosed: false };
    internal bool HasOwnedResources => owned != null || parents != null;

    static void Match(ReplayStorage.DirectoryEntry listed, ReplayFileExtent expected)
    {
        if ((listed.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || listed.Extent.Volume != expected.Volume || listed.Extent.Identity != expected.Identity
            || listed.Extent.AllocationUnit != expected.AllocationUnit)
            throw new ReplayOutputIdentityException("Replay output namespace identity or attributes changed; evidence is retained.");
    }

    internal override ReplayFileExtent Observe(string candidate)
    {
        if (!Path.GetFullPath(candidate).Equals(path, StringComparison.OrdinalIgnoreCase)) return files.Observe(candidate);
        try { return ObserveOwned(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            if (error is ReplayOutputIdentityException) throw;
            throw new ReplayOutputIdentityException("Replay output metadata observation is uncertain; evidence is retained.", error);
        }
    }

    ReplayFileExtent ObserveOwned()
    {
        if (disposed) throw new ObjectDisposedException(nameof(ReplayActiveOutputObservation));
        // Pin directory ancestry before observing/opening the leaf. The file
        // remains delete-sharing; disappearance/replacement is a failed proof.
        if (!completed) parents ??= TrustedStorage.PinDirectories(Path.GetDirectoryName(path));
        var before = ReplayStorage.DirectoryLeaf(path);
        if ((before.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new ReplayOutputIdentityException("Replay output is not an ordinary file.");
        ReplayFileExtent current;
        if (completed)
        {
            if (validated == null) throw new ReplayOutputIdentityException("Closed output observer has no validated owner.");
            Match(before, validated.CheckedIdentity);
            current = ReplayStorage.ObserveInventory(validated.Stream.SafeFileHandle, validated.CheckedIdentity);
            if (current.Length != validated.CheckedIdentity.Length)
                throw new ReplayOutputIdentityException("Validated output length changed after metadata observer closure.");
        }
        else if (owned == null)
        {
            var handle = ReplayStorage.OpenInventory(path);
            try
            {
                current = ReplayStorage.ObserveInventory(handle, before.Extent);
                identity = current; owned = handle;
            }
            catch { handle.Dispose(); throw; }
        }
        else
        {
            Match(before, identity);
            current = ReplayStorage.RefreshInventoryAllocation(owned, identity);
        }
        Match(ReplayStorage.DirectoryLeaf(path), current);
        return current;
    }

    internal override ReplayVolumeCapacity ObserveCapacity(string directory) => files.ObserveCapacity(directory);

    // This is a state transition, not a timestamp-based decision. Close every
    // owned observer BEFORE Saved, then query via the already validated read
    // owner for post-close accounting. The borrowed owner is never disposed here.
    internal void Complete(ReplayPublicationLease output)
    {
        if (completed || disposed || output == null) throw new ReplayOutputIdentityException("Output observation completion requires one validated owner.");
        var current = Observe(path);
        if (current.Volume != output.CheckedIdentity.Volume || current.Identity != output.CheckedIdentity.Identity
            || current.AllocationUnit != output.CheckedIdentity.AllocationUnit || current.Length != output.CheckedIdentity.Length)
            throw new ReplayOutputIdentityException("Validated owner differs from the observed output.");
        try
        {
            var handle = output.Stream.SafeFileHandle;
            if (handle.IsClosed || handle.IsInvalid) throw new IOException("Validated read owner is closed.");
            var actual = ReplayStorage.ObserveInventory(handle, output.CheckedIdentity);
            if (actual.Length != current.Length) throw new IOException("Validated read owner has a different EOF.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        { throw new ReplayOutputIdentityException("Validated read ownership is unconfirmed before observer closure.", error); }
        CloseOwned();
        validated = output; completed = true;
    }

    void CloseOwned()
    {
        // Keep the reference if closure fails; never turn an attempted close
        // into a certified transition or release another owner's read stream.
        try
        {
            ClosingForChecks?.Invoke(ClosePhase.BeforeHandle);
            owned?.Dispose();
            if (owned != null && !owned.IsClosed) throw new IOException("Replay output metadata observer closure is unconfirmed.");
            ClosingForChecks?.Invoke(ClosePhase.AfterHandle);
            owned = null;
            ClosingForChecks?.Invoke(ClosePhase.BeforeParents);
            parents?.Dispose(); parents = null;
            ClosingForChecks?.Invoke(ClosePhase.AfterParents);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { throw new ReplayOutputIdentityException("Replay output observer closure is uncertain; evidence is retained.", error); }
    }

    public void Dispose()
    {
        if (disposed) return;
        CloseOwned(); validated = null; disposed = true;
    }
}
