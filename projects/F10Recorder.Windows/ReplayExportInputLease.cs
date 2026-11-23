namespace F10Recorder;

// Borrowed by each child. Unknown child exit transfers this strong owner to
// ReplayExportProcess's exact-process retention registry; the save must not
// close its immutable sources merely because the synchronous call threw.
internal sealed class ReplayExportInputLease : IDisposable
{
    readonly List<IDisposable> inputs = [];
    readonly List<IDisposable> dependencies = [];
    internal IReadOnlyList<IDisposable> Inputs => inputs;
    internal bool DependenciesClosed=>dependencies.Count==0;
    internal void Add(IDisposable input) => inputs.Add(input);
    internal void Depend(IDisposable input) => dependencies.Add(input);
    internal void ReleaseDependencies()
    {
        foreach(var dependency in dependencies)dependency.Dispose();
        dependencies.Clear();
    }
    public void Dispose()
    {
        List<Exception> errors = [];
        foreach (var input in inputs)
            try { input.Dispose(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Replay input closure is uncertain.", errors);
        ReleaseDependencies();
    }
}
