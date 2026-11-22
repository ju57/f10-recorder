/* Link the exact native implementation. CPU frames exercise ownership and timer
 * behavior without creating a graphics device. IDs are read only after picking. */
#include <inttypes.h>
#include <stdio.h>
#include "libavfilter/vsrc_ddagrab.c"
#include "libavfilter/buffersrc.h"
#include "libavfilter/buffersink.h"
static int interrupt(void *opaque) { return 0; }
const AVIOInterruptCB int_cb={interrupt,NULL};
#define CHECK(x) do {if(!(x)){fprintf(stderr,"check failed at %d: %s\n",__LINE__,#x);exit(1);}}while(0)
static AVFrame *picture(int id)
{
    AVFrame *f=av_frame_alloc();CHECK(f);
    f->format=AV_PIX_FMT_GRAY8; f->width=4; f->height=1;
    CHECK(av_frame_get_buffer(f,0)>=0);
    memcpy(f->data[0],&id,sizeof(id));
    return f;
}
static int image(AVFrame *f){int i;memcpy(&i,f->data[0],sizeof(i));return i;}

static void replay(void)
{
    const int64_t epoch=1000000000;
    DelaySelector s={0};
    int64_t end,t,p;
    CHECK(scanf("%" SCNd64 " %" SCNd64,&s.hz,&end)==2 && s.hz>60 && end>0);
    s.epoch=epoch;
    int n=0,has=scanf("%" SCNd64 " %" SCNd64,&t,&p);
    for(int64_t slot=0;;slot++) {
        int64_t due=delay_due(&s,slot);
        if(due-epoch>end) break;
        while(has==2 && t<=due-epoch) {
            CHECK(delay_publish(&s,picture(n++),p+epoch,0,t+epoch)>=0);
            has=scanf("%" SCNd64 " %" SCNd64,&t,&p);
        }
        AVFrame *f=NULL;
        CHECK(delay_pick(&s,slot,due,&f)==0);
        if(f){CHECK(f->pts==slot && f->duration==1);printf("%"PRId64" %"PRId64" %d\n",due-epoch,slot,image(f));av_frame_free(&f);}
    }
    delay_clear(&s);
}

static void selector_cases(void)
{
    DelaySelector s={.epoch=1000000,.hz=6000};
    AVFrame *f=NULL;
    CHECK(delay_pick(&s,0,s.epoch+100,&f)==0 && !f);
    CHECK(delay_publish(&s,picture(1),s.epoch-20,0,s.epoch+101)==0);
    CHECK(delay_pick(&s,0,s.epoch+250,&f)==0 && !f); /* late wake cannot backfill slot 0 */
    CHECK(delay_pick(&s,1,s.epoch+250,&f)==0 && image(f)==1);av_frame_free(&f);
    CHECK(delay_publish(&s,picture(2),s.epoch-20,s.epoch+270,s.epoch+280)==0);
    CHECK(delay_pick(&s,2,s.epoch+300,&f)==0 && image(f)==1);av_frame_free(&f);
    CHECK(delay_pick(&s,3,s.epoch+400,&f)==0 && image(f)==2);av_frame_free(&f);
    CHECK(delay_publish(&s,picture(3),s.epoch+440,s.epoch+270,s.epoch+455)==0);
    CHECK(delay_pick(&s,4,s.epoch+500,&f)==0 && image(f)==3);AVFrame *owned=f;
    CHECK(delay_publish(&s,picture(4),s.epoch+540,s.epoch+270,s.epoch+550)==0);
    CHECK(delay_pick(&s,5,s.epoch+600,&f)==0 && image(f)==4 && image(owned)==3);
    av_frame_free(&f);av_frame_free(&owned);
    CHECK(delay_pick(&s,6,s.epoch+699,&f)<0);
    for(int i=0;i<DELAY_INPUTS;i++)CHECK(delay_publish(&s,picture(10+i),s.epoch+610+i,0,s.epoch+650+i)==0);
    f=picture(250);CHECK(delay_publish(&s,f,s.epoch+690,0,s.epoch+699)==AVERROR(ENOBUFS));av_frame_free(&f);
    delay_clear(&s);
}

/* Use the real Windows waitable timer with no additional picture arriving.
 * Hold delivery, then close as if the consumer reached EOF. */
