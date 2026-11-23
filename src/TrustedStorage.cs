using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace F10Recorder
{
    // Executable and ownership state never inherits trust from a recording profile.
    internal static class TrustedStorage
    {
        internal static string DefaultRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F10Recorder.Security"); } }
        [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        internal static void CreatePrivateDirectory(string path)
        {
            path = Path.GetFullPath(path);
            string parent = Path.GetDirectoryName(path);
            if (!Directory.Exists(parent)) throw new IOException("The private storage parent is unavailable.");
            using (var parents = PinDirectories(parent))
            using (var identity = WindowsIdentity.GetCurrent())
            {
                string sid = identity.User.Value; IntPtr descriptor; uint size;
                if (!ConvertStringSecurityDescriptorToSecurityDescriptor("O:" + sid + "D:P(A;OICI;FA;;;" + sid + ")(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", 1, out descriptor, out size)) throw new Win32Exception();
                try
                {
                    var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = descriptor };
                    if (!CreateDirectory(path, ref attributes) && Marshal.GetLastWin32Error() != 183) throw new Win32Exception();
                }
                finally { LocalFree(descriptor); }
                using (var pinned = PinDirectories(path)) Validate(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            }
        }
        internal static IDisposable Acquire(string root)
        {
            var pinned = PinDirectories(root);
            try { Validate(new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)); return pinned; }
            catch { pinned.Dispose(); throw; }
        }
        internal static void ValidateFile(string path)
        {
            Validate(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Private storage cannot contain reparse points.");
        }
        static void Validate(FileSystemSecurity security)
        {
            if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl == null)
                throw new IOException("Private storage has an unrestricted access list. Repair it in a private user location.");
            using (var identity = WindowsIdentity.GetCurrent())
            {
                string sid = identity.User.Value;
                if (!TrustedSid(security.GetOwner(typeof(SecurityIdentifier)).Value, sid)) throw new IOException("Private storage has an unexpected owner. Repair it in a private user location.");
                const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (rule.AccessControlType == AccessControlType.Allow && !TrustedSid(rule.IdentityReference.Value, sid) && (((int)rule.FileSystemRights & ((int)writes | 0x50000000)) != 0))
                        throw new IOException("Private storage grants write access to another account. Repair it in a private user location.");
            }
        }
        static bool TrustedSid(string value, string current) { return value == current || value == "S-1-5-18" || value == "S-1-5-32-544"; }
        internal static IDisposable PinDirectories(string path)
        {
            return PinDirectories(path, false);
        }
        internal sealed class ProfileDirectoryLease : IDisposable
        {
            SafeFileHandle anchor;
            IDisposable parents;
            internal string DirectoryPath;
            internal ProfileDirectoryLease(string path)
            {
                try
                {
                    anchor = Native.AnchorDirectory(path);
                    DirectoryPath = Native.FinalDirectoryPath(anchor);
                    parents = PinDirectories(DirectoryPath, true);
                    if (Native.DirectoryIdentity(anchor) != Native.DirectoryIdentity(DirectoryPath)
                        || !string.Equals(DirectoryPath, Native.FinalDirectoryPath(anchor), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The recording profile changed while ownership was being established.");
                }
                catch { Dispose(); throw; }
            }
            public void Dispose() { if (parents != null) parents.Dispose(); if (anchor != null) anchor.Dispose(); }
        }
        static IDisposable PinDirectories(string path, bool profile)
        {
            var pinned = new DirectoryPins();
            try
            {
                string full = Path.GetFullPath(path), current = Path.GetPathRoot(full);
                if (!profile && full.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Private storage must be on a local drive.");
                pinned.Add(current);
                foreach (string part in full.Substring(current.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                { current = Path.Combine(current, part); pinned.Add(current); }
                return pinned;
            }
            catch { pinned.Dispose(); throw; }
        }
        sealed class DirectoryPins : IDisposable
        {
            readonly List<SafeFileHandle> handles = new List<SafeFileHandle>();
            internal void Add(string path)
            {
                // Deny rename/deletion while validating and using this path; open the
                // directory itself rather than following a junction to another root.
                // FILE_LIST_DIRECTORY participates in sharing checks. An
                // attributes-only handle does not prevent directory replacement.
                var handle = CreateFile(path, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
                handles.Add(handle);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("A resolved storage path cannot contain reparse points.");
            }
            public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
        }
        sealed class LockLease : IDisposable
        {
            internal IDisposable Storage, Parents;
            internal ProfileDirectoryLease Profile;
            internal FileStream File;
            public void Dispose() { if (File != null) File.Dispose(); if (Profile != null) Profile.Dispose(); if (Parents != null) Parents.Dispose(); if (Storage != null) Storage.Dispose(); }
        }
        internal static bool IsSharingViolation(IOException error) { int code = error.HResult & 65535; return code == 32 || code == 33; }
        // Request-owned directory capabilities; the shared file lock still
        // exists only during one actual ledger operation, never the whole save.
        internal sealed class LockDirectoryLease : IDisposable
        {
            readonly string root,profiles,sid,name,rootIdentity,profilesIdentity;
            readonly IDisposable rootPins;
            readonly ReplayGuardDirectory profilesOwner;
            int active;
            readonly object lifecycle=new();
            bool disposed,uncertain;
            string lockIdentity;
            internal LockDirectoryLease(string root,string purpose)
            {
                this.root=Path.GetFullPath(root);profiles=Path.Combine(this.root,"profiles");
                try
                {
                    CreatePrivateDirectory(this.root);rootPins=Acquire(this.root);
                    rootIdentity=Native.DirectoryIdentity(this.root);CreatePrivateDirectory(profiles);
                    profilesIdentity=Native.DirectoryIdentity(profiles);
                    profilesOwner=new(profiles,profilesIdentity,ReplayStorage.ObserveCapacity(profiles).Volume);
                    using var identity=WindowsIdentity.GetCurrent();sid=identity.User.Value;
                    using var sha=SHA256.Create();name=Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sid+":"+purpose)))+".lock";
                }
                catch{Dispose();throw;}
            }
            internal IDisposable AcquireOperation()
            {lock(lifecycle)return AcquireOperationCore();}
            IDisposable AcquireOperationCore()
            {
                if(disposed||uncertain)throw new IOException("Authority session closure is uncertain.");
                using(var identity=WindowsIdentity.GetCurrent())if(identity.User.Value!=sid)throw new IOException("Authority user changed inside request.");
                if(Native.DirectoryIdentity(root)!=rootIdentity||Native.DirectoryIdentity(profiles)!=profilesIdentity
                    ||(File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0||(File.GetAttributes(profiles)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("Authority directory identity or namespace changed.");
                Validate(new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access));
                Validate(new DirectoryInfo(profiles).GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access));
                ReplayGuardDirectory.FileTicket file=null;
                try
                {
                    file=profilesOwner.OpenAuthorityLock(name);var observed=file.Observe();
                    if(observed.Links!=1||lockIdentity!=null&&observed.Extent.Identity!=lockIdentity)throw new IOException("Authority lock object changed or is linked.");
                    ValidateFile(Path.Combine(profiles,name));lockIdentity??=observed.Extent.Identity;
                    active++;var result=new Operation(this,file);file=null;return result;
                }
                finally{file?.Dispose();}
            }
            sealed class Operation(LockDirectoryLease owner,ReplayGuardDirectory.FileTicket file):IDisposable
            {
                bool closed;
                public void Dispose()
                {
                    lock(owner.lifecycle){
                    if(closed)return;
                    try{file.Dispose();closed=true;owner.active--;}
                    catch{owner.uncertain=true;throw;}
                    }
                }
            }
            public void Dispose()
            {lock(lifecycle)DisposeCore();}
            void DisposeCore()
            {
                if(disposed)return;
                if(active!=0||uncertain)throw new IOException("Authority operations remain owned.");
                try{profilesOwner?.Dispose();rootPins?.Dispose();disposed=true;}
                catch{uncertain=true;throw;}
            }
        }
        internal static IDisposable AcquireLock(string root, string purpose, string profile = null)
        {
            string bound; return AcquireLock(root, purpose, profile, out bound);
        }
        internal static IDisposable AcquireLock(string root, string purpose, string profile, out string bound)
        {
            bound = null;
            var lease = new LockLease();
            try
            {
                CreatePrivateDirectory(root); lease.Storage = Acquire(root);
                string locks = Path.Combine(root, "profiles"); CreatePrivateDirectory(locks); lease.Parents = PinDirectories(locks);
                if (profile != null) { lease.Profile = new ProfileDirectoryLease(profile); bound = lease.Profile.DirectoryPath; }
                using (var identity = WindowsIdentity.GetCurrent())
                using (var sha = SHA256.Create())
                {
                    string key = identity.User.Value + ":" + purpose + (profile == null ? "" : ":" + Native.DirectoryIdentity(bound));
                    string name = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "");
                    string file = Path.Combine(locks, name + ".lock");
                    lease.File = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    ValidateFile(file); return lease;
                }
            }
            catch { lease.Dispose(); throw; }
        }
        internal static IDisposable TryAcquireLock(string root, string purpose, string profile = null)
        {
            try { return AcquireLock(root, purpose, profile); }
            catch (IOException ex) { if (IsSharingViolation(ex)) return null; throw; }
        }
        internal static IDisposable TryAcquireLock(string root, string purpose, string profile, out string bound)
        {
            bound = null;
            try { return AcquireLock(root, purpose, profile, out bound); }
            catch (IOException ex) { if (IsSharingViolation(ex)) return null; throw; }
        }
    }
}
