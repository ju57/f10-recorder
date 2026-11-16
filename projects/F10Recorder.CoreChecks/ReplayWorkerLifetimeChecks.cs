using F10Recorder;

internal static class ReplayWorkerLifetimeChecks
{
    sealed class Result : IDisposable { public void Dispose() { } }
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }

    internal static void Run()
    {
        foreach (bool fail in new[] { false, true })
        {
            var lifetime = new ReplayWorkerLifetime();
            var timing = new ReplayPacketWorkTiming();
            var injected = new IOException("Expected owner failure.");
            int accepted = 0, guards = 0;
            try
            {
                ReplayPacketWork.Run(new[] { 0, 1, 2 }, 2, 1024,
                    (_, context) => { context.Check(); return new Result(); }, _ => 1,
                    (_, pump) => { pump(); accepted++; }, () => {
                        guards++;
                        timing.Joined = true;
                        timing.OwnerThread = -1;
                        Require(lifetime.Started && !lifetime.Joined, "Diagnostic mutation released active packet work.");
                        if (fail) throw injected;
                    }, timing, lifetime: lifetime);
                Require(!fail && accepted == 3, "Packet work lost ordered results.");
            }
            catch (IOException error) when (fail && ReferenceEquals(error, injected)) { }
            timing.Joined = false;
            Require(guards > 0 && lifetime.Joined, "Diagnostic erasure changed the actual packet join.");
        }

        byte[] bytes = ReplayTransportChecks.Fixture();
        using var source = new MemoryStream(bytes);
        var indexed = new ReplayWorkerLifetime();
        var batch = new ReplayTransportBatchTiming();
        ReplayTransportBatchIndex.Read([new(source, bytes.Length)], 60, true, 1, () => {
            batch.Joined = true;
            Require(indexed.Started && !indexed.Joined, "Diagnostic mutation released active index readers.");
        }, batch, lifetime: indexed);
        batch.Joined = false;
        Require(indexed.Joined && source.CanRead, "Index join depended on diagnostics or closed the caller's stream.");

        var uncertain = new ReplayWorkerLifetime();
        uncertain.Begin(); uncertain.Complete(false); uncertain.Complete(true);
        Require(!uncertain.Joined, "A later report erased unconfirmed worker closure.");
        Console.WriteLine("Passed: actual index/packet joins are independent of mutable diagnostics; uncertainty stays sticky.");
    }
}