static void real_timer(void)
{
    DelayDevice d={0};
    LARGE_INTEGER hz;CHECK(QueryPerformanceFrequency(&hz));
    d.select.hz=hz.QuadPart;d.select.epoch=queue_now();
    InitializeCriticalSection(&d.lock);d.initialized=1;
    d.stop=CreateEventW(NULL,TRUE,FALSE,NULL);d.available=CreateEventW(NULL,TRUE,FALSE,NULL);
    d.timer=CreateWaitableTimerExW(NULL,NULL,CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,TIMER_ALL_ACCESS);
    CHECK(d.timer && d.stop && d.available);
    CHECK(delay_publish(&d.select,picture(42),d.select.epoch,0,queue_now())==0);
    d.picker=(HANDLE)_beginthreadex(NULL,0,delay_timer,&d,0,NULL);CHECK(d.picker);
    CHECK(WaitForSingleObject(d.available,500)==WAIT_OBJECT_0);
    Sleep(95);
    SetEvent(d.stop);CHECK(WaitForSingleObject(d.picker,300)==WAIT_OBJECT_0);
    CHECK(d.failed==0 && d.count>=5);
    for(unsigned i=0;i<d.count;i++){
        CHECK(image(d.out[i])==42 && d.out[i]->pts==i);
        CHECK(d.out[i]->duration==1 && av_cmp_q(d.out[i]->time_base,(AVRational){1,60})==0);
    }
    unsigned n=d.count;Sleep(35);CHECK(n==d.count);
    printf("realTimerQuietFrames=%u; boundedEOF=true\n",n);
    delay_close(&d);
}
/* Drive exactly the same due/selection/dispatch code past the pilot's old
 * six-minute cap, using a non-integral QPC period and bounded live queues. */
static void sustained_capture(void)
{
    DelayDevice d = {0};
    d.available = CreateEventW(NULL,TRUE,FALSE,NULL);
    CHECK(d.available);
    d.select.hz = 10000003;
    d.select.epoch = 1234567890;
    CHECK(delay_publish(&d.select,picture(31),d.select.epoch,0,d.select.epoch)==0);
    for (int64_t slot=0;slot<432000;slot++) {
        int64_t due=delay_due(&d.select,slot);
        CHECK(due==d.select.epoch+((slot+1)*d.select.hz+59)/60);
        CHECK(delay_emit(&d,due)==0 && d.count==1 && d.slot==slot+1);
        AVFrame *f=delay_take(&d);
        CHECK(image(f)==31 && f->pts==slot && f->duration==1 && d.count==0);
        av_frame_free(&f);
    }
    for (int i=0;i<DELAY_OUTPUTS;i++) CHECK(delay_emit(&d,delay_due(&d.select,d.slot))==0);
    CHECK(delay_emit(&d,delay_due(&d.select,d.slot))==AVERROR(ENOBUFS));
    CHECK(d.count==DELAY_OUTPUTS && d.slot==432000+DELAY_OUTPUTS);
    while(d.count){AVFrame *f=delay_take(&d);av_frame_free(&f);}
    CHECK(delay_emit(&d,delay_due(&d.select,d.slot)+d.select.hz)==AVERROR(ETIMEDOUT));
    delay_clear(&d.select); CloseHandle(d.available);
}

/* A fresh 60 Hz frame must be available immediately from the configured
 * setpts/fps graph. The legacy path still waits for a second frame. */
