/* Windows device-free regression check. Build against the patched FFmpeg source
 * and configured static libraries. The filter is allocated but never configured
 * with a display device, so its capture callback must never run. */
#include "libavfilter/vsrc_ddagrab.c"

const AVIOInterruptCB int_cb = {0};

static int unexpected_poll(void *opaque)
{
    abort();
}

static void check_options(DdagrabContext *context)
{
    int64_t value = -1;
    if (context->class != &ddagrab_class ||
        av_opt_get_int(context, "f10_queue", 0, &value) < 0 || value != 1)
        abort();
}

int main(void)
{
    AVFilterGraph *graph = avfilter_graph_alloc();
    AVFilterContext *filter = graph ? avfilter_graph_alloc_filter(graph, &ff_vsrc_ddagrab.p, "layout") : NULL;
    if (!filter || av_opt_set_int(filter->priv, "f10_queue", 1, 0) < 0)
        return 1;
    DdagrabContext *context = filter->priv;
    check_options(context);
    if (queue_start(&context->queue, filter, unexpected_poll) < 0)
        return 1;
    EnterCriticalSection(&context->queue.data);
    check_options(context);
    LeaveCriticalSection(&context->queue.data);
    queue_stop(&context->queue);
    check_options(context);
    avfilter_graph_free(&graph);
    puts("PASS: real ddagrab AVOptions before queue start, while locked, and after queue teardown; no device");
    return 0;
}
