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
        // This is never assigned from --data-dir, settings, manifests or PATH.
        internal static string RootDirectory = TrustedStorage.DefaultRoot;
        internal const string ExecutableSha256 = "3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec";
        internal const string ProbeSha256 = "f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52";
        internal static string ExecutablePath { get { return Path.Combine(RootDirectory, "ffmpeg-" + PackageVersion, "bin", "ffmpeg.exe"); } }
        static readonly object Gate = new object();

        internal static string ResolveExecutable()
        {
            lock (Gate)
            {
                try { using (OpenVerifiedExecutable(ExecutablePath)) return ExecutablePath; }
                catch (Exception ex)
                {
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is System.ComponentModel.Win32Exception)) throw;
                    throw new IOException("The approved recording engine is unavailable or failed verification. Choose Repair recording engine from the tray menu.", ex);
                }
            }
        }
        internal static IDisposable OpenVerifiedExecutable(string path)
        {
            string full = Path.GetFullPath(path);
            CheckOwned(RootDirectory, full);
            string name = Path.GetFileName(full);
            string expectedHash = name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ? ExecutableSha256
                : name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase) ? ProbeSha256 : null;
            if (expectedHash == null) throw new IOException("Only approved recording-engine tools may run.");
            var lease = new ExecutableLease();
            try
            {
                lease.Root = TrustedStorage.Acquire(RootDirectory);
                lease.Parents = TrustedStorage.PinDirectories(Path.GetDirectoryName(full));
                lease.File = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                TrustedStorage.ValidateFile(full);
                using (var sha = SHA256.Create())
                    if (!BitConverter.ToString(sha.ComputeHash(lease.File)).Replace("-", "").Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("The recording engine failed verification. Choose Repair recording engine.");
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        sealed class ExecutableLease : IDisposable
        {
            internal IDisposable Root, Parents;
            internal FileStream File;
            public void Dispose() { if (File != null) File.Dispose(); if (Parents != null) Parents.Dispose(); if (Root != null) Root.Dispose(); }
        }
        internal static bool TryValidate(string path, out string version)
        {
            version = null;
            try
            {
                using (OpenVerifiedExecutable(path)) { }
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
        internal static int Install(string root, bool allowDownload, Action<Uri, string> acquire = null)
        {
            try
            {
                var installation = Ensure(allowDownload, acquire ?? Download);
                CaptureSession.TryDeleteFile(Path.Combine(root, "media-install-error.txt"));
                Log.Write("Media engine ready (" + installation.Source + "): " + installation.Executable); return 0;
            }
            catch (Exception ex) { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "media-install-error.txt"), ex.Message); Log.Write(ex.ToString()); return 1; }
        }
        internal static MediaInstallation DownloadForVerification(string root)
        {
            return Ensure(true, Download);
        }
        internal static MediaInstallation Ensure(bool allowDownload, Action<Uri, string> acquire)
        {
            lock (Gate)
            {
                TrustedStorage.CreatePrivateDirectory(RootDirectory);
                using (TrustedStorage.Acquire(RootDirectory))
                using (var setupLock = new FileStream(Path.Combine(RootDirectory, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    string version;
                    if (TryValidate(ExecutablePath, out version)) return Installation(version);
                    if (!allowDownload) throw new FileNotFoundException("The approved recording engine is unavailable. Choose Repair recording engine when online.");
                    string staging = Path.Combine(RootDirectory, ".install-" + Guid.NewGuid().ToString("N"));
                    TrustedStorage.CreatePrivateDirectory(staging);
                    try
                    {
                        string archive = Path.Combine(staging, "package.zip"); acquire(new Uri(PackageUrl), archive);
                        if (!Hash(archive).Equals(PackageSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("The downloaded recording engine failed checksum verification. Retry setup.");
                        string extracted = Path.Combine(staging, "extracted"); ExtractArchive(archive, extracted);
                        string binary = Path.Combine(extracted, "ffmpeg-" + PackageVersion + "-essentials_build", "bin", "ffmpeg.exe");
                        if (!TryValidate(binary, out version)) throw new IOException("The approved FFmpeg package does not support this recorder.");
                        string packageRoot = Path.GetDirectoryName(Path.GetDirectoryName(binary));
                        string installedPackage = Path.Combine(RootDirectory, "ffmpeg-" + PackageVersion);
                        TrustedStorage.CreatePrivateDirectory(installedPackage);
                        string destination = Path.GetDirectoryName(ExecutablePath); TrustedStorage.CreatePrivateDirectory(destination);
                        using (TrustedStorage.PinDirectories(destination))
                        {
                            // Keep the authenticated package's licenses, documentation,
                            // presets and inspection tool alongside its engine.
                            foreach (string file in Directory.GetFiles(packageRoot, "*", SearchOption.AllDirectories))
                            {
                                if (string.Equals(file, binary, StringComparison.OrdinalIgnoreCase)) continue;
                                string target = Path.Combine(installedPackage, file.Substring(packageRoot.Length + 1));
                                CheckOwned(installedPackage, target); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target, true);
                            }
                            // Preserve an existing engine until a fully verified replacement exists.
                            string replacement = Path.Combine(destination, ".replacement-" + Guid.NewGuid().ToString("N"));
                            try { File.Copy(binary, replacement); if (File.Exists(ExecutablePath)) File.Replace(replacement, ExecutablePath, null); else File.Move(replacement, ExecutablePath); }
                            finally { CaptureSession.TryDeleteFile(replacement); }
                        }
                        using (OpenVerifiedExecutable(ExecutablePath)) { }
                        return Installation(version);
                    }
                    finally { CheckOwned(RootDirectory, staging); CaptureSession.TryDeleteDirectory(staging); }
                }
            }
        }
        static MediaInstallation Installation(string version)
        {
            return new MediaInstallation { Executable = ExecutablePath, Version = version, Source = "managed", Sha256 = ExecutableSha256, PackageUrl = PackageUrl };
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
