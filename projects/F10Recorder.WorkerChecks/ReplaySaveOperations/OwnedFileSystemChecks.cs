namespace F10Recorder;

internal static class OwnedFileSystemChecks
{
    static void Need(bool value, string why) { if (!value) throw new IOException(why); }
    static void Reject(Action action, string why)
    {
        bool rejected = false;
        try { action(); } catch (Exception error) when (error is IOException or ObjectDisposedException or System.ComponentModel.Win32Exception) { rejected = true; }
        Need(rejected, why);
    }
    internal static void Run(string root, Action<string, object> passed, Action<string, string> junction, Action<string> removeJunction)
    {
        Directory.CreateDirectory(root);
        string inputs = Path.Combine(root, "inputs"), cache = Path.Combine(root, "cache"), foreign = Path.Combine(root, "foreign");
        Directory.CreateDirectory(inputs); Directory.CreateDirectory(cache); Directory.CreateDirectory(foreign);
        byte[] payload = Enumerable.Range(0, 4096).Select(n => (byte)(n % 251)).ToArray(), sentinel = Enumerable.Repeat((byte)0xE7, 4096).ToArray();
        File.WriteAllBytes(Path.Combine(inputs, "original.bin"), payload);
        File.WriteAllBytes(Path.Combine(foreign, "sentinel.bin"), sentinel);
        var expected = ReplayStorage.Observe(Path.Combine(inputs, "original.bin"));
        using var registry = new ReplayOwnedFileSystem();
        var original = registry.AdmitRoot(inputs, Native.DirectoryIdentity(inputs), expected.Volume);
        var owned = registry.AdmitRoot(cache, Native.DirectoryIdentity(cache), expected.Volume);
        registry.AdmitFile(original, "original.bin", expected, false);
        var healthy = registry.CreateDirectory(owned, "healthy");
        registry.Link(original, "original.bin", healthy, "link.bin");
        registry.Copy(original, "original.bin", healthy, "copy.bin");
        registry.WriteNew(healthy, "new.bin", stream =>
        {
            Reject(registry.Dispose, "Registry closed while its writer was live.");
            Reject(() => registry.RemoveRegisteredTree(healthy), "Registry retired a tree while its writer was live.");
            stream.Write(payload);
        });
        int count = 0;
        registry.Read(healthy, "copy.bin", stream =>
        {
            Reject(registry.Dispose, "Registry closed before its input user returned.");
            byte[] bytes = new byte[4096]; stream.ReadExactly(bytes); Need(bytes.SequenceEqual(payload), "Owned registry copied foreign input."); count = bytes.Length;
        });
        Reject(() => registry.RemoveFile(original, "original.bin"), "Original media was retired.");
        Reject(() => registry.RemoveRegisteredTree(original), "Original/root directory was retired.");
        registry.RemoveRegisteredTree(healthy);
        Need(count == 4096 && !Directory.Exists(Path.Combine(cache, "healthy"))
            && File.ReadAllBytes(Path.Combine(inputs, "original.bin")).SequenceEqual(payload), "Registered cleanup consumed original data or retained the owned healthy tree.");
        passed("registry-rooted-link-copy-write-read-and-known-tree-retirement", null);

        object cacheGate = new();
        using (var ready = new ManualResetEventSlim())
        {
            Task contender = null; bool acquired = false;
            try { registry.Read(original, "original.bin", _ =>
            {
                contender = Task.Run(() =>
                {
                    lock (cacheGate)
                    {
                        ready.Set();
                        Reject(() => registry.RemoveRegisteredTree(original), "Lock-order control retired an original root.");
                    }
                });
                Need(ready.Wait(1000), "Cache-gate contender did not start within the declared bound.");
                acquired = Monitor.TryEnter(cacheGate, 1000);
                if (acquired) Monitor.Exit(cacheGate);
            }); }
            finally { if (contender != null) Need(contender.Wait(2000), "Lock-order contender did not join."); }
            Need(contender != null, "Lock-order contender was not created.");
            contender.GetAwaiter().GetResult();
            Need(acquired, "Borrowed data callback held registry lock while waiting for Cache gate.");
            passed("registry-data-callback-avoids-cache-lock-inversion", new { acquired });
        }

        var unknown = registry.CreateDirectory(owned, "unknown");
        registry.Copy(original, "original.bin", unknown, "known.bin");
        string unknownPath = Path.Combine(cache, "unknown");
        File.WriteAllBytes(Path.Combine(unknownPath, "unregistered.bin"), sentinel);
        Reject(() => registry.RemoveRegisteredTree(unknown), "Cleanup recursively deleted an unregistered file.");
        Reject(() => registry.CreateDirectory(unknown, "revived"), "A failed directory retirement remained reusable.");
        Need(File.ReadAllBytes(Path.Combine(unknownPath, "unregistered.bin")).SequenceEqual(sentinel), "Cleanup touched an unregistered file.");
        passed("registry-unknown-member-retains-directory-and-foreign-bytes", null);

        var changed = registry.CreateDirectory(owned, "changed");
        registry.Copy(original, "original.bin", changed, "expected.bin");
        string replaced = Path.Combine(cache, "changed", "expected.bin");
        File.Move(replaced, Path.Combine(cache, "changed", "original-kept.bin"));
        File.WriteAllBytes(replaced, sentinel);
        bool entered = false;
        Reject(() => registry.Read(changed, "expected.bin", stream => { entered = true; stream.ReadByte(); }), "Input open read a substituted file.");
        Need(!entered, "Substituted input reached the data callback before rejection.");
        Reject(() => registry.RemoveFile(changed, "expected.bin"), "Prune deleted a substituted file.");
        Need(File.ReadAllBytes(replaced).SequenceEqual(sentinel), "A later healthy path repaired an operation on a substituted file.");
        passed("registry-input-and-prune-refuse-substituted-native-object", null);

        var tree = registry.CreateDirectory(owned, "transient");
        var pulse = registry.CreateDirectory(tree, "pulse");
        string pulsePath = Path.Combine(cache, "transient", "pulse");
        long tick = 0; int scans = 0; bool active = false;
        var gate = new ReplayStorageObservationGate(() => { scans++; _ = ReplayStorage.InventoryNamespaces([root], new ReplayFiles()); return true; }, 100, 500, timestamp: () => tick, frequency: 1000);
        gate.CheckNow();
        try
        {
            junction(pulsePath, foreign); active = true; tick = 10; gate.Check(); Need(scans == 1, "Transient operation control unexpectedly rescanned.");
            Reject(() => registry.RemoveRegisteredTree(tree), "Registered cleanup followed a transient junction into foreign contents.");
            Need(File.ReadAllBytes(Path.Combine(foreign, "sentinel.bin")).SequenceEqual(sentinel), "Transient cleanup changed foreign data before a healthy rescan.");
        }
        finally { if (active) { removeJunction(pulsePath); active = false; } }
        tick = 100; gate.Check(); Need(scans == 2, "Restored scheduled global scan did not complete.");
        Need(File.ReadAllBytes(Path.Combine(foreign, "sentinel.bin")).SequenceEqual(sentinel), "Healthy later scan concealed a wrong-file deletion.");
        passed("registry-transient-gap-cannot-redirect-actual-tree-cleanup", new { scans });
    }
}

