using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace F10Recorder;

internal sealed record CalibrationValidationWorkTiming(long Started, long Completed, int GuardCalls, bool Joined, string Error);

// Qualification-only observer work. It must not consume the product guard's
// freshness window without servicing that SAME guard. Never renew a timestamp
// or move this work beyond validation/publication to make a test pass.
internal static class CalibrationValidationWork
{
    internal sealed class GuardContext
    {
        internal Action Current { get; private set; }
        internal IDisposable Enter(Action guard) => new Scope(this, guard);
        sealed class Scope : IDisposable
        {
            readonly GuardContext owner; readonly Action previous; bool disposed;
            internal Scope(GuardContext owner, Action guard)
            { this.owner = owner; previous = owner.Current; owner.Current = guard; }
            public void Dispose() { if (disposed) return; owner.Current = previous; disposed = true; }
        }
    }
    internal static void Run(Action work, Action guard, Action<CalibrationValidationWorkTiming> observed = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        long started = Stopwatch.GetTimestamp(); Exception failure = null; int checks = 0; bool joined = true;
        Task pending = null;
        void Check() { checks++; guard(); }
        try
        {
            if (guard == null) work();
            else
            {
                Check(); // Reject already-expired evidence; never rebase its clock.
                pending = Task.Run(work); joined = false;
                try
                {
                    while (!pending.IsCompleted)
                    {
                        Thread.Sleep(25);
                        if (!pending.IsCompleted) Check();
                        if (Stopwatch.GetElapsedTime(started).TotalSeconds > 5)
                            throw new IOException("Qualification observer exceeded its five-second work deadline.");
                    }
                }
                catch (Exception error) { failure = error; }
                // A failed guard cannot leave a native observer task using an
                // input after the caller unwinds. Always join before returning.
                try { pending.GetAwaiter().GetResult(); }
                catch (Exception error) { failure ??= error; }
                finally { joined = pending.IsCompleted; }
                if (failure == null && Stopwatch.GetElapsedTime(started).TotalSeconds > 5)
                    failure = new IOException("Qualification observer exceeded its five-second work deadline.");
                if (failure == null) Check();
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { observed?.Invoke(new(started, Stopwatch.GetTimestamp(), checks, joined, failure?.ToString())); }
            catch (Exception) when (failure != null) { } // Diagnostics never replace the primary guard/observer failure.
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
