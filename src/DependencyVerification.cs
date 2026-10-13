using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Web.Script.Serialization;

namespace F10Recorder
{
    internal static class DependencyVerification
    {
        internal static int Run(string data, bool download)
        {
            var report = new Dictionary<string, object>();
            try
            {
                string existing = MediaDependency.ResolveExecutable();
                string originalHash = MediaDependency.Hash(existing);
                bool acquired = false;
                var reused = MediaDependency.Ensure(Path.Combine(data, "reuse"), new[] { existing }, true, new Uri(MediaDependency.PackageUrl), MediaDependency.PackageSha256, delegate { acquired = true; throw new Exception("Existing FFmpeg should prevent downloading."); });
                if (acquired || reused.Source != "existing" || reused.Executable != existing) throw new Exception("Compatible existing engine was not reused.");
                report["existingReuseWithoutDownload"] = true;
                string invalid = Path.Combine(data, "invalid", "ffmpeg.exe"); Directory.CreateDirectory(Path.GetDirectoryName(invalid)); File.WriteAllText(invalid, "not an executable");
                string version;
                if (MediaDependency.TryValidate(invalid, out version)) throw new Exception("An invalid executable was accepted.");
                report["invalidCandidateRejected"] = true;
                var fallback = MediaDependency.Ensure(Path.Combine(data, "skip-invalid"), new[] { invalid, existing }, false, new Uri(MediaDependency.PackageUrl), MediaDependency.PackageSha256, delegate { throw new Exception("Unexpected download."); });
                if (fallback.Executable != existing) throw new Exception("Invalid candidate blocked reuse of a valid installation.");
                report["invalidCandidateSkipped"] = true;
                bool failed = false;
                try { MediaDependency.Ensure(Path.Combine(data, "bad-checksum"), new string[0], true, new Uri(MediaDependency.PackageUrl), MediaDependency.PackageSha256, delegate(Uri uri, string path) { File.WriteAllText(path, "incomplete package"); }); }
                catch (IOException) { failed = true; }
                if (!failed || File.Exists(Path.Combine(data, "bad-checksum", "media-engine.json"))) throw new Exception("Checksum failure published an installation.");
                report["checksumFailureRejected"] = true;
                string traversal = Path.Combine(data, "traversal.zip");
                using (var archive = ZipFile.Open(traversal, ZipArchiveMode.Create)) { using (var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open())) writer.Write("must not escape"); }
                failed = false;
                try { MediaDependency.ExtractArchive(traversal, Path.Combine(data, "extracted")); } catch (IOException) { failed = true; }
                if (!failed || File.Exists(Path.Combine(data, "escaped.txt"))) throw new Exception("Archive traversal was not rejected.");
                report["archiveBoundaryEnforced"] = true;
                var settings = new Settings(); string preferenceFile = Path.Combine(data, "preference-test.json"); settings.Save(preferenceFile);
                if (File.ReadAllText(preferenceFile).Contains("FfmpegPath")) throw new Exception("The recording preferences still expose an editable FFmpeg path.");
                report["pathRemovedFromPreferences"] = true;
                if (download)
                {
                    var installed = MediaDependency.DownloadForVerification(Path.Combine(data, "clean-install"));
                    if (installed.Source != "private") throw new Exception("Missing-engine installation did not produce a private runtime.");
                    var codecs = EncoderCapabilities.Probe(installed.Executable);
                    if (!codecs.Any(codec => codec.Codec == "h264_nvenc")) throw new Exception("Downloaded engine could not initialize H.264 on this GPU.");
                    bool redownloaded = false;
                    var again = MediaDependency.Ensure(Path.Combine(data, "clean-install"), new string[0], true, new Uri(MediaDependency.PackageUrl), MediaDependency.PackageSha256, delegate { redownloaded = true; throw new Exception("Already installed runtime was downloaded again."); });
                    if (redownloaded || again.Executable != installed.Executable) throw new Exception("Private engine was not reused on repair.");
                    report["cleanInstallDownloadAndValidation"] = true; report["privateRuntimeReused"] = true; report["downloadedEngine"] = installed; report["downloadedCodecs"] = codecs.Select(codec => codec.Codec).ToArray();
                }
                if (MediaDependency.Hash(existing) != originalHash) throw new Exception("Existing FFmpeg was modified.");
                report["existingBinaryPreserved"] = true;
                report["result"] = "passed"; File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return 0;
            }
            catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return 1; }
        }
    }
}
