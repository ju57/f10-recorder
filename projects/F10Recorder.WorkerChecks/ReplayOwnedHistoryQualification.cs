using System.Text.Json;

namespace F10Recorder;

// A fresh task-scoped admission. Historical capacity/continuation envelopes
// remain unchanged and cannot be selected by changing this document's label.
internal static partial class ReplayCalibrationRun
{
    const string OwnedHistoryAdmission="issue83-owned-history-fresh-v1";
    const string OwnedHistoryRepairAdmission="issue83-owned-history-catalog-repair-fresh-v1";
    const long OwnedHistoryOutsideFuture=1_000_000_000;
    static bool OwnedHistoryQualification(CalibrationPlan p)=>p?.FreshCandidateAdmission is OwnedHistoryAdmission or OwnedHistoryRepairAdmission || DurationQualification(p);
    static bool OwnedHistoryRepair(CalibrationPlan p)=>p?.FreshCandidateAdmission==OwnedHistoryRepairAdmission;
    static bool OwnedHistoryPreparationKind(string kind) => kind is OwnedHistoryAdmission or OwnedHistoryRepairAdmission || DurationAdmissionKind(kind);
    static bool CanonicalOwnedPath(string path)
    {
        try{return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && !path.StartsWith("\\\\",StringComparison.Ordinal)
            && !Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Equals(Path.GetPathRoot(Path.GetFullPath(path)),StringComparison.OrdinalIgnoreCase)
            && Path.TrimEndingDirectorySeparator(path.Replace('/',Path.DirectorySeparatorChar)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),StringComparison.OrdinalIgnoreCase);}
        catch(Exception error) when(error is ArgumentException or NotSupportedException or PathTooLongException){return false;}
    }
    static JsonDocument ReadOwnedHistoryJson(string path,int limit=131072)
    {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        Require(input.Length is >0 && input.Length<=limit,"Owned-history evidence size bound.");
        byte[] raw=new byte[checked((int)input.Length)];input.ReadExactly(raw);Require(input.ReadByte()==-1,"Owned-history evidence grew.");
        var doc=JsonDocument.Parse(raw);
        try{RequireUniqueJsonMembers(doc.RootElement);return doc;}catch{doc.Dispose();throw;}
    }
    static long QualificationPhysicalFloor(CalibrationPlan p)=>checked(p.FloorBytes+(OwnedHistoryQualification(p)?OwnedHistoryOutsideFuture:0));
    static bool OwnedHistoryFutureFits(long accrued,long outstanding,long reports)
    {try{return accrued>=0 && outstanding is >0 and <=500_000_000 && reports==64_000_000 && checked(accrued+outstanding+reports)<=OwnedHistoryOutsideFuture;}catch(OverflowException){return false;}}
    static bool OwnedHistoryEnvelopeValid(CalibrationPlan p)=>DurationQualification(p)?DurationEnvelopeValid(p):OwnedHistoryQualification(p)
        && p.CompositeQualification && p.CompositeManualForegroundHandoff && p.CompositeExplicitForegroundRelease
        && p.CompositeTrainingRoot==null && !p.ProductionQualification && p.ProductionTrainingRoot==null
        && p.ExactContinuationRoot==null && p.ExactHeldoutContinuationRoot==null
        && p.Order!=null && p.Order.SequenceEqual(Order) && p.RetainedChildren==0 && p.MaximumChildren==61 && p.ValidationChildren==18
        && p.RetainedElapsedSeconds==0 && p.RetainedOfflineSeconds==0 && p.DeadlineSeconds==5400 && p.OfflineDeadlineSeconds==1200
        && p.BudgetBytes==(OwnedHistoryRepair(p)?32_000_000_000:28_000_000_000) && p.StopBytes==(OwnedHistoryRepair(p)?31_000_000_000:27_000_000_000) && p.FloorBytes==60_000_000_000
        && p.RetainedBudgetBytes==(OwnedHistoryRepair(p)?8_000_000_000:4_000_000_000) && p.OutsideBuildAllowanceBytes==2_000_000_000
        && p.AdditionalHistoricalReserveBytes==0 && p.CopyFallbackReserveBytes==1_000_000_000
        && p.SourceComponentBytes==1_000_000_000 && p.OutputComponentBytes==2_000_000_000
        && p.DirectoryLimit==4096 && p.FileLimit==16384 && p.FreezeRetainedAllocations && p.ReuseCurrentMetadataHandles
        && !p.RetainActiveOutputHandles && !p.ReadonlyValidationPhases && !p.OperatorForegroundHandoff && !p.UserForegroundHandoff && !p.AutomaticFixtureHandoff
        && p.ReplayExporterRecipe==ReplaySaveExport.PolicyIdentity && p.Files!=null && p.EvidenceRoles?.ContainsKey("fresh-candidate-qualification")==true
        && (!OwnedHistoryRepair(p) || p.EvidenceRoles.ContainsKey("prior-failed-attempt"));

    static bool OwnedHistoryPriorLinks(string priorHash,string receiptHash,string recordedOldPurpose,string actualOldPurpose,string declaredRepairPurpose,string currentPurpose)
        => !string.IsNullOrWhiteSpace(priorHash) && priorHash==receiptHash
            && !string.IsNullOrWhiteSpace(recordedOldPurpose) && recordedOldPurpose==actualOldPurpose
            && !string.IsNullOrWhiteSpace(declaredRepairPurpose) && declaredRepairPurpose==currentPurpose;
    static void ValidateOwnedHistoryPriorAttempt(CalibrationPlan p,string currentPurpose=null)
    {
        if(!OwnedHistoryRepair(p))return;
        string role=EvidenceRole(p,"prior-failed-attempt");Require(HashFile(role)==p.Files[role],"Changed prior-attempt disposition.");
        using var doc=ReadOwnedHistoryJson(role);var e=doc.RootElement;
        string priorPlan=e.GetProperty("planPath").GetString(),controller=e.GetProperty("controllerPath").GetString();
        Require(new[]{priorPlan,controller}.All(f=>p.Files.TryGetValue(f,out var hash) && HashFile(f)==hash)
            && e.GetProperty("kind").GetString()=="owned-history-prior-attempt-closed-without-model-v1"
            && e.GetProperty("importOldSamplesOrModel").GetBoolean()==false,"Prior failed attempt must remain independently bound and unused.");
        using var old=ReadOwnedHistoryJson(priorPlan);using var receipt=ReadOwnedHistoryJson(controller,262144);
        var c=receipt.RootElement;string root=old.RootElement.GetProperty("plan").GetProperty("Root").GetString();
        int spent=c.GetProperty("newManagedRows").GetInt32();
        string repairPurpose=e.GetProperty("repairPurposeDeadlineUtc").GetString();
        Require(old.RootElement.GetProperty("plan").GetProperty("FreshCandidateAdmission").GetString()==OwnedHistoryAdmission
            && old.RootElement.GetProperty("plan").GetProperty("MaximumChildren").GetInt32()==61
            && e.GetProperty("additionalRepairAttempts").GetInt32()==1 && e.GetProperty("extendOrRewritePriorWindow").GetBoolean()==false
            && p.RetainedRoots.Contains(root,StringComparer.OrdinalIgnoreCase) && spent==21
            && e.GetProperty("spentQualificationRoles").GetInt32()==spent
            && e.GetProperty("cumulativeQualificationRoleCeiling").GetInt32()==checked(spent+p.MaximumChildren)
            && e.GetProperty("priorElapsedSeconds").GetDouble()==c.GetProperty("seconds").GetDouble()
            && OwnedHistoryPriorLinks(HashFile(priorPlan),c.GetProperty("planHash").GetString(),e.GetProperty("originalPurposeDeadlineUtc").GetString(),
                old.RootElement.GetProperty("originalPurposeDeadlineUtc").GetString(),repairPurpose,currentPurpose??repairPurpose)
            && c.GetProperty("absent").GetBoolean() && c.GetProperty("pendingControlProofCount").GetInt32()==0 && c.GetProperty("pendingCreatorProofCount").GetInt32()==0
            && c.GetProperty("finalObservationError").ValueKind==JsonValueKind.Null && c.GetProperty("collectorCleanupError").ValueKind==JsonValueKind.Null
            && c.GetProperty("observedChildClosure").GetArrayLength()==20
            && c.GetProperty("observedChildClosure").EnumerateArray().All(v=>v.GetProperty("absent").GetBoolean() && v.GetProperty("error").ValueKind==JsonValueKind.Null)
            && !File.Exists(Path.Combine(root,"frozen-training-model.json")) && !File.Exists(Path.Combine(root,"qualified-reference.json")),
            "Preceding stopped resources, closure, unchanged deadline and retained namespace are incomplete.");
    }

    static int OwnedHistoryRootLimit(string kind) => kind == DurationRetainedHistoryAdmission ? 32 : 8;
    static bool OwnedHistoryPathsDisjoint(string[] retained,string current,int maximum=8)
    {
        bool Contains(string parent,string child)=>child.Equals(parent,StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(Path.TrimEndingDirectorySeparator(parent)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        if(maximum is <1 or >32 || retained==null || retained.Length<1 || retained.Length>maximum || !CanonicalOwnedPath(current))return false;
        current=Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        if(retained.Any(p=>!CanonicalOwnedPath(p)))return false;
        retained=retained.Select(p=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToArray();
        for(int n=0;n<retained.Length;n++)
        {
            if(!Path.IsPathFullyQualified(retained[n]) || Contains(retained[n],current) || Contains(current,retained[n]))return false;
            for(int m=0;m<n;m++)if(Contains(retained[m],retained[n]) || Contains(retained[n],retained[m]))return false;
        }
        return true;
    }

    // Invoked by the existing read-only Prepare route before binding generated
    // evidence. Only a prospectively declared small fixed-file set is queried;
    // no repository/media sweep, capture, engine probe or settings write occurs.
    static void PrepareOwnedHistoryOutside(string admission)
    {
        string resources=Path.Combine(admission,"resources.json");
        using var resource=ReadOwnedHistoryJson(resources);
        if(!resource.RootElement.TryGetProperty("freshCandidateAdmission",out var kind) || !OwnedHistoryPreparationKind(kind.GetString()))return;
        string input=Path.Combine(admission,"outside-inventory-plan.json");
        using var plan=ReadOwnedHistoryJson(input);var p=plan.RootElement;
        Require(p.GetProperty("kind").GetString()=="owned-history-fixed-outside-inputs-v1"
            && p.GetProperty("files").GetArrayLength() is >0 and <=128,"Bound fixed outside inputs required.");
        bool haveInventory=File.Exists(Path.Combine(admission,"outside-inventory.json")),haveStatement=File.Exists(Path.Combine(admission,"outside-storage-baseline.json"));
        Require(haveInventory==haveStatement,"Incomplete prior outside preparation must be retained.");
        var rows=new List<object>();var ids=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total=0,totalReadBytes=0;
        foreach(var file in p.GetProperty("files").EnumerateArray())
        {
            string path=file.GetProperty("path").GetString();Require(CanonicalOwnedPath(path) && paths.Add(path),"Duplicate or noncanonical fixed input.");
            long length=new FileInfo(path).Length;totalReadBytes=checked(totalReadBytes+length);
            Require(length is >0 and <=268435456 && totalReadBytes<=1_000_000_000,"Aggregate fixed-input prehash bound.");
        }
        var writerRoots=p.GetProperty("writerRoots").EnumerateArray().Select(v=>v.GetString()).ToArray();
        Require(writerRoots.Length is >0 and <=4 && writerRoots.All(CanonicalOwnedPath) && writerRoots.All(Directory.Exists),"Bound canonical outside writer roots required.");
        var excluded=p.GetProperty("excludedRoots").EnumerateArray().Select(v=>v.GetString()).ToArray();
        string currentRoot=resource.RootElement.GetProperty("experimentRoot").GetString();
        Require(OwnedHistoryPathsDisjoint(excluded,currentRoot,OwnedHistoryRootLimit(kind.GetString())) && OwnedHistoryPathsDisjoint(writerRoots,currentRoot)
            && excluded.All(root=>OwnedHistoryPathsDisjoint(writerRoots,root)),"Preparation outside roots overlap separately charged work.");
        if(haveInventory)return; // Bounds above apply on reuse; final validation freshly verifies native/content applicability.
        var writerBaseline=ReplayStorage.Inventory(writerRoots,new ReplayFiles(),512,32);
        Require(writerBaseline.Values.Sum(e=>e.AllocatedBytes)<=64_000_000,"Outside metadata baseline exceeds64MB.");
        paths.Clear();
        foreach(var file in p.GetProperty("files").EnumerateArray())
        {
            string path=file.GetProperty("path").GetString(),expected=file.GetProperty("sha256").GetString();
            Require(paths.Add(path) && HashFile(path)==expected,
                "Changed or unbounded fixed outside input.");
            var extent=ReplayStorage.Observe(path);string id=extent.Volume+"/"+extent.Identity;
            if(ids.TryGetValue(id,out long previous))Require(previous==extent.AllocatedBytes,"Inconsistent outside alias allocation.");
            else{ids.Add(id,extent.AllocatedBytes);total=checked(total+extent.AllocatedBytes);}
            rows.Add(new{path,sha256=expected,extent});
        }
        foreach(var extent in writerBaseline.Values)
        {
            string id=extent.Volume+"/"+extent.Identity;
            if(ids.TryGetValue(id,out long previous))Require(previous==extent.AllocatedBytes,"Inconsistent writer/input alias allocation.");
            else{ids.Add(id,extent.AllocatedBytes);total=checked(total+extent.AllocatedBytes);}
        }
        Require(total is >0 and <=1_000_000_000,"Fixed outside files exceed their separate baseline cap.");
        Write(Path.Combine(admission,"outside-inventory.json"),new{result="complete-owned-history-fixed-outside-inventory",error=(string)null,
            planSha256=HashFile(input),nativeAllocatedBytes=total,rows,writerBaseline});
        Write(Path.Combine(admission,"outside-storage-baseline.json"),new{baselineCapBytes=1_000_000_000L,futureGrowthAllowanceBytes=OwnedHistoryOutsideFuture,
            observedNativeBytes=total,scope="Only explicitly declared fixed inputs plus oneGB future reports/copies outside the current run. Existing unrelated media remains physically charged by caller-free/floor, not credited or swept."});
    }

    static void ValidateOwnedHistoryOutside(CalibrationPlan plan)
    {
        Require(OwnedHistoryEnvelopeValid(plan) && OwnedHistoryPathsDisjoint(plan.RetainedRoots,plan.Root,OwnedHistoryRootLimit(plan.FreshCandidateAdmission)),"Owned-history namespace/envelope changed.");
        ValidateCapacityDocument(plan);
        ValidateOwnedHistoryPriorAttempt(plan);
        string statement=EvidenceRole(plan,"outside-storage-baseline"),inventory=EvidenceRole(plan,"outside-inventory"),inputs=EvidenceRole(plan,"outside-inventory-plan");
        Require(new[]{statement,inventory,inputs}.All(path=>HashFile(path)==plan.Files[path]),"Owned-history outside evidence changed.");
        using var sdoc=ReadOwnedHistoryJson(statement);using var idoc=ReadOwnedHistoryJson(inventory);using var pdoc=ReadOwnedHistoryJson(inputs);
        var s=sdoc.RootElement;var i=idoc.RootElement;var p=pdoc.RootElement;
        Require(p.GetProperty("kind").GetString()=="owned-history-fixed-outside-inputs-v1"
            && p.GetProperty("excludedRoots").EnumerateArray().Select(v=>v.GetString()).SequenceEqual(plan.RetainedRoots)
            && i.GetProperty("result").GetString()=="complete-owned-history-fixed-outside-inventory"
            && i.GetProperty("error").ValueKind==JsonValueKind.Null && i.GetProperty("planSha256").GetString()==HashFile(inputs)
            && s.GetProperty("baselineCapBytes").GetInt64()==1_000_000_000 && s.GetProperty("futureGrowthAllowanceBytes").GetInt64()==OwnedHistoryOutsideFuture,
            "Owned-history outside partition is incomplete.");
        var expected=p.GetProperty("files").EnumerateArray().ToDictionary(f=>f.GetProperty("path").GetString(),f=>f.GetProperty("sha256").GetString(),StringComparer.OrdinalIgnoreCase);
        Require(expected.Count is >0 and <=128 && i.GetProperty("rows").GetArrayLength()==expected.Count,"Fixed outside membership changed.");
        long sum=0;var ids=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in i.GetProperty("rows").EnumerateArray())
        {
            string path=row.GetProperty("path").GetString(),hash=row.GetProperty("sha256").GetString();
            Require(CanonicalOwnedPath(path) && seen.Add(path) && expected.TryGetValue(path,out var wanted) && wanted==hash && plan.Files.TryGetValue(path,out var bound) && bound==hash
                && !plan.RetainedRoots.Any(root=>path.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                && !path.StartsWith(Path.TrimEndingDirectorySeparator(plan.Root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Outside member overlaps a separately charged namespace or is unbound.");
            var extent=row.GetProperty("extent").Deserialize<ReplayFileExtent>();ReplayStorage.Validate(extent);
            var current=ReplayStorage.Observe(path);
            Require(current==extent && HashFile(path)==hash,"Fixed outside allocation/content changed since native baseline.");
            string id=extent.Volume+"/"+extent.Identity;
            if(ids.TryGetValue(id,out long prior))Require(prior==extent.AllocatedBytes,"Outside alias extent differs.");else{ids.Add(id,extent.AllocatedBytes);sum=checked(sum+extent.AllocatedBytes);}
        }
        string forecast=EvidenceRole(plan,"outside-outstanding-writes"),envelope=EvidenceRole(plan,"outside-forward-envelope");
        Require(HashFile(forecast)==plan.Files[forecast] && HashFile(envelope)==plan.Files[envelope],"Changed bounded outside forecast.");
        using var fdoc=ReadOwnedHistoryJson(forecast,65536);using var edoc=ReadOwnedHistoryJson(envelope,65536);var f=fdoc.RootElement;var e=edoc.RootElement;
        var writerRoots=p.GetProperty("writerRoots").EnumerateArray().Select(v=>v.GetString()).ToArray();
        Require(writerRoots.Length is >0 and <=4 && writerRoots.All(CanonicalOwnedPath) && writerRoots.All(Directory.Exists)
            && writerRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count()==writerRoots.Length,"Changed outside writer namespace.");
        bool Inside(string root,string path)=>path.Equals(root,StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        for(int n=0;n<writerRoots.Length;n++)
            Require(!Inside(writerRoots[n],plan.Root) && !Inside(plan.Root,writerRoots[n])
                && !plan.RetainedRoots.Any(root=>Inside(root,writerRoots[n]) || Inside(writerRoots[n],root))
                && !writerRoots.Take(n).Any(root=>Inside(root,writerRoots[n]) || Inside(writerRoots[n],root)),"Outside writer overlaps current/retained namespace.");
        var baseline=i.GetProperty("writerBaseline").Deserialize<Dictionary<string,ReplayFileExtent>>();
        Require(baseline.Count<=512 && baseline.All(pair=>CanonicalOwnedPath(pair.Key) && writerRoots.Any(root=>Inside(root,pair.Key))),"Invalid writer baseline membership.");
        foreach(var extent in baseline.Values)
        {
            ReplayStorage.Validate(extent);string id=extent.Volume+"/"+extent.Identity;
            if(ids.TryGetValue(id,out long previous))Require(previous==extent.AllocatedBytes,"Inconsistent baseline writer/input alias allocation.");
            else{ids.Add(id,extent.AllocatedBytes);sum=checked(sum+extent.AllocatedBytes);}
        }
        Require(sum is >0 and <=1_000_000_000 && sum==i.GetProperty("nativeAllocatedBytes").GetInt64()
            && sum==s.GetProperty("observedNativeBytes").GetInt64(),"Native outside allocation union differs.");
        var writerCurrent=ReplayStorage.Inventory(writerRoots,new ReplayFiles(),512,32);long accrued=0;
        var accruedIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var pair in writerCurrent)
            if((!baseline.TryGetValue(pair.Key,out var prior) || prior.Volume!=pair.Value.Volume || prior.Identity!=pair.Value.Identity || prior.AllocatedBytes!=pair.Value.AllocatedBytes)
                && accruedIds.Add(pair.Value.Volume+"/"+pair.Value.Identity))accrued=checked(accrued+pair.Value.AllocatedBytes);
        Require(f.GetProperty("kind").GetString()=="owned-history-outside-future-v1" && f.GetProperty("newProducerOrBuild").GetBoolean()==false
            && f.GetProperty("maximumAdditionalBytes").GetInt64()==OwnedHistoryOutsideFuture && f.GetProperty("charges").GetArrayLength()==writerRoots.Length,
            "Uncovered outside producer or charge membership.");
        var names=new HashSet<string>(StringComparer.Ordinal);var chargedRoots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long outstanding=0;
        foreach(var charge in f.GetProperty("charges").EnumerateArray())
        {
            string name=charge.GetProperty("name").GetString(),root=charge.GetProperty("root").GetString();long bytes=charge.GetProperty("maximumBytes").GetInt64();
            Require(!string.IsNullOrWhiteSpace(name) && names.Add(name) && CanonicalOwnedPath(root) && chargedRoots.Add(root)
                && writerRoots.Contains(root,StringComparer.OrdinalIgnoreCase) && bytes>0,"Invalid/duplicate outside writer charge.");
            outstanding=checked(outstanding+bytes);
        }
        Require(outstanding<=500_000_000 && e.GetProperty("kind").GetString()=="owned-history-outside-forward-v1"
            && e.GetProperty("forecastSha256").GetString()==HashFile(forecast) && e.GetProperty("baselinePlanSha256").GetString()==HashFile(inputs)
            && e.GetProperty("maximumAdditionalBytes").GetInt64()==OwnedHistoryOutsideFuture
            && e.GetProperty("reportReserveBytes").GetInt64()==64_000_000
            && OwnedHistoryFutureFits(accrued,outstanding,64_000_000),"Accrued/outstanding outside writers exceed their independent promise.");
    }

    internal static int PrepareOwnedHistoryOutsideEvidence(string admission)
    {
        using var resource=ReadOwnedHistoryJson(Path.Combine(admission,"resources.json"));
        Require(OwnedHistoryPreparationKind(resource.RootElement.GetProperty("freshCandidateAdmission").GetString()),"Wrong fixed-file preparation mode.");
        PrepareOwnedHistoryOutside(admission);Console.WriteLine("Prepared bounded fixed-file native outside evidence; no capture or media process.");return 0;
    }

    internal static int OwnedHistoryAdmissionChecks()
    {
        var p=new CalibrationPlan("D:\\current","unused","pure-owned-history",new(),Order,["D:\\retained"],0,0,MaximumChildren:61,
            BudgetBytes:28_000_000_000,StopBytes:27_000_000_000,DeadlineSeconds:5400,RetainedOfflineSeconds:0,ReuseCurrentMetadataHandles:true,
            DirectoryLimit:4096,OutputComponentBytes:2_000_000_000,RetainedBudgetBytes:4_000_000_000,FileLimit:16384,OfflineDeadlineSeconds:1200,
            OutsideBuildAllowanceBytes:2_000_000_000,FreezeRetainedAllocations:true,ReplayExporterRecipe:ReplaySaveExport.PolicyIdentity,ValidationChildren:18,
            CompositeQualification:true,CompositeManualForegroundHandoff:true,CompositeExplicitForegroundRelease:true,CopyFallbackReserveBytes:1_000_000_000)
            {FreshCandidateAdmission=OwnedHistoryAdmission,EvidenceRoles=new(){["fresh-candidate-qualification"]="unused"}};
        int count=0;void Check(bool condition){Require(condition,"Owned-history qualification admission control failed.");count++;}
        foreach (string kind in new[] { OwnedHistoryAdmission, OwnedHistoryRepairAdmission, DurationAdmission, DurationTrainingAdmission, DurationRetainedHistoryAdmission })
            Check(OwnedHistoryPreparationKind(kind));
        Check(!OwnedHistoryPreparationKind("unknown")); Check(!OwnedHistoryPreparationKind(null));
        Check(OwnedHistoryEnvelopeValid(p));Check(NewCohortBytes(p)==20_643_108_864);Check(QualificationFreeRequirement(p)==87_286_217_728);
        foreach(var invalid in new[]{p with{BudgetBytes=p.BudgetBytes+1},p with{StopBytes=p.StopBytes-1},p with{FloorBytes=p.FloorBytes-1},
            p with{RetainedBudgetBytes=p.RetainedBudgetBytes-1},p with{OutsideBuildAllowanceBytes=p.OutsideBuildAllowanceBytes-1},
            p with{AdditionalHistoricalReserveBytes=1},p with{CompositeTrainingRoot="old"},p with{Order=Order.Reverse().ToArray()},
            p with{MaximumChildren=62},p with{RetainedChildren=1},p with{DeadlineSeconds=5401},p with{FreshCandidateAdmission="unknown"}})Check(!OwnedHistoryEnvelopeValid(invalid));
        Check(OwnedHistoryPathsDisjoint(["D:\\retained"],"D:\\current"));Check(!OwnedHistoryPathsDisjoint(["D:\\current"],"D:\\current\\child"));
        Check(!OwnedHistoryPathsDisjoint(["D:\\retained","D:\\retained\\child"],"D:\\current"));
        Check(!OwnedHistoryPathsDisjoint(["D:\\retained\\..\\current"],"D:\\current\\child"));
        Check(!OwnedHistoryPathsDisjoint(["D:\\retained\\.\\child"],"D:\\current"));
        Check(!OwnedHistoryPathsDisjoint(["\\\\?\\D:\\retained"],"D:\\current"));
        Check(!OwnedHistoryPathsDisjoint(["D:\\"],"D:\\current"));
        Check(!OwnedHistoryPathsDisjoint(["D:/"],"D:\\current"));
        Check(!OwnedHistoryPathsDisjoint(["D:\\retained"],"D:\\"));
        var many=Enumerable.Range(0,33).Select(n=>@"D:\retained-"+n).ToArray();
        Check(OwnedHistoryPathsDisjoint(many.Take(8).ToArray(),@"D:\current"));
        Check(!OwnedHistoryPathsDisjoint(many.Take(9).ToArray(),@"D:\current"));
        Check(OwnedHistoryRootLimit(DurationRetainedHistoryAdmission)==32);
        foreach(string kind in new[]{OwnedHistoryAdmission,OwnedHistoryRepairAdmission,DurationAdmission,DurationTrainingAdmission,"unknown",null})
            Check(OwnedHistoryRootLimit(kind)==8);
        Check(OwnedHistoryPathsDisjoint(many.Take(32).ToArray(),@"D:\current",OwnedHistoryRootLimit(DurationRetainedHistoryAdmission)));
        Check(!OwnedHistoryPathsDisjoint(many,@"D:\current",OwnedHistoryRootLimit(DurationRetainedHistoryAdmission)));
        Check(!OwnedHistoryPathsDisjoint([@"D:\current\child"],@"D:\current",20));
        Check(!OwnedHistoryPathsDisjoint([@"D:\retained",@"D:\retained\child"],@"D:\current",20));
        Check(!OwnedHistoryPathsDisjoint(many.Take(32).ToArray(),@"D:\current",33));
        Check(checked(p.RetainedBudgetBytes+p.OutsideBuildAllowanceBytes+NewCohortBytes(p))<p.StopBytes);
        Check(OwnedHistoryFutureFits(436_000_000,500_000_000,64_000_000));
        Check(!OwnedHistoryFutureFits(436_000_001,500_000_000,64_000_000));
        Check(!OwnedHistoryFutureFits(-1,500_000_000,64_000_000));
        Check(!OwnedHistoryFutureFits(0,500_000_001,64_000_000));
        Check(!OwnedHistoryFutureFits(long.MaxValue,1,64_000_000));
        var repair=p with{FreshCandidateAdmission=OwnedHistoryRepairAdmission,BudgetBytes=32_000_000_000,StopBytes=31_000_000_000,RetainedBudgetBytes=8_000_000_000,
            EvidenceRoles=new(){["fresh-candidate-qualification"]="unused",["prior-failed-attempt"]="unused"}};
        Check(OwnedHistoryEnvelopeValid(repair));Check(QualificationFreeRequirement(repair)==87_286_217_728);
        Check(!OwnedHistoryEnvelopeValid(repair with{RetainedBudgetBytes=4_000_000_000}));
        Check(!OwnedHistoryEnvelopeValid(repair with{EvidenceRoles=p.EvidenceRoles}));
        Check(OwnedHistoryPriorLinks("hash","hash","old","old","new","new"));
        Check(!OwnedHistoryPriorLinks("hash","foreign","old","old","new","new"));
        Check(!OwnedHistoryPriorLinks("hash","hash","old","foreign","new","new"));
        Check(!OwnedHistoryPriorLinks("hash","hash","old","old","new","foreign"));
        Console.WriteLine($"Passed {count} pure owned-history qualification admission controls.");return 0;
    }
}
