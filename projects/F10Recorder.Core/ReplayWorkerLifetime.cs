namespace F10Recorder;

// One operation owns these facts independently of mutable diagnostic output.
// An unconfirmed join is sticky; the owner cannot reuse it for another batch.
internal sealed class ReplayWorkerLifetime
{
    int state; // not started / active / joined / uncertain
    internal bool Started => Volatile.Read(ref state) != 0;
    internal bool Joined => Volatile.Read(ref state) is 0 or 2;

    internal void Begin()
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            throw new IOException("Replay worker lifetime is single-use.");
    }

    internal void Complete(bool joined)
    {
        if (!joined) Volatile.Write(ref state, 3);
        else Interlocked.CompareExchange(ref state, 2, 1);
    }
}
