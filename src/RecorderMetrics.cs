namespace F10Recorder
{
    // Measurements from encoder progress and owned processes, not GPU-wide estimates.
    public sealed class RecorderMetrics
    {
        public string SampledUtc;
        public int CaptureProcessId;
        public int WorkerProcessId;
        public double CaptureProgressSeconds;
        public int ConfiguredCaptureRateLimit;
        public int ConfiguredOutputFramesPerSecond;
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
        // Missing fields from an older worker never imply measured output demand.
        public string ReplayOutputDemandKind = "Unmeasured";
        public string ReplayOutputDemandRevision;
        public string ReplayOutputDemandReason = "Output/finalization calibration is not available for this recording profile.";
        public string ReplayStorageError;
        public string ReplayStorageSafetyKind = "Unmeasured";
        public string ReplayStorageSafetyReason = "Capture poll and stop growth have not been qualified for this recording profile.";
        public bool ReplayStorageSafetyHeld;
        public bool ReplayStorageSafetyWarning;
        public int Recoveries;
        public double LastSaveMilliseconds;
        public long LastSaveBytes;
        public bool LastSaveSucceeded;
    }
}
