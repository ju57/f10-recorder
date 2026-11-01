using System.Diagnostics;
using System.Text;

namespace F10Recorder;

internal static class BoundedCalibrationTraceChecks
{
    internal sealed class WriteControlStream(Stream inner, Action beforeWrite) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) { beforeWrite(); inner.Write(buffer, offset, count); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    static bool Rejects(Action action) { try { action(); return false; } catch (IOException) { return true; } }
    internal static object Run()
    {
        var receipts = new List<object>();
        using (var blocked = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var bytes = new MemoryStream(); int writes = 0;
            using var trace = new BoundedCalibrationTrace(null, () => new WriteControlStream(bytes, () =>
            { if (Interlocked.Increment(ref writes) == 1) { blocked.Set(); Require(release.Wait(3000), "Stall control release timed out."); } }));
            trace.Append("first"); Require(blocked.Wait(1000), "Controlled writer did not block.");
            var timer = Stopwatch.StartNew();
            double enqueueMilliseconds = 0;
            try { for (int n = 0; n < 10; n++) trace.Append("row" + n); enqueueMilliseconds = timer.Elapsed.TotalMilliseconds; }
            finally { Thread.Sleep(650); release.Set(); }
            Require(enqueueMilliseconds < 500, "Enqueue waited for the stalled writer.");
            trace.CompleteAndCheck();
            Require(trace.MaximumWriteMilliseconds >= 600, "Writer stall was not exercised.");
            string expected = string.Join(Environment.NewLine, new[] { "first" }.Concat(Enumerable.Range(0, 10).Select(n => "row" + n))) + Environment.NewLine;
            Require(Encoding.UTF8.GetString(bytes.ToArray()) == expected, "Trace rows were lost, reordered or changed.");
            receipts.Add(new { name = "stalled-writer-lossless-drain", enqueueMilliseconds, trace = trace.Receipt });
        }
        using (var blocked = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            int writes = 0;
            var trace = new BoundedCalibrationTrace(null, () => new WriteControlStream(new MemoryStream(), () =>
            { if (Interlocked.Increment(ref writes) == 1) { blocked.Set(); Require(release.Wait(3000), "Overflow control release timed out."); } }), capacity: 1);
            try
            {
                trace.Append("first"); Require(blocked.Wait(1000), "Overflow writer did not block."); trace.Append("second");
                var timer = Stopwatch.StartNew(); bool rejected = Rejects(() => trace.Append("overflow"));
                Require(rejected && timer.ElapsedMilliseconds < 500 && trace.Failure != null, "Overflow did not fail without blocking.");
            }
            finally { release.Set(); Require(Rejects(trace.CompleteAndCheck), "Overflow incorrectly qualified."); receipts.Add(new { name = "overflow-invalidates", trace = trace.Receipt }); Require(Rejects(trace.Dispose), "Sticky overflow disappeared."); }
        }
        {
            using var failed = new ManualResetEventSlim();
            var trace = new BoundedCalibrationTrace(null, () => new WriteControlStream(new MemoryStream(), () => { failed.Set(); throw new IOException("injected trace writer failure"); }));
            Rejects(() => trace.Append("fault")); Require(failed.Wait(1000), "Writer exception control did not execute.");
            Require(Rejects(trace.CompleteAndCheck), "Writer exception incorrectly qualified.");
            receipts.Add(new { name = "writer-error-invalidates", trace = trace.Receipt }); Require(Rejects(trace.Dispose), "Sticky writer error disappeared.");
        }
        return new { result = "passed", receipts };
    }
}
