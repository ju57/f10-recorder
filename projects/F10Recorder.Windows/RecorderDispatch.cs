namespace F10Recorder;

// Owner-thread dispatch and engine lifetime fencing; knows no capture rules.
internal sealed class RecorderDispatch(Action<Action> post)
{
    int generation;
    bool disposed;
    internal Action<Action> NextGeneration()
    {
        int current = ++generation;
        return action => post(() => { if (!disposed && generation == current) action(); });
    }
    internal void Dispose() => disposed = true;
}
