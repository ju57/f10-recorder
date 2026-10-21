using System;
using System.IO;
using System.Threading.Tasks;

namespace F10Recorder
{
    internal static class ScreenshotCapture
    {
        internal static string Capture(int displayIndex, string outputDirectory)
        {
            return CaptureSource("ddagrab=output_idx=" + (displayIndex < 0 ? Native.PrimaryOutputIndex() : displayIndex) + ":framerate=1:draw_mouse=0", outputDirectory, true);
        }
        internal static bool SameTarget(WindowTargetSnapshot expected, WindowTargetSnapshot actual)
        {
            return expected != null && actual != null && expected.Available && actual.Available && expected.Generation == actual.Generation
                && expected.Handle == actual.Handle && expected.ProcessId == actual.ProcessId && expected.ProcessStartedUtcTicks == actual.ProcessStartedUtcTicks;
        }
        static void RequireSource(Func<bool> valid)
        {
            if (valid != null && !valid()) throw new IOException("The selected window changed. No screenshot was saved.");
        }
        internal static string CaptureSource(string source, string outputDirectory, bool hardware, Func<bool> sourceStillValid = null)
        {
            outputDirectory = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(outputDirectory);
            string name = "Screenshot-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string temporary = Path.Combine(outputDirectory, "." + name + ".partial.png"), destination = Path.Combine(outputDirectory, name + ".png");
            try
            {
                RequireSource(sourceStillValid);
                var arguments = new System.Collections.Generic.List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", source };
                if (hardware) arguments.AddRange(new[] { "-vf", "hwdownload,format=bgra" });
                arguments.AddRange(new[] { "-frames:v", "1", "-c:v", "png", "-f", "image2", "-update", "1", "-y", temporary });
                using (var job = new Native.ProcessJob())
                using (var process = CaptureSession.StartProcess(MediaDependency.ResolveExecutable(), arguments, outputDirectory))
                {
                    job.Add(process); Task<string> error = process.StandardError.ReadToEndAsync(), output = process.StandardOutput.ReadToEndAsync();
                    var deadline = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        while (!process.WaitForExit(50))
                        {
                            RequireSource(sourceStillValid);
                            if (deadline.ElapsedMilliseconds >= 15000) throw new TimeoutException("Screenshot capture timed out. Check that the display is available.");
                        }
                    }
                    catch { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } throw; }
                    string detail = error.Result; var ignored = output.Result;
                    if (process.ExitCode != 0 || !File.Exists(temporary)) throw new IOException("Screenshot capture failed. " + detail.Trim());
                }
                ValidatePng(temporary);
                RequireSource(sourceStillValid);
                File.Move(temporary, destination); Log.Write("Screenshot saved: " + destination); return destination;
            }
            finally { CaptureSession.TryDeleteFile(temporary); }
        }
        static void ValidatePng(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                byte[] header = reader.ReadBytes(24);
                byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 };
                if (stream.Length < 45 || header.Length != 24) throw new IOException("Screenshot has no image data.");
                for (int i = 0; i < signature.Length; i++) if (header[i] != signature[i]) throw new IOException("Screenshot is not a PNG image.");
                uint width = (uint)header[16] << 24 | (uint)header[17] << 16 | (uint)header[18] << 8 | header[19];
                uint height = (uint)header[20] << 24 | (uint)header[21] << 16 | (uint)header[22] << 8 | header[23];
                if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue) throw new IOException("Screenshot has invalid dimensions.");
            }
        }
    }
}
