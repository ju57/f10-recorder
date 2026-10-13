namespace F10Recorder
{
    // Measurements from encoder progress and owned processes, not GPU-wide estimates.
    public sealed class RecorderMetrics
    {
        public string SampledUtc;
        public int CaptureProcessId;
        public int WorkerProcessId;
        public double CaptureProgressSeconds;
        public double BufferedSeconds;
        public long EncodedFrames;
        public long EncoderReportedDrops;
        public long EncoderReportedDuplicates;
        public double EncodingSpeed;
        public double CaptureCpuPercent;
        public long CaptureWorkingSetBytes;
        public long WorkerWorkingSetBytes;
        public double WorkerCpuPercent;
        public long UiWorkingSetBytes;
        public double UiCpuPercent;
        public long CacheBytes;
        public int Recoveries;
        public double LastSaveMilliseconds;
        public long LastSaveBytes;
        public bool LastSaveSucceeded;
    }
}
