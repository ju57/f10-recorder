using System.Globalization;
using System.Text;
using System.Text.Json;

namespace F10Recorder;

// Shared by actual fixture writes and generated TextWriter checks. Recording a
// measured row precedes policy rejection, so a failed bracket is never lost.
internal static class CalibrationReferencePhase
{
    internal const string Identity="after-validated-foreground-request-v1";
    internal const long PreAdmissionMaximumBytes=2097152;
    internal static void CheckRecordingDuringSave(bool stopped,CaptureHealthSnapshot health)
    {
        if(stopped||!health.Healthy)throw new IOException("Recording stopped or capture/audio health failed during the primary save.");
    }
    internal static void CheckRecordingAtPublication(bool stopped,CaptureHealthSnapshot atPress,CaptureHealthSnapshot atPublication,double requestedEnd)
    {
        CheckRecordingDuringSave(stopped,atPublication);
        if(!atPress.Healthy || !(atPublication.CompletedSegmentEnd>atPress.CompletedSegmentEnd && atPublication.CompletedSegmentEnd>=requestedEnd))
            throw new IOException("The open-tail save requires newly completed recording history through the requested end.");
    }
    internal static void RequireNormalFixtureExit((bool Forced,int Code) exit)
    {
        if(exit.Forced||exit.Code!=0)
            throw new IOException("Fixture did not exit normally; source is not eligible.");
    }
    internal static void CloseBeforePairedExport(bool captureProducersAbsent,Func<(bool Forced,int Code)> close,Action verifyReference)
    {
        if(!captureProducersAbsent)
            throw new IOException("Capture and audio must be drained before closing the source fixture.");
        RequireNormalFixtureExit(close());
        verifyReference();
    }
    internal static byte[] DiagnosticSummaryBytes(object value)
    {byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(value);if(bytes.Length>16384)throw new IOException("Diagnostic summary16KiB bound before write");return bytes;}
    internal sealed class TextBudget(long maximum)
    {
        internal long Bytes {get;private set;}
        internal void Write(TextWriter writer,string text)
        {
            int count=Encoding.UTF8.GetByteCount(text);
            if(maximum is <1 or >PreAdmissionMaximumBytes||count>maximum-Bytes)throw new IOException("Pre-admission diagnostic byte ceiling before write");
            Bytes+=count;writer.Write(text); // Conservative charge even if a writer partially fails.
        }
    }
    internal static void WritePaint(TextWriter writer,int id,long before,long after,long frequency,bool qualified,TextBudget budget=null)
    {
        if(id<0||before<=0||after<before||frequency<=0)throw new IOException("Invalid visual publication bracket");
        string line=FormattableString.Invariant($"{id},{before},{after}")+writer.NewLine;
        if(budget==null)writer.Write(line);else budget.Write(writer,line);
        if(qualified&&(after-before)/(double)frequency>.025)
        {writer.Flush();throw new IOException("Visual compositor bracket exceeds25ms.");}
    }
    internal static void ValidatePaintBrackets(long[][] rows,double frequency,int? lastRequiredMarker)
    {
        if(rows.Length is <=2 or >40000 || rows.Any(r=>r.Length!=3 || r[0] is <0 or >=0x1000000 || r[1]<=0 || r[2]<r[1])
            || rows.Select(r=>r[0]).Distinct().Count()!=rows.Length
            || lastRequiredMarker.HasValue && !rows.Any(r=>r[0]==lastRequiredMarker.Value)
            || rows.Any(r=>(!lastRequiredMarker.HasValue || r[0]<=lastRequiredMarker.Value) && (r[2]-r[1])/frequency>.025))
            throw new IOException("Unique visual publication brackets within 25 ms required through the final display witness.");
    }
    internal static void ValidateArm(string phase,long requested,long arm,long ready,double epoch,
        IEnumerable<long> paints,IEnumerable<double> clockRows)
    {
        var paintRows=paints.ToArray();var audioRows=clockRows.ToArray();
        if(phase!=Identity||requested<=0||arm<requested||ready<arm||!double.IsFinite(epoch)||epoch<ready
            ||paintRows.Length==0||audioRows.Length==0||paintRows.Any(q=>q<arm)||audioRows.Any(q=>!double.IsFinite(q)||q<arm))
            throw new IOException("Qualified reference phase/request/arm/Ready/source row boundary changed");
    }
}
