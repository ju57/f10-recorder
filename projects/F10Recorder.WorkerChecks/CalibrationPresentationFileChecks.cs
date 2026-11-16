using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Separately admitted native FILE controls. Never called by generated controls.
internal static class CalibrationPresentationFileChecks
{
    internal static int Run(string root)
    {
        root=Path.GetFullPath(root);
        if(!root.StartsWith(Path.GetFullPath("artifacts")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Directory.Exists(root))throw new IOException("Fresh ignored native-file fixture required");
        if(ReplayStorage.ObserveCapacity(Path.GetDirectoryName(root)).AvailableBytes<60000000000)throw new IOException("60GB file-control floor");
        Directory.CreateDirectory(root);var watch=Stopwatch.StartNew();var checks=new List<string>();
        void Need(bool ok,string name){if(!ok)throw new IOException(name);checks.Add(name);}
        bool Reject(Action body){try{body();return false;}catch(IOException){return true;}}
        string trace=Path.Combine(root,"trace.csv");var sink=new FilePresentationSink(trace,65536);
        try
        {
            for(int i=1;i<=3;i++)sink.Write(new(i,100+i,1000+i*100,999+i*100,1001+i*100,1,false,false,true));
            Need(Reject(()=>{using var writer=new FileStream(trace,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);}),"real-trace-owner-denies-other-writer");
            Need(Reject(()=>File.Move(trace,trace+".moved")),"real-trace-owner-denies-rename");
            sink.FlushDurable();Need(sink.Rows==3&&sink.Bytes<=65536&&sink.Hash?.Length==64,"real-trace-flush-hash-counts");
        }
        finally{sink.Dispose();}
        Need(sink.Closed&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(trace)))==sink.Hash,"real-trace-owner-closed-stable-bytes");
        foreach(string name in new[]{CalibrationPresentationReference.ReadyName,CalibrationPresentationReference.RequestName})
        {
            string path=Path.Combine(root,name);FileStream observer=null;
            try
            {
                CalibrationPresentationReference.WriteJson(path,new{state="generated-native-file-fixture",nativeDesktopCalls=0},(pending,final)=>{
                    Need(File.Exists(pending)&&!File.Exists(final),"final-hidden-during-real-partial-write-"+name);
                    Need(CalibrationFixturePublication.IsVolatile(pending)&&CalibrationFixturePublication.IsVolatile(final),"real-publisher-volatile-names-"+name);
                    observer=new FileStream(pending,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                });
                Need(File.Exists(path)&&!File.Exists(path+".pending"),"atomic-final-visible-after-close-"+name);
                Need(CalibrationPresentationReference.ReadJson<JsonElement>(path).GetProperty("state").GetString()=="generated-native-file-fixture","complete-real-poll-document-"+name);
            }
            finally{observer?.Dispose();}
        }
        Need(watch.Elapsed.TotalSeconds<30,"native-file-work-bound");
        CalibrationPresentationReference.WriteJson(Path.Combine(root,"result.json"),new{result="passed-native-file-only-presentation-controls",checks,count=checks.Count,seconds=watch.Elapsed.TotalSeconds,
            nativeDesktopCalls=0,deviceCalls=0,audioCalls=0,codecCalls=0,newCaptures=0,mediaRead=false,generatedFixtureOnly=true});
        Console.WriteLine($"Passed {checks.Count} native-file-only presentation controls;no hardware/media acquisition.");return 0;
    }
}
