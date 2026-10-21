using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Web.Script.Serialization;

namespace F10Recorder
{
    internal static class DependencyVerification
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        static void Rejected(Action action)
        {
            bool rejected = false;
            try { action(); } catch (IOException) { rejected = true; } catch (System.ComponentModel.Win32Exception) { rejected = true; }
            Require(rejected, "An unapproved executable was accepted.");
        }
        internal static int Run(string data, bool download, string package = null)
        {
            TrustedStorage.CreatePrivateDirectory(data); RecorderHost.Initialize(data);
            var report = new Dictionary<string, object>();
            string originalRoot = MediaDependency.RootDirectory, originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                string rows = " ... ddagrab |->V Capture\n ... aresample A->A Audio\n ... hwdownload V->V Download\n ... setpts V->V Timestamp\n";
                Require(!MediaDependency.SupportsCaptureFilters(rows + " ... vfrdet V->V Reports fps\n") && MediaDependency.SupportsCaptureFilters(rows + " ... fps V->V Pace\n"), "Filter inventory accepted a description instead of the FPS filter.");
                report["filterInventory"] = true;
                MediaDependency.RootDirectory = Path.Combine(data, "managed");
                string untrusted = Path.Combine(data, "profile", "runtime", "ffmpeg.exe"), marker = Path.Combine(data, "executed.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(untrusted));
                // A runnable modern apphost fixture makes rejection-before-execution meaningful.
                foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
                    File.Copy(file, Path.Combine(Path.GetDirectoryName(untrusted), Path.GetFileName(file)));
                File.Copy(Environment.ProcessPath, untrusted);
                var fixtureInfo = new ProcessStartInfo(untrusted) { UseShellExecute = false, CreateNoWindow = true };
                fixtureInfo.ArgumentList.Add("--dependency-marker"); fixtureInfo.ArgumentList.Add(marker);
                using (var fixture = Process.Start(fixtureInfo))
                {
                    try { Require(fixture.WaitForExit(10000) && fixture.ExitCode == 0 && File.Exists(marker), "Untrusted marker fixture was not executable."); }
                    finally { if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(); } }
                }
                File.Delete(marker);
                report["untrustedMarkerFixtureExecutable"] = true;
                string preferences = Path.Combine(data, "profile", "settings.json"); File.WriteAllText(preferences, "{\"ReplayMinutes\":5}");
                string manifest = Path.Combine(data, "profile", "media-engine.json");
                File.WriteAllText(manifest, new JavaScriptSerializer().Serialize(new MediaInstallation { Executable = untrusted, Sha256 = MediaDependency.Hash(untrusted) }));
                Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(untrusted) + Path.PathSeparator + ".");
                Rejected(delegate { MediaDependency.ResolveExecutable(); });
                Rejected(delegate { using (var process = CaptureSession.StartProcess(untrusted, new[] { "--dependency-marker", marker }, data)) process.WaitForExit(); });
                Require(!File.Exists(marker), "An untrusted engine executed before verification.");
                report["pathProfileManifestAndDirectLaunchRejectedBeforeExecution"] = true;
                string version;
                Require(!MediaDependency.TryValidate(untrusted, out version), "An external compatibility probe ran.");
                TrustedStorage.CreatePrivateDirectory(MediaDependency.RootDirectory);
                Rejected(delegate { MediaDependency.Ensure(false, delegate { throw new Exception("Offline lookup downloaded."); }); });
                Rejected(delegate { MediaDependency.Ensure(true, delegate(Uri uri, string file) { File.WriteAllText(file, "bad package"); }); });
                Require(!File.Exists(MediaDependency.ExecutablePath), "Bad checksum published an engine.");
                report["offlineMissingAndPackageChecksumFailure"] = true;
                string traversal = Path.Combine(data, "traversal.zip");
                using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Create)) using (var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) writer.Write("escape");
                Rejected(delegate { MediaDependency.ExtractArchive(traversal, Path.Combine(data, "extract")); });
                Require(!File.Exists(Path.Combine(data, "escaped.txt")), "Archive traversal escaped staging.");
                report["archiveContainment"] = true;
                if (package == null && !download) throw new IOException("Approved-package success checks need --package with the pinned archive, or --download.");
                var installed = package == null ? MediaDependency.DownloadForVerification(data)
                    : MediaDependency.Ensure(true, delegate(Uri uri, string file) { File.Copy(package, file); });
                Require(installed.Sha256 == MediaDependency.ExecutableSha256, "Installed engine was not approved.");
                string packageRoot = Path.GetDirectoryName(Path.GetDirectoryName(installed.Executable));
                Require(Directory.GetFiles(packageRoot, "LICENSE*").Length > 0, "The package license was not retained.");
                using (var probe = CaptureSession.StartProcess(Path.Combine(Path.GetDirectoryName(installed.Executable), "ffprobe.exe"), new[] { "-version" }, data))
                { var output = probe.StandardOutput.ReadToEndAsync(); var error = probe.StandardError.ReadToEndAsync(); Require(probe.WaitForExit(5000) && probe.ExitCode == 0, "The approved inspection tool did not run."); output.Wait(); error.Wait(); }
                report["approvedInspectionToolAndPackageNotices"] = true;
                var reused = MediaDependency.Ensure(false, delegate { throw new Exception("Verified engine downloaded again."); });
                Require(reused.Executable == installed.Executable, "Verified offline engine was not reused.");
                report["approvedInstallAndOfflineReuse"] = true;
                using (var lease = MediaDependency.OpenVerifiedExecutable(installed.Executable))
                {
                    Rejected(delegate { using (File.Open(installed.Executable, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } });
                    Rejected(delegate { Directory.Move(Path.GetDirectoryName(installed.Executable), Path.GetDirectoryName(installed.Executable) + "-moved"); });
                }
                report["verificationLeasePreventsWriteAndRename"] = true;
                DateTime written = File.GetLastWriteTimeUtc(installed.Executable);
                using (var file = new FileStream(installed.Executable, FileMode.Open, FileAccess.ReadWrite)) { int first = file.ReadByte(); file.Position = 0; file.WriteByte((byte)(first ^ 1)); }
                File.SetLastWriteTimeUtc(installed.Executable, written);
                Rejected(delegate { MediaDependency.ResolveExecutable(); });
                Rejected(delegate { using (var process = CaptureSession.StartProcess(installed.Executable, new[] { "-version" }, data)) process.WaitForExit(); });
                Require(!MediaDependency.TryValidate(installed.Executable, out version), "Modified engine passed a probe.");
                report["sameLengthSameTimestampTamperingRejected"] = true;
                if (package == null) MediaDependency.DownloadForVerification(data);
                else MediaDependency.Ensure(true, delegate(Uri uri, string file) { File.Copy(package, file); });
                Require(MediaDependency.ResolveExecutable() == installed.Executable, "Explicit repair did not restore the approved engine.");
                Require(File.ReadAllText(preferences) == "{\"ReplayMinutes\":5}" && File.Exists(untrusted) && File.Exists(manifest), "Setup changed the old profile.");
                report["explicitRepairAndOldProfilePreservation"] = true;
                report["mediaEngine"] = installed.Executable;
                report["result"] = "passed";
                return 0;
            }
            catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); return 1; }
            finally
            {
                MediaDependency.RootDirectory = originalRoot; Environment.SetEnvironmentVariable("PATH", originalPath);
                File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report));
            }
        }
    }
}
