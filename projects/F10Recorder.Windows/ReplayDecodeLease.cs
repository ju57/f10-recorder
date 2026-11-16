using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

// A pathname decoder requires a no-delete pin as well as immutable bytes.
// Unknown child exit transfers this owner to ReplayExportProcess's exact-child
// retention registry; neither UI/session disposal nor GC may unpin its input.
internal sealed class ReplayDecodeLease : IDisposable
{
    ReplayPublicationLease output;
    SafeFileHandle pin;
    readonly string path;
    ReplayNativeExportScope observationOwner;
    bool observationUsed;
    internal ReplayFileExtent CheckedIdentity => output.CheckedIdentity;

    internal ReplayDecodeLease(string path, ReplayPublicationLease retainedOutput = null)
    {
        this.path = Path.GetFullPath(path);
        output = retainedOutput ?? new ReplayPublicationLease(path);
        try { pin = output.PinDecoderPath(path); }
        catch { output.Dispose(); throw; }
    }

    // Metadata eligibility is separate from successful decode/publication.
    // The native scope borrows this exact immutable reader and pathname pin;
    // it never owns or closes either one.
    internal void BindObservation(ReplayExportRequest request)
    {
        if (request?.NativeFiles == null) return;
        if (observationUsed) throw new IOException("Decoder observation is single-use.");
        observationUsed = true;
        request.NativeFiles.BindDecoderObservation(this, path);
        observationOwner = request.NativeFiles;
    }

    internal ReplayFileExtent ObserveReservation(ReplayFileExtent expected)
    {
        if (pin == null || pin.IsClosed || pin.IsInvalid || output == null
            || !output.Stream.CanRead || output.Published)
            throw new IOException("Decoder metadata owner is no longer protected.");
        var actual = ReplayStorage.ObserveInventory(pin, output.CheckedIdentity);
        if (actual.Volume != expected.Volume || actual.Identity != expected.Identity
            || actual.AllocationUnit != expected.AllocationUnit || actual.Length != expected.Length
            || actual.Length != output.CheckedIdentity.Length)
            throw new IOException("Decoder metadata differs from the held output.");
        return actual; // Fresh allocation query, never an inter-callback cache.
    }

    internal void EndObservation(bool uncertain = false)
    {
        // Keep the reference visible until revocation has crossed the scope's
        // observation lock. A concurrent disposer must cross that barrier too.
        var owner = Volatile.Read(ref observationOwner);
        if (owner == null) return;
        owner.EndDecoderObservation(this, uncertain);
        Interlocked.CompareExchange(ref observationOwner, null, owner);
    }

    internal void VerifyUnchanged()
    {
        var observed = ReplayStorage.ObserveInventory(pin, output.CheckedIdentity);
        if (observed.Length != output.CheckedIdentity.Length)
            throw new IOException("Full decode input extent changed during validation.");
    }

    // Only call after confirmed decoder absence and successful full validation.
    // Dropping the path pin permits rename; the main lease still denies writes.
    internal void TransferTo(ReplayExportRequest request)
    {
        VerifyUnchanged();
        EndObservation();
        pin.Dispose(); pin = null;
        request.FreezeValidatedOutput(output);
        output = null;
    }

    public void Dispose()
    {
        EndObservation();
        try { pin?.Dispose(); }
        finally { output?.Dispose(); }
    }
}
