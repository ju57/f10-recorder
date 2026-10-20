using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace F10Recorder
{
    internal sealed class MediaInstallation
    {
        public string Executable;
        public string Version;
        public string Source;
        public string Sha256;
        public string PackageUrl;
    }
    internal static class MediaDependency
    {
        internal const string PackageVersion = "9.0.2";
        internal const string PackageUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip";
        internal const string PackageSha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";
        internal static string RootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F10Recorder");
        static readonly object Gate = new object();
        static string cacheRoot, cacheExecutable;
        static long cacheLength, cacheWriteTicks;

        internal static string ResolveExecutable()
        {
            lock (Gate)
            {
                if (cacheRoot == RootDirectory && File.Exists(cacheExecutable))
                {
                    var file = new FileInfo(cacheExecutable);
                    if (file.Length == cacheLength && file.LastWriteTimeUtc.Ticks == cacheWriteTicks) return cacheExecutable;
                }
                var installation = FindCompatible(RootDirectory, DiscoverCandidates(), true);
                if (installation == null) throw new FileNotFoundException("The recording engine is unavailable. Run F10 Recorder's installer to repair it.");
                Remember(installation); return installation.Executable;
            }
        }
        static void Remember(MediaInstallation installation)
        {
            var file = new FileInfo(installation.Executable); cacheRoot = RootDirectory; cacheExecutable = file.FullName; cacheLength = file.Length; cacheWriteTicks = file.LastWriteTimeUtc.Ticks;
        }
        internal static IEnumerable<string> DiscoverCandidates()
        {
            var paths = new List<string>();
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (!string.IsNullOrWhiteSpace(directory)) try { paths.Add(Path.Combine(directory.Trim().Trim('"'), "ffmpeg.exe")); } catch (ArgumentException) { }
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"));
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ffmpeg", "bin", "ffmpeg.exe"));
            paths.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            paths.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
            paths.Add(Path.Combine(local, "Programs", "ffmpeg", "bin", "ffmpeg.exe"));
            string scoop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps");
            foreach (string name in new[] { "ffmpeg", "ffmpeg-essentials" }) paths.Add(Path.Combine(scoop, name, "current", "bin", "ffmpeg.exe"));
            string winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(winget))
            {
                foreach (string package in Directory.GetDirectories(winget, "Gyan.FFmpeg*"))
                {
                    paths.Add(Path.Combine(package, "bin", "ffmpeg.exe"));
                    foreach (string child in Directory.GetDirectories(package)) paths.Add(Path.Combine(child, "bin", "ffmpeg.exe"));
                }
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray();
        }
        internal static MediaInstallation FindCompatible(string root, IEnumerable<string> candidates, bool includePrivate)
        {
            var all = new List<string>(candidates);
            // Installer-owned metadata is separate from user recording preferences.
            string manifest = Path.Combine(root, "media-engine.json");
            try { if (File.Exists(manifest)) { var saved = new JavaScriptSerializer().Deserialize<MediaInstallation>(File.ReadAllText(manifest)); if (saved != null && !string.IsNullOrEmpty(saved.Executable) && (includePrivate || !IsPrivate(root, saved.Executable))) all.Add(saved.Executable); } } catch (Exception ex) { Log.Write("Media engine metadata ignored: " + ex.Message); }
            if (includePrivate && Directory.Exists(Path.Combine(root, "runtime"))) all.AddRange(Directory.GetFiles(Path.Combine(root, "runtime"), "ffmpeg.exe", SearchOption.AllDirectories).Where(path => !path.Contains(Path.DirectorySeparatorChar + ".install-")));
            foreach (string candidate in all.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string version;
                if (!TryValidate(candidate, out version)) continue;
                return new MediaInstallation { Executable = Path.GetFullPath(candidate), Version = version, Source = IsPrivate(root, candidate) ? "private" : "existing", Sha256 = Hash(candidate), PackageUrl = IsPrivate(root, candidate) ? PackageUrl : null };
            }
            return null;
        }
        static bool IsPrivate(string root, string path) { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.Combine(root, "runtime")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        internal static bool TryValidate(string path, out string version)
        {
            version = null;
            try
            {
                if (!File.Exists(path) || !Path.GetFileName(path).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) return false;
                // Reject malformed/non-console carriers before Windows can display a loader error dialog.
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    if (reader.BaseStream.Length < 128 || reader.ReadUInt16() != 0x5A4D) return false;
                    reader.BaseStream.Position = 60; int pe = reader.ReadInt32();
                    if (pe < 64 || pe > reader.BaseStream.Length - 96) return false;
                    reader.BaseStream.Position = pe; if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) return false;
                    reader.BaseStream.Position = pe + 24 + 68; if (reader.ReadUInt16() != 3) return false;
                }
                string output;
                if (!Run(path, "-version", out output) || !output.StartsWith("ffmpeg version ")) return false;
                version = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).First();
                if (!Run(path, "-filters", out output) || !SupportsCaptureFilters(output)) return false;
                if (!Run(path, "-encoders", out output) || !HasWord(output, "h264_nvenc") || !HasWord(output, "aac") || !HasWord(output, "png")) return false;
                if (!Run(path, "-muxers", out output) || !new[] { "segment", "mp4", "mpegts", "matroska" }.All(name => HasWord(output, name))) return false;
                if (!Run(path, "-demuxers", out output) || !new[] { "concat", "matroska", "lavfi" }.All(name => HasWord(output, name))) return false;
                if (!Run(path, "-bsfs", out output) || !HasWord(output, "setts")) return false;
                if (!Run(path, new[] { "-h", "bsf=setts" }, out output) || !HasWord(output, "prescale")) return false;
                return true;
            }
            catch (Exception ex) { Log.Write("FFmpeg candidate rejected: " + path + "; " + ex.Message); return false; }
        }
        internal static bool SupportsCaptureFilters(string output)
        {
            // Match filter names in inventory rows, not words in descriptions.
            return new[] { "ddagrab", "aresample", "hwdownload", "fps", "setpts" }.All(name => Regex.IsMatch(output, @"(?m)^\s*[A-Z.]+\s+" + Regex.Escape(name) + @"\s+[AVN|]+->[AVN|]+\s+"));
        }
        static bool HasWord(string output, string word) { return Regex.IsMatch(output, @"\b" + Regex.Escape(word) + @"\b"); }
        static bool Run(string path, string option, out string output)
        {
            return Run(path, new[] { option }, out output);
        }
        static bool Run(string path, IEnumerable<string> options, out string output)
        {
            using (var process = CaptureSession.StartProcess(path, new[] { "-hide_banner" }.Concat(options), Path.GetDirectoryName(Path.GetFullPath(path))))
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(8000)) { process.Kill(); process.WaitForExit(); output = ""; return false; }
                output = stdout.Result + stderr.Result; return process.ExitCode == 0;
            }
        }
        internal static int Install(string root, bool allowDownload)
        {
            try
            {
                var installation = Ensure(root, DiscoverCandidates(), allowDownload, new Uri(PackageUrl), PackageSha256, Download);
                // Remove the old editable engine location while preserving recording preferences.
                string preferences = Path.Combine(root, "settings.json");
                if (File.Exists(preferences))
                {
                    var fields = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(preferences));
                    if (fields != null && fields.ContainsKey("FfmpegPath")) { fields.Remove("FfmpegPath"); string temporary = preferences + ".tmp"; File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(fields)); File.Replace(temporary, preferences, null); }
                }
                Log.Write("Media engine ready (" + installation.Source + "): " + installation.Executable); return 0;
            }
            catch (Exception ex) { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "media-install-error.txt"), ex.Message); Log.Write(ex.ToString()); return 1; }
        }
        internal static MediaInstallation DownloadForVerification(string root)
        {
            return Ensure(root, new string[0], true, new Uri(PackageUrl), PackageSha256, Download);
        }
        internal static MediaInstallation Ensure(string root, IEnumerable<string> candidates, bool allowDownload, Uri package, string expectedHash, Action<Uri, string> acquire)
        {
            Directory.CreateDirectory(root);
            var found = FindCompatible(root, candidates, true);
            if (found != null) { SaveManifest(root, found); return found; }
            if (!allowDownload) throw new FileNotFoundException("No compatible FFmpeg installation was found.");
            string runtime = Path.Combine(root, "runtime"); Directory.CreateDirectory(runtime);
            string staging = Path.Combine(runtime, ".install-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
            try
            {
                string archive = Path.Combine(staging, "package.zip"); acquire(package, archive);
                if (!Hash(archive).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("The downloaded recording engine failed checksum verification.");
                string extracted = Path.Combine(staging, "extracted"); ExtractArchive(archive, extracted);
                var installation = FindCompatible(staging, Directory.GetFiles(extracted, "ffmpeg.exe", SearchOption.AllDirectories), false);
                if (installation == null) throw new IOException("The downloaded FFmpeg package does not support this recorder.");
                // Use a unique private directory so a failed update never overwrites a working engine.
                string destination = Path.Combine(runtime, "ffmpeg-" + PackageVersion + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string relative = installation.Executable.Substring(Path.GetFullPath(extracted).Length + 1);
                CheckOwned(runtime, extracted); CheckOwned(runtime, destination); Directory.Move(extracted, destination);
                installation.Executable = Path.Combine(destination, relative); installation.Source = "private"; installation.PackageUrl = package.AbsoluteUri;
                SaveManifest(root, installation); return installation;
            }
            finally { CheckOwned(runtime, staging); CaptureSession.TryDeleteDirectory(staging); }
        }
        static void SaveManifest(string root, MediaInstallation installation)
        {
            string path = Path.Combine(root, "media-engine.json"), temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(installation));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            CaptureSession.TryDeleteFile(Path.Combine(root, "media-install-error.txt"));
        }
        internal static string Hash(string path) { using (var sha = SHA256.Create()) using (var file = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); }
        static void CheckOwned(string root, string target) { if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Media package path is outside its installation directory."); }
        internal static void ExtractArchive(string archive, string destination)
        {
            Directory.CreateDirectory(destination); long total = 0;
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > 5000) throw new IOException("Media package contains too many files.");
                foreach (var entry in zip.Entries)
                {
                    total += entry.Length; if (total > 1024L * 1024 * 1024) throw new IOException("Media package is too large.");
                    string path = Path.GetFullPath(Path.Combine(destination, entry.FullName)); CheckOwned(destination, path);
                    if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new IOException("Media package contains a symbolic link.");
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)); using (var input = entry.Open()) using (var output = File.Create(path)) input.CopyTo(output);
                }
            }
        }
        static void Download(Uri uri, string destination)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new DownloadClient()) { client.Headers[HttpRequestHeader.UserAgent] = "F10Recorder-Setup/1.0"; client.DownloadFile(uri, destination); }
        }
        sealed class DownloadClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address) { var request = base.GetWebRequest(address); request.Timeout = 120000; return request; }
        }
    }
}
