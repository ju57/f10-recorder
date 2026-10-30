using System.Text.Json;
namespace F10Recorder;

internal static class ReplaySnapshotInventoryChecks
{
    internal static int Run(string root)
    {
        using var owner = PilotStorageObservation.Create(root);
        var passed = new List<string>();
        void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
        Dictionary<string,ReplayFileExtent> Scan(string path, ReplaySnapshotInventory reader)
        { reader.BeginScan();try{return ReplayStorage.Inventory([path],reader,32,8);}finally{reader.EndScan();} }
        string live=Path.Combine(root,"live");Directory.CreateDirectory(live);
        string file=Path.Combine(live,"chunk.ts");File.WriteAllBytes(file,new byte[4096]);
        using(var reader=new ReplaySnapshotInventory())
        {
            Scan(live,reader);Require(reader.HandleCount==0,"Growing acquisition file acquired a persistent handle.");
            passed.Add("live-acquisition-uses-short-metadata-leases");
        }
        string snapshot=Path.Combine(root,"save-owned");Directory.CreateDirectory(snapshot);
        string input=Path.Combine(snapshot,"000000.ts");File.WriteAllBytes(input,new byte[4096]);
        using(var reader=new ReplaySnapshotInventory())
        {
            reader.Seal(snapshot);var before=Scan(snapshot,reader)[input];Require(reader.HandleCount==1,"Sealed input handle not retained.");
            using(var writer=new FileStream(input,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){writer.Write(new byte[4096]);writer.Flush(true);}
            var grown=Scan(snapshot,reader)[input];Require(grown.Length==8192&&grown==ReplayStorage.Observe(input),"Allocation/EOF was cached.");
            passed.Add("sealed-handle-still-refreshes-real-allocation-and-EOF");
            string old=input+".old";File.Move(input,old);File.WriteAllBytes(input,new byte[12288]);
            var replaced=Scan(snapshot,reader)[input];Require(replaced.Identity!=before.Identity&&replaced.Length==12288,"Same name retargeted a cached handle.");
            passed.Add("same-name-replacement-revalidates-identity");
            string metadata=Path.Combine(snapshot,"metadata.json");File.WriteAllText(metadata,"first");Scan(snapshot,reader);
            File.WriteAllText(metadata+".pending","second");File.Move(metadata+".pending",metadata,true);Scan(snapshot,reader);
            Require(reader.HandleCount==1,"Atomic metadata acquired a retained handle.");passed.Add("atomic-metadata-is-never-held");
            reader.ReleaseUnder(snapshot);Require(reader.HandleCount==0,"Cleanup retained metadata handles.");Directory.Delete(snapshot,true);
            passed.Add("namespace-retirement-releases-handles-before-removal");
        }
        // CloseAsync is the maintained host shutdown route, not just IDisposable.
        using(var cache=new ReplayCache(cacheDirectory:live)){cache.CloseAsync().GetAwaiter().GetResult();cache.CloseAsync().GetAwaiter().GetResult();}
        passed.Add("empty-native-cache-closeAsync-is-idempotent");
        File.WriteAllText(Path.Combine(root,"verification.json"),JsonSerializer.Serialize(new{result="passed",passed,effects="Tiny private native metadata fixtures only; no engine, child, capture, audio, model or user-state change."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Snapshot metadata checks: {passed.Count}/{passed.Count} passed");return 0;
    }
}
