using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
namespace F10Recorder;
internal static class RootedOperationChecks
{
    static readonly JsonSerializerOptions Json=new(WorkerProtocol.Json){WriteIndented=true};
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool DeviceIoControl(SafeFileHandle file,uint code,byte[] input,uint inputBytes,IntPtr output,uint outputBytes,out uint returned,IntPtr overlapped);
    static void Need(bool ok,string message){if(!ok)throw new IOException(message);}
    static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    internal static void Junction(string path,string target)
    {
        byte[] sub=Encoding.Unicode.GetBytes(@"\??\"+Path.GetFullPath(target)),print=Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] data=new byte[20+sub.Length+print.Length];BitConverter.GetBytes(0xA0000003u).CopyTo(data,0);BitConverter.GetBytes(checked((ushort)(data.Length-8))).CopyTo(data,4);
        BitConverter.GetBytes(checked((ushort)sub.Length)).CopyTo(data,10);BitConverter.GetBytes(checked((ushort)(sub.Length+2))).CopyTo(data,12);BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(data,14);sub.CopyTo(data,16);print.CopyTo(data,18+sub.Length);
        using var file=CreateFile(path,0x40000000,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);if(file.IsInvalid||!DeviceIoControl(file,0x900A4,data,(uint)data.Length,IntPtr.Zero,0,out _,IntPtr.Zero))throw new Win32Exception();
    }
    internal static void RemoveJunction(string path)
    {
        byte[] data=new byte[8];BitConverter.GetBytes(0xA0000003u).CopyTo(data,0);
        using var file=CreateFile(path,0x40000000,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);if(file.IsInvalid||!DeviceIoControl(file,0x900AC,data,8,IntPtr.Zero,0,out _,IntPtr.Zero))throw new Win32Exception();
    }
    static ReplayGuardDirectory Root(string path)=>new(path,Native.DirectoryIdentity(path),ReplayStorage.ObserveCapacity(path).Volume);
    static void Rejected(Action action,string message){bool failed=false;try{action();}catch(Exception e)when(e is IOException or Win32Exception or ObjectDisposedException or UnauthorizedAccessException){failed=true;}Need(failed,message);}
    internal static int Run(string planFile,string expectedSha)
    {
        Need(new FileInfo(planFile).Length<=65536&&Hash(planFile)==expectedSha,"Exact rooted-operation admission required.");using var doc=JsonDocument.Parse(File.ReadAllText(planFile));var p=doc.RootElement;
        Need(p.GetProperty("kind").GetString()=="replay-save-operation-controls-v1"&&DateTime.UtcNow.AddSeconds(60)<p.GetProperty("expiresUtc").GetDateTime(),"Fresh rooted-operation plan required.");
        foreach(var f in p.GetProperty("files").EnumerateObject())Need(Hash(f.Name)==f.Value.GetString(),"Changed binding: "+f.Name);
        foreach(string path in new[]{Environment.ProcessPath,typeof(RootedOperationChecks).Assembly.Location,typeof(CaptureSession).Assembly.Location,typeof(Settings).Assembly.Location,typeof(object).Assembly.Location})Need(p.GetProperty("files").TryGetProperty(path,out _),"Actual helper/runtime/product unbound.");
        string root=Path.GetFullPath(p.GetProperty("root").GetString());Need(root.StartsWith(Path.GetFullPath("artifacts")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&!Directory.Exists(root)&&!File.Exists(root),"Exact fresh fixture root required.");
        Log.DirectoryPath = root;
        Directory.CreateDirectory(root);using var process=Process.GetCurrentProcess();var clock=Stopwatch.StartNew();var controls=new List<object>();long peakRam=0;int peakHandles=0;Exception failure=null;bool junctionActive=false;string activePath=null;
        void Bound(){process.Refresh();peakRam=Math.Max(peakRam,process.WorkingSet64);peakHandles=Math.Max(peakHandles,process.HandleCount);Need(clock.Elapsed.TotalSeconds<30&&peakRam<=268435456&&peakHandles<=2048,"Rooted-operation resource/time limit.");Need(ReplayStorage.ObserveCapacity(root).AvailableBytes>=60000000000,"Rooted-operation free60GB floor.");}
        void Passed(string name,object detail=null){Bound();controls.Add(new{name,detail});byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(controls,Json);Need(bytes.Length<=131072,"Control progress128KiB cap.");File.WriteAllBytes(Path.Combine(root,"controls.json"),bytes);}
        string Make(string leaf){string path=Path.Combine(root,leaf);Directory.CreateDirectory(path);return path;}
        byte[] payload=Enumerable.Range(0,4096).Select(n=>(byte)(n%251)).ToArray(),foreign=Enumerable.Repeat((byte)0xE7,4096).ToArray();
        try
        {
            string a=Make("source"),b=Make("target"),f=Make("foreign");File.WriteAllBytes(Path.Combine(a,"source.bin"),payload);File.WriteAllBytes(Path.Combine(f,"source.bin"),foreign);
            string foreignHash=Hash(Path.Combine(f,"source.bin"));var original=ReplayStorage.Observe(Path.Combine(a,"source.bin"));
            using var source=Root(a);using var target=Root(b);
            Rejected(()=>new ReplayGuardDirectory(a,"00000000:0000000000000000",original.Volume).Dispose(),"Wrong directory ID accepted.");
            Rejected(()=>new ReplayGuardDirectory(a,source.Identity,@"\\?\Volume{00000000-0000-0000-0000-000000000000}\").Dispose(),"Wrong volume accepted.");Passed("directory-and-volume-binding");
            using(var input=source.OpenRead("source.bin",original))
            {
                byte[] read=new byte[4096];input.Data().ReadExactly(read);Need(read.SequenceEqual(payload),"Rooted input read changed source.");
                Rejected(()=>source.Dispose(),"Directory closed while file user remained.");Passed("input-native-object-and-root-lifetime");
                input.LinkTo(target,"snapshot.bin");Need(input.Observe().Links==2,"Native hardlink count did not change.");Passed("hardlink-from-readonly-no-delete-source");
            }
            var link=ReplayStorage.Observe(Path.Combine(b,"snapshot.bin"));Need(link.Identity==original.Identity,"Hardlink target differs from source.");
            using(var removal=target.OpenForRemoval("snapshot.bin",link)){removal.Delete();Rejected(()=>removal.Observe(),"Successful disposition allowed a later query.");}
            Need(!File.Exists(Path.Combine(b,"snapshot.bin"))&&File.ReadAllBytes(Path.Combine(a,"source.bin")).SequenceEqual(payload),"Hardlink cleanup damaged original.");Passed("snapshot-link-delete-preserves-original");
            using(var input=source.OpenRead("source.bin",original))using(var copy=target.CreateNew("copy.bin"))
            {
                Need(copy.CreatedNew&&!copy.AllowsOtherWriters,"Managed creator lacks origin/share proof.");input.Data().CopyTo(copy.Data());copy.Data().Flush();Need(copy.Observe().Extent.Length==4096,"Copy length changed.");
                Rejected(()=>new FileStream(Path.Combine(b,"copy.bin"),FileMode.Open,FileAccess.Write,FileShare.ReadWrite).Dispose(),"Managed output admitted a competing data writer.");
            }
            Need(File.ReadAllBytes(Path.Combine(b,"copy.bin")).SequenceEqual(payload),"Rooted copy bytes changed.");Passed("create-new-copy-and-exclusive-writer");
            ReplayFileExtent grown;
            using(var main=target.CreateNew("growing-main.bin"))
            {
                main.Data().Write(payload);main.Data().Flush();var initial=main.Observe().Extent;
                using var observer=target.OpenObservation("growing-main.bin",initial);
                main.Data().Write(payload);main.Data().Flush();grown=observer.Observe().Extent;
                Need(grown.Length==8192&&grown.AllocatedBytes>=8192,"Held observer reused an old extent.");
                main.LinkTo(source,"late-main-link.bin");Need(observer.Observe().Links==2,"Held observer missed a late output hardlink.");Passed("fresh-output-growth-and-link-observation");
            }
            using(var removal=source.OpenForRemoval("late-main-link.bin",grown))removal.Delete();
            Need(ReplayStorage.Observe(Path.Combine(b,"growing-main.bin")).Identity==grown.Identity,"Link cleanup removed main object.");
            using(var scratch=target.CreateExternalScratch("external-scratch.bin"))
            {
                Need(scratch.CreatedNew&&scratch.AllowsOtherWriters,"External writer sharing was falsely certified exclusive.");
                using var other=new FileStream(Path.Combine(b,"external-scratch.bin"),FileMode.Open,FileAccess.Write,FileShare.ReadWrite);other.Write(payload);other.Flush();
            }
            Passed("external-scratch-does-not-inherit-main-writer-proof");
            string workPath=Path.Combine(b,"owned-work"),workIdentity,nestedIdentity;ReplayFileExtent registered;
            using(var work=target.CreateDirectory("owned-work"))
            {
                Need(work.CreatedNewDirectory,"Workspace creator did not certify a new directory.");workIdentity=work.Identity;
                using(var member=work.CreateNew("registered.bin")){member.Data().Write(payload);member.Data().Flush();registered=member.Observe().Extent;}
                Rejected(()=>target.Dispose(),"Parent closed with live child directory.");
                Rejected(()=>target.DeleteEmptyDirectory("owned-work",workIdentity),"Cleanup removed a live pinned directory.");
            }
            Rejected(()=>target.DeleteEmptyDirectory("owned-work","00000000:0000000000000000"),"Cleanup accepted wrong directory ID.");
            Rejected(()=>target.DeleteEmptyDirectory("owned-work",workIdentity),"Cleanup recursively removed a nonempty directory.");Passed("new-child-directory-and-nonrecursive-owned-cleanup");
            using(var work=target.OpenDirectory("owned-work",workIdentity))
            {
                using(var nested=work.CreateDirectory("foreign-junction"))nestedIdentity=nested.Identity;
                string nestedPath=Path.Combine(workPath,"foreign-junction");Junction(nestedPath,f);junctionActive=true;activePath=nestedPath;
                Rejected(()=>work.OpenDirectory("foreign-junction",Native.DirectoryIdentity(f)).Dispose(),"Cleanup/input capability followed nested junction.");
            }
            Rejected(()=>target.DeleteEmptyDirectory("owned-work",workIdentity),"Cleanup followed foreign junction contents.");
            Need(Hash(Path.Combine(f,"source.bin"))==foreignHash,"Nested cleanup modified foreign sentinel.");
            RemoveJunction(activePath);junctionActive=false;
            using(var work=target.OpenDirectory("owned-work",workIdentity))
            {
                work.DeleteEmptyDirectory("foreign-junction",nestedIdentity);
                using var member=work.OpenForRemoval("registered.bin",registered);member.Delete();
            }
            target.DeleteEmptyDirectory("owned-work",workIdentity);Need(!Directory.Exists(workPath),"Owned empty workspace was not retired.");Passed("nested-junction-refusal-and-final-directory-retirement");
            Rejected(()=>target.CreateNew("copy.bin").Dispose(),"CreateNew replaced existing file.");
            Rejected(()=>source.OpenRead("source.bin",original with{Identity="00000000:0000000000000000"}).Dispose(),"Wrong file ID accepted.");Passed("identity-and-no-replacement-before-data");
            var copyExtent=ReplayStorage.Observe(Path.Combine(b,"copy.bin"));File.WriteAllBytes(Path.Combine(a,"final.bin"),foreign);
            using(var publish=target.OpenForRemoval("copy.bin",copyExtent)){Rejected(()=>publish.RenameTo(source,"final.bin"),"Publication overwrote a foreign destination.");Rejected(()=>publish.Observe(),"Failed mutation ticket remained reusable.");}
            Need(File.ReadAllBytes(Path.Combine(a,"final.bin")).SequenceEqual(foreign)&&File.Exists(Path.Combine(b,"copy.bin")),"Collision changed source or destination.");Passed("publication-collision-preserves-both");
            using(var publish=target.OpenForRemoval("copy.bin",copyExtent))publish.RenameTo(source,"published.bin");
            Need(!File.Exists(Path.Combine(b,"copy.bin"))&&ReplayStorage.Observe(Path.Combine(a,"published.bin")).Identity==copyExtent.Identity,"Rooted publication changed object.");Passed("checked-object-relative-publication");
            // Native success followed by an injected completion/report failure
            // still leaves a mutation ticket close-only; no subsequent stat/data.
            var published=ReplayStorage.Observe(Path.Combine(a,"published.bin"));source.AfterNativeMutationForChecks=()=>throw new IOException("Injected post-native completion failure.");
            using(var removal=source.OpenForRemoval("published.bin",published)){Rejected(removal.Delete,"Injected completion failure was ignored.");Rejected(()=>removal.Observe(),"Exception revived disposed object access.");}
            source.AfterNativeMutationForChecks=null;Need(!File.Exists(Path.Combine(a,"published.bin")),"Injected failure did not follow native success.");Passed("mutation-exception-is-close-only");
            string cache=Make("cache"),pulse=Path.Combine(cache,"pulse");Directory.CreateDirectory(pulse);
            long tick=0;int observations=0;var timing=new ReplayStorageCheckTiming();var gate=new ReplayStorageObservationGate(()=>{observations++;_ = ReplayStorage.InventoryNamespaces([root],new ReplayFiles());return true;},100,500,timing,timestamp:()=>tick,frequency:1000);
            gate.CheckNow();Junction(pulse,f);junctionActive=true;activePath=pulse;tick=10;gate.Check();Need(observations==1,"Global scan unexpectedly sampled accepted gap.");RemoveJunction(pulse);junctionActive=false;tick=90;gate.Check();Need(observations==1,"Skipped callback refreshed global evidence.");tick=100;gate.Check();Need(observations==2,"Due healthy scan missing.");Passed("accepted-transient-junction-gap",new{observations,timing.Coalesced});
            Junction(pulse,f);junctionActive=true;activePath=pulse;tick=110;Rejected(gate.CheckNow,"Forced scan accepted persistent junction.");RemoveJunction(pulse);junctionActive=false;Passed("forced-transition-rejects-persistent-junction");
            long opTick=0;int opObservations=0;var operationGate=new ReplayStorageObservationGate(()=>{opObservations++;_ = ReplayStorage.InventoryNamespaces([root],new ReplayFiles());return true;},100,500,timestamp:()=>opTick,frequency:1000);operationGate.CheckNow();
            // Inject after the directory precheck and restore before the postcheck.
            // A later healthy path cannot hide a wrong-target native side effect.
            foreach(string operation in new[]{"create","link","rename"})
            {
                string owned=Make("pulse-"+operation);using var destination=Root(owned);string leaf=operation+"-result.bin";
                opTick+=10;operationGate.Check();int beforeObservations=opObservations;
                bool injected=false,nativeCompleted=false,succeeded=false;string operationError=null;
                void Before(){Junction(owned,f);injected=true;junctionActive=true;activePath=owned;}
                void Restore(){if(junctionActive){RemoveJunction(owned);junctionActive=false;}}
                void AfterNative(){nativeCompleted=true;Restore();}
                destination.BeforeNativeOpenForChecks=Before;destination.AfterNativeOpenForChecks=AfterNative;
                destination.BeforeNativeMutationForChecks=Before;destination.AfterNativeMutationForChecks=AfterNative;
                try
                {
                    if(operation=="create"){try{using var created=destination.CreateNew(leaf);created.Data().Write(payload);created.Data().Flush();succeeded=true;}catch(Exception e)when(e is IOException or Win32Exception){operationError=e.ToString();}}
                    else if(operation=="link"){using var input=source.OpenRead("source.bin",original);try{input.LinkTo(destination,leaf);succeeded=true;}catch(Exception e)when(e is IOException or Win32Exception){operationError=e.ToString();}}
                    else{string from="rename-source.bin";File.WriteAllBytes(Path.Combine(a,from),payload);var expected=ReplayStorage.Observe(Path.Combine(a,from));using var input=source.OpenForRemoval(from,expected);try{input.RenameTo(destination,leaf);succeeded=true;}catch(Exception e)when(e is IOException or Win32Exception){operationError=e.ToString();}}
                }
                finally{Restore();destination.BeforeNativeOpenForChecks=destination.AfterNativeOpenForChecks=destination.BeforeNativeMutationForChecks=destination.AfterNativeMutationForChecks=null;}
                Need(injected&&nativeCompleted,"Held-parent redirect was not injected across the native operation.");
                Need(!File.Exists(Path.Combine(f,leaf))&&Hash(Path.Combine(f,"source.bin"))==foreignHash,"Temporary redirect affected foreign output despite restored health.");
                opTick++;operationGate.Check();Need(opObservations==beforeObservations,"A later scan masked the operation test.");
                if(succeeded)Need(File.Exists(Path.Combine(owned,leaf))&&File.ReadAllBytes(Path.Combine(owned,leaf)).SequenceEqual(payload),"Successful operation did not land in the original authorized directory.");
                if(!succeeded&&operation=="rename")Need(File.Exists(Path.Combine(a,"rename-source.bin"))||File.Exists(Path.Combine(owned,leaf)),"Failed rename lost the owned source without retained evidence.");
                Passed("temporary-parent-junction-cannot-redirect-"+operation,new{injected,nativeCompleted,succeeded,operationError});
            }
            bool inputRedirected=false,inputPrevented=false,foreignInputRead=false;
            source.BeforeNativeOpenForChecks=()=>{try{Junction(a,f);inputRedirected=true;junctionActive=true;activePath=a;}catch(Win32Exception e)when(e.NativeErrorCode is 5 or 32 or 145){inputPrevented=true;throw;}};
            source.AfterNativeOpenForChecks=()=>{if(junctionActive){RemoveJunction(a);junctionActive=false;}};
            try
            {
                try{using var input=source.OpenRead("source.bin",original);byte[] actual=new byte[4096];input.Data().ReadExactly(actual);foreignInputRead=!actual.SequenceEqual(payload);}
                catch(Exception e)when(e is IOException or Win32Exception){}
            }
            finally{if(junctionActive){RemoveJunction(a);junctionActive=false;}source.BeforeNativeOpenForChecks=source.AfterNativeOpenForChecks=null;}
            Need(inputRedirected||inputPrevented,"Input redirect control did not exercise a fault.");Need(!foreignInputRead&&Hash(Path.Combine(f,"source.bin"))==foreignHash,"Input attempt read or modified foreign bytes.");Passed("input-open-parent-redirect-protection",new{inputRedirected,inputPrevented});
            tick=601;Rejected(gate.RequireFreshness,"Skipped checks refreshed expired evidence.");
            long blocked=0;var slow=new ReplayStorageObservationGate(()=>{blocked+=501;return true;},100,500,timestamp:()=>blocked,frequency:1000);Rejected(slow.CheckNow,"Checking time was excluded from500ms.");Passed("freshness-skips-and-check-work-fail-closed");
            Need(Hash(Path.Combine(f,"source.bin"))==foreignHash&&File.ReadAllBytes(Path.Combine(a,"source.bin")).SequenceEqual(payload),"Original or foreign sentinel changed.");Bound();
            EvidenceOwnerChecks.Run(Path.Combine(root,"owner"),Passed);
            OwnedFileSystemChecks.Run(Path.Combine(root,"registry"),Passed,Junction,RemoveJunction);
            TypedGateChecks.Run(Path.Combine(root,"typed-gate"),Passed,Bound);
            CacheIntegrationChecks.Run(Path.Combine(root,"cache"),Passed,Bound,Junction,RemoveJunction);
            LedgerCheckpointChecks.Run(Path.Combine(root,"ledger"),Passed,Bound);
            SessionCacheIntegrationChecks.Run(Path.Combine(root,"session-cache"),Passed,Bound,Junction,RemoveJunction);SessionNativeChecks.Run(Path.Combine(root,"session-native"),Passed,Bound);PipelineNativeChecks.Run(Path.Combine(root,"packet-pipeline"),Passed,Bound);
            ProductionExportChecks.Run(Path.Combine(root,"production"),Passed,Bound);
            DecoderObservationChecks.Run(Path.Combine(root,"decoder-observation"),Passed,Bound);
        }
        catch(Exception error){failure=error;Console.Error.WriteLine(error);}
        finally{if(junctionActive&&activePath!=null){try{RemoveJunction(activePath);junctionActive=false;}catch(Exception cleanup){failure??=cleanup;}}}
        Bound();var map=junctionActive?null:ReplayStorage.Inventory([root],new ReplayFiles(),2048,512);long bytes=map==null?0:ReplayStorage.Bytes(map);Need(junctionActive||bytes<=4194304,"Fixture4MiB native cap exceeded.");
        byte[] report=JsonSerializer.SerializeToUtf8Bytes(new{result=failure==null?"rooted-operation-controls-collected":"failed",error=failure?.ToString(),controls,junctionActive,bytes,files=map,seconds=clock.Elapsed.TotalSeconds,peakRam,peakHandles,
            scope="Tiny NONMEDIA rooted-operation plus actual synthetic Cache/session integration. Includes actual accepted transient gap and wrong-target exploitation attempts; no ordinary media/worker UI/capture/encoder/decoder/storage-model/performance/adoption claim."},Json);Need(report.Length<=1048576,"Rooted-operation report1MiB limit.");File.WriteAllBytes(Path.Combine(root,"result.json"),report);Bound();Console.WriteLine(JsonSerializer.Serialize(new{terminal=true,seconds=clock.Elapsed.TotalSeconds,peakRam,peakHandles,bytes,reportBytes=report.Length}));return failure==null?0:1;
    }
}