static void clock_graph(int already_clocked)
{
    AVFilterGraph *graph=avfilter_graph_alloc();
    AVFilterContext *src=NULL,*clock=NULL,*fps=NULL,*sink=NULL;
    CHECK(graph);
    const char *source=already_clocked?
        "video_size=4x1:pix_fmt=gray:time_base=1/60:pixel_aspect=1/1:frame_rate=60/1":
        "video_size=4x1:pix_fmt=gray:time_base=1/1000000:pixel_aspect=1/1:frame_rate=240/1";
    CHECK(avfilter_graph_create_filter(&src,avfilter_get_by_name("buffer"),"frames",source,NULL,graph)==0);
    CHECK(avfilter_graph_create_filter(&clock,avfilter_get_by_name("setpts"),"clock",
        "'if(eq(TB,1/60),PTS,PTS+if(eq(N,0),100000-PTS,PREV_OUTPTS-PREV_INPTS))'",NULL,graph)==0);
    CHECK(avfilter_graph_create_filter(&fps,avfilter_get_by_name("fps"),"out",
        "fps=60:round=near:f10_passthrough_clocked=1",NULL,graph)==0);
    CHECK(avfilter_graph_create_filter(&sink,avfilter_get_by_name("buffersink"),"sink",NULL,NULL,graph)==0);
    CHECK(avfilter_link(src,0,clock,0)==0 && avfilter_link(clock,0,fps,0)==0 && avfilter_link(fps,0,sink,0)==0);
    CHECK(avfilter_graph_config(graph,NULL)==0);
    AVFrame *in=picture(101),*out=av_frame_alloc();CHECK(out);
    in->pts=already_clocked?23:333;
    CHECK(av_buffersrc_add_frame(src,in)==0);
    int first=av_buffersink_get_frame(sink,out);
    if(already_clocked) {
        CHECK(first==0 && out->pts==23 && image(out)==101);
        av_frame_unref(out);
        CHECK(av_buffersink_get_frame(sink,out)==AVERROR(EAGAIN));
        if (already_clocked==2) {
            av_frame_free(&in);in=picture(102);in->pts=25;
            CHECK(av_buffersrc_add_frame(src,in)==0 && av_buffersink_get_frame(sink,out)==AVERROR_INVALIDDATA);
        } else {
            CHECK(av_buffersrc_close(src,24,0)==0 && av_buffersink_get_frame(sink,out)==AVERROR_EOF);
        }
    } else {
        CHECK(first==AVERROR(EAGAIN));
        av_frame_free(&in);in=picture(102);in->pts=333+16667;
        CHECK(av_buffersrc_add_frame(src,in)==0 && av_buffersink_get_frame(sink,out)==0);
        CHECK(out->pts==6 && image(out)==101);
        av_frame_unref(out);
        CHECK(av_buffersrc_close(src,333+33334,0)==0);
        CHECK(av_buffersink_get_frame(sink,out)==0 && out->pts==7 && image(out)==102);
        av_frame_unref(out);CHECK(av_buffersink_get_frame(sink,out)==AVERROR_EOF);
    }
    av_frame_free(&in); av_frame_free(&out); avfilter_graph_free(&graph);
}

static void surfaces(void)
{
    DdagrabContext d={.framerate={240,1},.width=1920,.height=1080,.dup_frames=1};
    D3D11_TEXTURE2D_DESC desc={.Width=1920,.Height=1080,.MipLevels=1,.ArraySize=1,
        .Format=DXGI_FORMAT_B8G8R8A8_UNORM,.SampleDesc={1,0}};
    CHECK(delay_surface_supported(&d,&desc));
    desc.Format=DXGI_FORMAT_R10G10B10A2_UNORM;CHECK(!delay_surface_supported(&d,&desc));
    desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.Width=2560;CHECK(!delay_surface_supported(&d,&desc));
    desc.Width=1920;desc.ArraySize=2;CHECK(!delay_surface_supported(&d,&desc));
    desc.ArraySize=1;d.offset_x=1;CHECK(!delay_surface_supported(&d,&desc));
    d.offset_x=0;d.framerate.num=60;CHECK(!delay_surface_supported(&d,&desc));
}

int main(int argc,char **argv)
{
    if(argc==2 && !strcmp(argv[1],"--replay")) {replay();return 0;}
    selector_cases(); real_timer(); sustained_capture(); surfaces(); clock_graph(1); clock_graph(0); clock_graph(2);
    /* Exercise AVOptions on an allocated real desktop filter without opening DXGI. */
    AVFilterGraph *graph=avfilter_graph_alloc();CHECK(graph);
    AVFilterContext *c=avfilter_graph_alloc_filter(graph,&ff_vsrc_ddagrab.p,"delay");CHECK(c);
    CHECK(av_opt_set_int(c->priv,"f10_delay_epoch",123456789,0)==0);
    DdagrabContext *dda=c->priv;
    CHECK(dda->class==&ddagrab_class && dda->delay_epoch==123456789 && !dda->queue_opt);
    D3D11_TEXTURE2D_DESC desc={.Width=1920,.Height=1080,.Format=DXGI_FORMAT_B8G8R8A8_UNORM,
        .MipLevels=1,.ArraySize=1,.SampleDesc={1,0}};
    dda->width=1920;dda->height=1080;dda->framerate=(AVRational){240,1};dda->dup_frames=1;
    CHECK(!dda->require_delay && delay_select_surface(c,&desc)==1);
    desc.Width=2560;
    CHECK(av_opt_set_int(dda,"f10_require_delay",1,0)==0 && delay_select_surface(c,&desc)==AVERROR(ENOTSUP));
    CHECK(dda->delay_epoch==123456789 && !dda->delay.initialized);
    CHECK(av_opt_set_int(dda,"f10_require_delay",0,0)==0 && delay_select_surface(c,&desc)==0);
    CHECK(dda->delay_epoch==0 && !dda->delay.initialized);
    avfilter_graph_free(&graph);
    puts("two simulated hours (432000 slots), bounded overload, cursor, quiet input, precise PTS, surface eligibility, clocked first-frame and legacy clock/EOF passed");
    return 0;
}
