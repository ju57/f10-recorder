using System.Text.Json;

namespace F10Recorder;

internal static class ReplayCollectorDisposeChecks
{
    internal static int Run(string root)
    {
        if(Directory.Exists(root)||File.Exists(root))throw new IOException("Preserve prior generic failure fixture.");
        using var owned=PilotStorageObservation.Create(root);
        File.WriteAllBytes(Path.Combine(root,"dummy.bin"),new byte[4096]);
        var plan=new CalibrationPlan(root,"unused-no-engine","controlled-generic-export-failure",new(),[],[],0,0,
            BudgetBytes:2_000_000_000,StopBytes:1_900_000_000,FloorBytes:60_000_000_000,OutsideBuildAllowanceBytes:0,DirectoryLimit:64,FileLimit:512);
        ReplayCalibrationRun.Sampler observed=null;string primary=null;
        try
        {
            using var sampler=observed=new ReplayCalibrationRun.Sampler(plan,active:false);
            sampler.BeginActive();Thread.Sleep(220);
            throw new IOException("controlled-generic-export-failure");
        }
        catch(IOException error){primary=error.Message;}
        string file=Path.Combine(root,"storage-writer.json");
        JsonElement? receipt=File.Exists(file)?JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file)):null;
        bool passed=primary=="controlled-generic-export-failure"&&observed.ObserverAbsent&&receipt is{} r
            &&r.GetProperty("final").GetBoolean()&&r.GetProperty("writerAbsent").GetBoolean()&&r.GetProperty("failure").ValueKind==JsonValueKind.Null
            &&r.GetProperty("enqueued").GetInt64()==r.GetProperty("written").GetInt64()&&r.GetProperty("bytes").GetInt64()==r.GetProperty("persistedBytes").GetInt64();
        byte[] firstReceipt=File.Exists(file)?File.ReadAllBytes(file):[];
        observed.Dispose();
        bool repeatedSuccessPreserved=File.Exists(file)&&firstReceipt.SequenceEqual(File.ReadAllBytes(file));
        string blocked=Path.Combine(root,"receipt-conflict");Directory.CreateDirectory(blocked);
        File.WriteAllBytes(Path.Combine(blocked,"dummy.bin"),new byte[4096]);
        string authority=Path.Combine(blocked,"authority"),owner=Guid.NewGuid().ToString("N");
        var ledger=new ReplayVolumeReservations(authority);var capacity=ReplayStorage.ObserveCapacity(blocked);
        ledger.Update(owner,[new(Path.Combine(blocked,"future-growth"),blocked,capacity.Volume,capacity.AllocationUnit,4096,ReplayVolumeClaimKind.UnmaterializedGrowth)],new ReplayFiles());
        ReplayCalibrationRun.Sampler conflict=null;string conflictPrimary=null,conflictError=null,repeatError=null,claimError=null;
        byte[] sentinel=[0x62,0x61,0x64];string conflictFile=Path.Combine(blocked,"storage-writer.json");
        try
        {
            using var sampler=conflict=new ReplayCalibrationRun.Sampler(plan with{Root=blocked},active:false);
            sampler.BeginActive();Thread.Sleep(220);File.WriteAllBytes(conflictFile,sentinel);
            conflictPrimary="controlled-generic-export-failure";throw new IOException(conflictPrimary);
        }
        catch(IOException error){conflictError=error.ToString();}
        try{conflict.Dispose();}catch(IOException error){repeatError=error.ToString();}
        bool released=false;
        try{ReplayCalibrationRun.CompleteStoppedCaptureClaim(conflict,true,()=>{ledger.Release(owner);released=true;});}
        catch(IOException error){claimError=error.ToString();}
        bool conflictRejected=conflictPrimary=="controlled-generic-export-failure"&&conflict.ObserverAbsent&&conflictError.Contains("receipt persistence failed")
            &&repeatError!=null&&claimError!=null&&!released&&File.ReadAllText(Path.Combine(authority,"replay-volume-reservations.json")).Contains(owner)
            &&sentinel.SequenceEqual(File.ReadAllBytes(conflictFile));
        passed&=repeatedSuccessPreserved&&conflictRejected;
        File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed,primary,observerAbsent=observed.ObserverAbsent,receipt,repeatedSuccessPreserved,
            conflict=new{conflictRejected,conflictPrimary,conflictError,repeatError,claimError,released,observerAbsent=conflict.ObserverAbsent},
            scope="Actual using-Sampler unwinding after a generic exception, no stop-request/sampler-failure marker; real threads/writer. No producer/capture/media/worker/account or synthetic receipt."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(new{passed,primary,observerAbsent=observed.ObserverAbsent,writerReceiptExists=receipt!=null}));
        return passed?0:1;
    }
}
