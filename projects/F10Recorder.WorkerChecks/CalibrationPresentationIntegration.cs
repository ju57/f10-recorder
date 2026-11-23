using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace F10Recorder;

internal sealed record PresentationPlanBinding(string Path,string Hash,PresentationMethod Spec)
{
    internal static PresentationPlanBinding For(CalibrationPlan plan)
    {
        if(plan.EvidenceRoles?.TryGetValue(PresentationMethod.Role,out string path)!=true)return null;
        if(plan.FreshCandidateAdmission is not ReplayCalibrationRun.SameCaptureContextAdmission and not ReplayCalibrationRun.FiveMinuteStorageAdmission and not ReplayCalibrationRun.SameCaptureRetainedAdmission
            ||!plan.Files.TryGetValue(path,out string hash))throw new IOException("Presentation reference only belongs to pinned same-C00 qualification");
        return new(path,hash,PresentationMethod.Read(path,hash));
    }
}
internal static class CalibrationPresentationIntegration
{
    internal static readonly string[] Files=[CalibrationPresentationReference.TraceName,CalibrationPresentationReference.ClosureName,
        CalibrationPresentationReference.ReadyName,CalibrationPresentationReference.RequestName,CalibrationForegroundRelease.FileName,
        "fixture-ready.json","foreground-admission.json","foreground-handoff-complete.json","fixture-exit.json"];
    internal static void ValidateStart(PresentationStart request,CalibrationForegroundRelease release,PresentationPlanBinding binding,int pid,long hwnd,bool visible,bool foreground)
    {
        if(request.MethodHash!=binding.Hash||request.Pid!=pid||request.Hwnd!=hwnd||request.Label!="H03"||request.Nonce!=release.Nonce
            ||release.Pid!=pid||release.Hwnd!=hwnd||release.Label!="H03"||string.IsNullOrWhiteSpace(request.Nonce)||request.RequestedQpc<=0||!visible||!foreground)
            throw new IOException("Exact foreground release required before independent desktop observation");
    }
    [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
    internal static T AfterValidatedForeground<T>(PresentationStart request,CalibrationForegroundRelease release,PresentationPlanBinding binding,int pid,long hwnd,
        Func<(bool Visible,bool Foreground)> state,Action<PresentationStart> startAudio,Func<T> startNative)
    {
        var first=state();ValidateStart(request,release,binding,pid,hwnd,first.Visible,first.Foreground);
        startAudio(request); // Must return actual audio Ready before native construction.
        var second=state();ValidateStart(request,release,binding,pid,hwnd,second.Visible,second.Foreground);
        return startNative();
    }
    internal static CalibrationPresentationReference TryStart(string root,PresentationPlanBinding binding,int pid,IntPtr hwnd,Action<PresentationStart> startAudio)
    {
        string path=System.IO.Path.Combine(root,CalibrationPresentationReference.RequestName);
        if(!System.IO.File.Exists(path))return null;
        var request=CalibrationPresentationReference.ReadJson<PresentationStart>(path);
        var release=CalibrationPresentationReference.ReadJson<CalibrationForegroundRelease>(System.IO.Path.Combine(root,CalibrationForegroundRelease.FileName));
        return AfterValidatedForeground(request,release,binding,pid,hwnd.ToInt64(),()=> (Native.IsWindowVisible(hwnd),GetForegroundWindow()==hwnd),
            r=>startAudio(r),()=>new CalibrationPresentationReference(binding.Spec,binding.Hash,request,
            ()=>new NativePresentationReader(binding.Spec.OutputIndex,binding.Spec.Left,binding.Spec.Top,binding.Spec.Width,binding.Spec.Height,binding.Spec.DeviceName),
            ()=>new FilePresentationSink(System.IO.Path.Combine(root,CalibrationPresentationReference.TraceName),binding.Spec.MaximumBytes),
            ready=>CalibrationPresentationReference.WriteJson(System.IO.Path.Combine(root,CalibrationPresentationReference.ReadyName),ready),
            closure=>CalibrationPresentationReference.WriteJson(System.IO.Path.Combine(root,CalibrationPresentationReference.ClosureName),closure)));
    }
    internal static void RequestAndWait(string root,PresentationPlanBinding binding,int pid,IntPtr hwnd,Action health,Func<bool> exited)
    {
        var release=CalibrationPresentationReference.ReadJson<CalibrationForegroundRelease>(System.IO.Path.Combine(root,CalibrationForegroundRelease.FileName));
        if(release.Pid!=pid||release.Hwnd!=hwnd.ToInt64()||release.Label!="H03"||!release.IsReleased(root)||GetForegroundWindow()!=hwnd)
            throw new IOException("Verified controlled foreground release required");
        var request=new PresentationStart(binding.Hash,"H03",pid,hwnd.ToInt64(),release.Nonce,Stopwatch.GetTimestamp(),QualifySavedPictures:true);
        CalibrationPresentationReference.WriteJson(System.IO.Path.Combine(root,CalibrationPresentationReference.RequestName),request);
        var watch=Stopwatch.StartNew();string readyPath=System.IO.Path.Combine(root,CalibrationPresentationReference.ReadyName);
        while(!System.IO.File.Exists(readyPath))
        {health();if(exited()||watch.Elapsed.TotalSeconds>binding.Spec.ReadySeconds||GetForegroundWindow()!=hwnd)throw new IOException("Independent presentation Ready deadline/foreground failure before recorder start");Thread.Sleep(25);}
        var ready=CalibrationPresentationReference.ReadJson<PresentationReady>(readyPath);
        if(ready.Request!=request||ready.MethodHash!=binding.Hash||ready.Method!=binding.Spec.Method||ready.Frequency!=Stopwatch.Frequency
            ||ready.ReadyQpc<request.RequestedQpc||ready.ReadyQpc>Stopwatch.GetTimestamp()||ready.DeviceName!=binding.Spec.DeviceName
            ||ready.Left!=binding.Spec.Left||ready.Top!=binding.Spec.Top||ready.Width!=binding.Spec.Width||ready.Height!=binding.Spec.Height)
            throw new IOException("Independent presentation Ready identity/output/clock mismatch");
    }
    internal static object HashBinding(string sourceCell,PresentationMethod spec)
        =>new {method=spec.Method,files=Files.ToDictionary(n=>n,n=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(CalibrationPresentationReference.ReadBounded(System.IO.Path.Combine(sourceCell,n),n==CalibrationPresentationReference.TraceName?20971520:16384))))};
    internal static void ValidateBinding(string sourceCell,JsonElement binding,PresentationMethod spec)
    {
        if(binding.GetProperty("method").GetString()!=spec.Method)throw new IOException("Native presentation binding method mismatch");
        var files=binding.GetProperty("files");
        if(!files.EnumerateObject().Select(p=>p.Name).ToHashSet().SetEquals(Files))throw new IOException("Exact immutable presentation reference readset required");
        foreach(var name in Files)
            if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(CalibrationPresentationReference.ReadBounded(System.IO.Path.Combine(sourceCell,name),name==CalibrationPresentationReference.TraceName?20971520:16384)))!=files.GetProperty(name).GetString())throw new IOException("Native reference file changed");
    }
    // Invoked while the accepted reference metadata owner is held, before any
    // retained input copy, exporter or decoder can run. No native/media call.
    internal static void ValidateRetainedMethod(PresentationPlanBinding binding,PresentationClosure accepted)
    {
        if(binding==null||accepted==null||accepted.Method!=binding.Spec.Method||accepted.MethodHash!=binding.Hash
            ||accepted.DeviceName!=binding.Spec.DeviceName||accepted.Left!=binding.Spec.Left||accepted.Top!=binding.Spec.Top
            ||accepted.Width!=binding.Spec.Width||accepted.Height!=binding.Spec.Height)
            throw new IOException("Retained pinned native method/output differs from independently accepted C00");
    }
}
