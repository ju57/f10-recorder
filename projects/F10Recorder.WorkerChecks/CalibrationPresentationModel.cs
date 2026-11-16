#nullable enable
using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

record PresentationRow(long Ordinal,int Id,long Present,long Before,long After,uint Accumulated,bool Coalesced,bool Protected,bool Released);
record PresentationBirth(int Id,long Present);

// Future production caller must feed only rows created while owning the SAME
// DXGI frame-info/texture and release it before committing the row. Pure here.
sealed class PresentationTimeline
{
    readonly Dictionary<int,PresentationBirth> births=[];
    readonly Dictionary<int,PresentationBirth> nextBirths=[];
    readonly long frequency;
    long ordinal,present,lastAfter;
    int? id;
    int transitions;
    bool failed,complete;
    public bool Ready=>!failed&&transitions>=2;
    public PresentationBirth Birth(int value)=>births[value];
    public PresentationTimeline(long frequency)
    { if(frequency<=0)throw new IOException("Positive native QPC frequency required");this.frequency=frequency; }
    void Require(bool condition,string error)
    { if(!condition){failed=true;throw new IOException(error);} }
    public void Add(PresentationRow row)
    {
        Require(!failed&&!complete,"Sticky failure/closed timeline");
        // v4 proves the fixture marker ROI from the acquired texture. The global
        // protected-content mask flag remains diagnostic; it proves no full-frame clearance.
        Require(row.Ordinal==ordinal+1&&row.Before>0&&row.Before>=lastAfter&&row.Before<=row.After&&row.Released,"Frame ownership/ordinal/clock failure");
        ordinal=row.Ordinal;lastAfter=row.After;
        if(row.Present==0&&row.Accumulated==0)return; // Explicit pointer-only event; not an image presentation.
        Require(row.Present>present&&row.Present<=row.After&&row.Accumulated>0&&row.Id is >=0 and <0x1000000,"Invalid native presentation row");
        bool bootstrap=id==null;
        Require(bootstrap||row.Id>=id,"Reused/backward visual ID");
        if(!Ready&&row.Accumulated>1)
        {
            // Raw rows and ownership/ordinal/native-clock checks remain intact.
            // No birth in this unproved prefix can qualify a captured picture.
            births.Clear();nextBirths.Clear();transitions=0;
            id=row.Id;present=row.Present;return;
        }
        // v5 is restricted to the pinned forward-only/no-wrap fixture: equal IDs
        // cannot hide a marker transition. Keep its ORIGINAL birth and next link.
        Require(bootstrap||row.Accumulated==1||row.Id==id,"Unobserved/coalesced marker transition cannot qualify");
        if(!bootstrap&&row.Id!=id)
        {
            var birth=new PresentationBirth(row.Id,row.Present);
            // A paint may never be presented. Native AccumulatedFrames==1
            // proves no missed DESKTOP image; link to next ACTUAL observed ID.
            if(births.ContainsKey(id!.Value))nextBirths.Add(id.Value,birth);
            births.Add(row.Id,birth);transitions++;
        }
        // Bootstrap ID has no proved birth and is deliberately NOT recorded.
        id=row.Id;present=row.Present;
    }
    public void Complete(bool observerJoined,bool nativeOwnersClosed,bool traceComplete)
    { Require(!complete&&!failed&&Ready&&observerJoined&&nativeOwnersClosed&&traceComplete,"Incomplete presentation closure");complete=true; }
    public void CheckFrame(int sourceId,double sourceQpc,double bridgeSeconds)
    {
        Require(complete&&!failed&&double.IsFinite(sourceQpc)&&sourceQpc>=0&&double.IsFinite(bridgeSeconds)&&bridgeSeconds>=0&&bridgeSeconds<=.002,"Closed finite independent presentation trace/original bridge bound required");
        bool haveCurrent=births.TryGetValue(sourceId,out var current),haveNext=nextBirths.TryGetValue(sourceId,out var next);
        Require(haveCurrent&&haveNext,"Current and next independently observed presentation required");
        double tolerance=frequency*(1d/60+bridgeSeconds);
        Require(double.IsFinite(tolerance),"Finite original one-frame+bridge tolerance required");
        Require(sourceQpc>=current!.Present-tolerance&&sourceQpc<=next!.Present+tolerance,"Source picture outside unchanged one-frame+bridge presentation bounds");
    }
}

static class BarcodeRaster
{
    public static int Decode(byte[] bgra,int pitch)
    {
        if(pitch<576*4||pitch>8192||bgra.Length!=pitch*48)throw new IOException("Bounded BGRA8 ROI required");
        int id=0;
        for(int bit=0;bit<24;bit++)
        {
            int Cell(int row)
            {
                long sum=0;
                for(int y=row*24;y<(row+1)*24;y++)for(int x=bit*24;x<(bit+1)*24;x++)
                {int p=y*pitch+x*4;sum+=bgra[p]+bgra[p+1]+bgra[p+2];}
                return (int)(sum/(24*24*3));
            }
            int a=Cell(0),b=Cell(1);
            if(!(a<=70||a>=180)||!(b<=70||b>=180)||(a>=180)==(b>=180))throw new IOException("Ambiguous native barcode/complement");
            if(a>=180)id|=1<<bit;
        }
        return id;
    }
}

