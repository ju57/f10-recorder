using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace F10Recorder
{
    internal sealed class RecordingEngineCapabilities
    {
        public string CheckedUtc;
        public string EngineSha256;
        public string PackageSha256;
        public string PackageVersion;
        public EncoderOption[] Encoders;
    }
    public sealed class EncoderOption
    {
        public string Codec;
        public string Name;
        public override string ToString() { return Name; }
    }
    internal static class EncoderCapabilities
    {
        internal static RecordingEngineCapabilities ProbeManaged()
        {
            string executable = MediaDependency.ResolveExecutable();
            using (MediaDependency.OpenVerifiedExecutable(executable))
            {
                var encoders = Probe(executable);
                // A receipt describes this authenticated, bounded initialization check,
                // not qualification of every resolution, rate or capture source.
                return new RecordingEngineCapabilities { CheckedUtc = DateTime.UtcNow.ToString("o"),
                    EngineSha256 = MediaDependency.ExecutableSha256, PackageSha256 = MediaDependency.PackageSha256,
                    PackageVersion = MediaDependency.PackageVersion, Encoders = encoders.ToArray() };
            }
        }
        internal static readonly EncoderOption[] Choices = {
            new EncoderOption { Codec = "h264_nvenc", Name = "H.264 — broad playback compatibility" },
            new EncoderOption { Codec = "hevc_nvenc", Name = "HEVC / H.265 — smaller files" },
            new EncoderOption { Codec = "av1_nvenc", Name = "AV1 — efficient, requires a modern player" }
        };
        internal static List<EncoderOption> Probe(string executable)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("Run the installer to repair F10 Recorder's media engine.");
            // Test initialization on the actual GPU; a compiled encoder list alone does not establish hardware support.
            var results = Choices.Select(choice => Task.Run(delegate { return CanInitialize(executable, choice) ? choice : null; })).ToArray();
            Task.WaitAll(results);
            return results.Select(task => task.Result).Where(choice => choice != null).ToList();
        }
        static bool CanInitialize(string executable, EncoderOption option)
        {
            using (var process = CaptureSession.StartProcess(executable, new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=256x256:rate=30", "-frames:v", "1", "-c:v", option.Codec, "-preset", "p3", "-f", "null", "-" }, Path.GetDirectoryName(executable)))
            {
                Task<string> error = process.StandardError.ReadToEndAsync();
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(10000)) { process.Kill(); process.WaitForExit(); Log.Write("Codec probe timed out: " + option.Codec); return false; }
                string detail = error.Result; output.Wait();
                if (process.ExitCode != 0) Log.Write("Codec unavailable: " + option.Codec + " " + detail.Trim());
                return process.ExitCode == 0;
            }
        }
    }
}
