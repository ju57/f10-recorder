using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace F10Recorder;

// A sealed proof is published only after the child is in its non-breakaway job.
// Record it before creation as unsealed: a crash during launch cannot turn a
// missing job or PID into proof that an unassigned child never existed.
internal sealed record ReplayProducerProof(int WorkerId, long WorkerStartedUtcTicks, string JobName, bool Sealed)
{
    internal const string JobPrefix = @"Global\F10Recorder-Producer-";
    internal static ReplayProducerProof Current(string jobName, bool sealedLaunch)
    {
        using var worker = Process.GetCurrentProcess();
        return new(worker.Id, worker.StartTime.ToUniversalTime().Ticks, jobName, sealedLaunch);
    }
    internal static bool Valid(ReplayProducerProof proof) => proof != null && proof.WorkerId > 0
        && proof.WorkerStartedUtcTicks > 0 && proof.WorkerStartedUtcTicks <= DateTime.MaxValue.Ticks
        && proof.JobName != null && proof.JobName.StartsWith(JobPrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(proof.JobName[JobPrefix.Length..], "N", out _);

    internal bool ConfirmedAbsent()
    {
        if (!Valid(this) || !Sealed) return false;
        try
        {
            try
            {
                using var worker = Process.GetProcessById(WorkerId);
                // Read the identity from this process handle. PID reuse is not
                // confused with the original worker's continuing lifetime.
                if (worker.StartTime.ToUniversalTime().Ticks == WorkerStartedUtcTicks && !worker.HasExited) return false;
            }
            catch (ArgumentException) { /* No process currently has that PID. */ }
            return JobEmpty(JobName);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException)
        { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Accounting
    {
        internal long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out Accounting info, uint size, out uint returned);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static bool JobEmpty(string name)
    {
        IntPtr job = OpenJobObject(4 /* JOB_OBJECT_QUERY */, false, name);
        if (job == IntPtr.Zero) return Marshal.GetLastWin32Error() == 2; // Every other error is uncertainty.
        try
        {
            return QueryInformationJobObject(job, 1, out var info, (uint)Marshal.SizeOf<Accounting>(), out _)
                && info.ActiveProcesses == 0;
        }
        finally { CloseHandle(job); }
    }
}
