using System.Diagnostics;

namespace F10Recorder;

internal sealed record ReplayManagedMuxReceipt(int ParentPid, DateTime ParentStartedUtc, bool Closed, bool InputHashesComplete,
    string State, int Sources, long SourceBytes, double Milliseconds, double ParentCpuMilliseconds,
    ReplayFileExtent FinalOutput, string Error, string CloseError, string ObservationError, long BufferCapacityBytes, long ThreadAllocatedBytes,
    ReplayPacketExtractionTiming Extraction, ReplayMp4WriteTiming Writing, int GuardCalls, double GuardMilliseconds);

// Per-request packet writer selected by the candidate's default construction.
// Ordinary numeric admission still requires its exact-policy qualified catalog.
internal static class ReplayPacketMuxExport
{
    // Qualification-only serialization of ALREADY established full-file hashes.
    // Invoked while the request's immutable input handles still own the objects.
    internal static Action<ReplayExportRequest, IReadOnlyList<ReplayTransportIndex>> CompletedInputProofForChecks;
    internal static void Run(ReplayExportRequest request, IReadOnlyList<FileStream> originals, IReadOnlyList<ReplayTransportIndex> indices,
        ReplayHybridPlan plan, IReadOnlyDictionary<int, (string Path, ReplayTransportIndex Index)> boundaries,
        ReplayAacCopyPlan audio, Action check)
    {
        long started = Stopwatch.GetTimestamp(); using var parent = Process.GetCurrentProcess();
        double initialCpu = parent.TotalProcessorTime.TotalMilliseconds;
        long initialAllocation = GC.GetAllocatedBytesForCurrentThread();
        FileStream output = null; ReplayMp4PacketWriter writer = null; Exception failure = null;
        string closeError = null, observationError = null; bool closed = false, inputComplete = false;
        int sources = 0, nextAudio = 0; long bytes = 0; ReplayFileExtent final = null;
        using var samples = new ReplayPacketSampleWorkspace();
        var extraction = new ReplayPacketExtractionTiming(); var writing = new ReplayMp4WriteTiming();
        int guardCalls = 0; double guardMilliseconds = 0;Action servicePending = null;
        void BaseCheck()
        {
            long at = Stopwatch.GetTimestamp();
            try { check?.Invoke(); }
            finally { guardCalls++; guardMilliseconds += Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
        }
        void Check(){servicePending?.Invoke();BaseCheck();}
        // Freeze bounded eligibility before creating main. Unknown/custom
        // ownership and large samples keep the existing serial packet path.
        bool pipeline=request.PacketPipelineForChecks != false && request.NativeFiles?.Eligible==true
            &&(request.IndexReaders.Started || request.PreparedAcquisition.Complete)&&request.IndexReaders.Joined&&request.FallbackIndexReaders.Joined
            &&plan.Parts.Count is >0 and <=4096
            &&plan.Parts.All(p=>originals[p.Input].IsAsync&&ReplayPreparedPackets.Eligible(indices[p.Input]))
            &&boundaries.Values.All(b=>ReplayPreparedPackets.Eligible(b.Index));
        long sourceCapacityBound=checked(Math.Min(4,plan.Parts.Count)*(2*8388608L+65536+16777216+262144)+(2*8388608L+65536+262144));
        pipeline&=sourceCapacityBound<=167772160;
        request.PacketPipelineSelected=pipeline;
        if(pipeline)request.Timing.ExportConstruction+="+BoundedPacketPipeline";
        try
        {
            output = request.NativeFiles?.CreateMain()
                ?? new(request.Partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
            writer = new(output, request.Info.FramesPerSecond, plan.FirstFrame / (double)request.Info.FramesPerSecond - request.Interval.Start,
                indices[0].Sps.Span, indices[0].Pps.Span, checked((int)(plan.EndFrame - plan.FirstFrame)),
                audio?.PrimingSamples ?? 0, audio?.LogicalSamples ?? 0, audio?.Expected, Check, writing);
            void Audio(ReplayTransportAudioFrame frame, ReadOnlySpan<byte> payload)
            {
                if (audio == null) throw new IOException("Unexpected source audio in packet mux.");
                long pts = (frame.Pts + plan.ClockShift) % ReplayTransportReader.TimestampModulus;
                if (pts < 0) pts += ReplayTransportReader.TimestampModulus;
                long position = checked((long)Math.Round(pts * 48000d / 90000) - ReplayHybridPlan.OutputEpoch * 48000L / 90000);
                if (position < audio.Frames[0].StartSamples || position >= audio.LogicalSamples) return;
                if (nextAudio >= audio.Frames.Count || position != audio.Frames[nextAudio].StartSamples
                    || frame.PayloadSha256 != audio.Frames[nextAudio].PayloadSha256 || payload.Length != audio.Frames[nextAudio].Bytes)
                    throw new IOException("Packet mux changed the frozen source AAC sequence.");
                writer.Audio(payload); nextAudio++;
            }
            if(pipeline)
            {
                var jobs=plan.Parts.Select(part=>new ReplayPacketPrepareInput(originals[part.Input],indices[part.Input],part.First==0&&part.Count>0,part.Audio)).ToArray();
                foreach(var job in jobs)job.Stream.Position=0;
                var workers=Enumerable.Range(0,Math.Min(4,jobs.Length)).Select(_=>new ReplayPacketPrepareWorker()).ToArray();
                int ordinal=0;request.PacketMuxWork=new();
                try
                {
                    ReplayPacketWork.Run(jobs,workers.Length,16777216,
                        (job,context)=>workers[context.Worker].Prepare(job,context),body=>body.CapacityBytes,
                        (body,pump)=>
                        {
                            servicePending=pump;
                            try
                            {
                                var part=plan.Parts[ordinal++];Check();
                                if(!body.SourceHashComplete||body.SourceBytes!=indices[part.Input].Bytes)throw new IOException("Packet preparation omitted a complete source hash.");
                                if(part.Count>0&&part.First>0)
                                {
                                    var boundary=boundaries[part.Input];
                                    using var head=request.NativeFiles.OpenInput(boundary.Path,request.NativeFiles.Extent(boundary.Path),false);
                                    ReplayTransportPacketSamples.Read(head,boundary.Index,(picture,payload)=>writer.Video(payload,picture.Idr),null,Check,samples,extraction);
                                    sources++;bytes=checked(bytes+boundary.Index.Bytes);
                                }
                                int pictureOrdinal=0;
                                for(int n=0;n<body.Count;n++)
                                {
                                    pump();var item=body.Event(n);
                                    if(item.Picture!=null){if(pictureOrdinal++<part.Count)writer.Video(body.Payload(item),item.Picture.Idr);}
                                    else Audio(item.Audio,body.Payload(item));
                                }
                                sources++;bytes=checked(bytes+body.SourceBytes);
                            }
                            finally{servicePending=null;}
                        },BaseCheck,request.PacketMuxWork,TimeSpan.FromSeconds(30),request.PacketReaders);
                    if(!request.PacketReaders.Joined)throw new IOException("Packet producer has unjoined/incomplete source work.");
                }
                finally
                {
                    // Run never returns before joins, including guard/IO failure.
                    // No packet workspace may close while its reader is active.
                    if(!request.PacketReaders.Joined)throw new IOException("Packet source ownership has not drained.");
                    long workspace=workers.Sum(w=>w.PeakWorkspaceBytes);
                    request.PacketPipelineBufferBytes=checked(workspace+request.PacketMuxWork.PeakOwnedBytes+samples.CapacityBytes+(workers.Length+1)*262144L);
                    foreach(var worker in workers){Add(extraction,worker.Extraction);worker.Dispose();}
                    if(request.PacketPipelineBufferBytes>167772160)throw new IOException("Packet source buffers exceeded160MiB allowance.");
                }
            }
            else
            {
            foreach (var part in plan.Parts)
            {
                Check();
                if (part.Count > 0 && part.First > 0)
                {
                    var boundary = boundaries[part.Input];
                    using var head = request.NativeFiles?.OpenInput(boundary.Path, boundary.Index == null ? null : request.NativeFiles.Extent(boundary.Path), false)
                        ?? new FileStream(boundary.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                    ReplayTransportPacketSamples.Read(head, boundary.Index, (picture, payload) => writer.Video(payload, picture.Idr), null, Check, samples, extraction);
                    sources++; bytes = checked(bytes + boundary.Index.Bytes);
                }
                int pictureOrdinal = 0; var source = originals[part.Input]; source.Position = 0;
                ReplayTransportPacketSamples.Read(source, indices[part.Input], part.First == 0 && part.Count > 0
                    ? (picture, payload) => { if (pictureOrdinal++ < part.Count) writer.Video(payload, picture.Idr); } : null,
                    part.Audio ? Audio : null, Check, samples, extraction);
                sources++; bytes = checked(bytes + indices[part.Input].Bytes);
            }
            }
            if(request.PreparedInputs!=null)
            {
                // Parts may omit accepted preroll inputs. Their bytes must be
                // freshly checked even when no packet from them is emitted.
                var used=plan.Parts.Select(p=>p.Input).ToHashSet();
                for(int n=0;n<originals.Count;n++)
                    if(!used.Contains(n))
                    {
                        originals[n].Position=0;
                        ReplayTransportPacketSamples.Read(originals[n],indices[n],null,null,Check,samples,extraction);
                        sources++;bytes=checked(bytes+indices[n].Bytes);
                    }
            }
            if (nextAudio != (audio?.Frames.Count ?? 0)) throw new IOException("Packet mux omitted frozen source AAC frames.");
            inputComplete = true; writer.Complete();
        }
        catch (Exception error) { failure = error; writer?.Abort(); }
        try { if (output != null) { output.Dispose(); closed = true; } }
        catch (Exception error) { closeError = error.ToString(); failure ??= error; writer?.Abort(); }
        try
        {
            Check();
            Check(); // Check the completed observer before native finalization.
            request.Timing.FinalObservationStarted = Stopwatch.GetTimestamp();
            if (output != null)
            {
                var listed = ReplayStorage.Observe(request.Partial); using var handle = ReplayStorage.OpenPinnedInventory(request.Partial);
                final = ReplayStorage.ObserveInventory(handle, listed);
            }
            request.Timing.FinalObservationCompleted = Stopwatch.GetTimestamp();
            Check(); // Native final observation is part of the guarded handoff.
        }
        catch (Exception error) { observationError = error.ToString(); failure ??= error; writer?.Abort(); }
        request.Timing.ManagedReceiptStarted = Stopwatch.GetTimestamp(); parent.Refresh();
        request.FreezeManagedMux(new(Environment.ProcessId, parent.StartTime.ToUniversalTime(), closed, inputComplete, writer?.State.ToString(),
            sources, bytes, Stopwatch.GetElapsedTime(started).TotalMilliseconds, parent.TotalProcessorTime.TotalMilliseconds - initialCpu,
            final, failure?.ToString(), closeError, observationError, (pipeline?request.PacketPipelineBufferBytes:samples.CapacityBytes), GC.GetAllocatedBytesForCurrentThread() - initialAllocation,
            extraction, writing, guardCalls, guardMilliseconds));
        request.Timing.ManagedReceiptCompleted = Stopwatch.GetTimestamp();
        try { Check(); }
        catch (Exception error)
        {
            // The frozen receipt describes the closed producer, independently
            // of a later export-to-validation handoff failure.
            request.Timing.ManagedHandoffError = ReplayOutputDemand.Bounded(error.ToString(), 1024);
            failure ??= error;
        }
        if (failure != null) throw new IOException("Managed replay packet mux failed; per-request producer receipt retained.", failure);
        if (!closed || !inputComplete || writer?.State != ReplayMp4WriterState.Completed || final == null)
            throw new IOException("Managed replay packet mux has no complete closed producer evidence.");
        CompletedInputProofForChecks?.Invoke(request, indices);
    }
    static void Add(ReplayPacketExtractionTiming target,ReplayPacketExtractionTiming part)
    {
        target.ReadMilliseconds+=part.ReadMilliseconds;target.HashMilliseconds+=part.HashMilliseconds;
        target.PayloadLoopMilliseconds+=part.PayloadLoopMilliseconds;target.AvccMilliseconds+=part.AvccMilliseconds;
        target.VideoConsumerMilliseconds+=part.VideoConsumerMilliseconds;target.AudioConsumerMilliseconds+=part.AudioConsumerMilliseconds;
        target.GuardMilliseconds+=part.GuardMilliseconds;target.ReadBytes+=part.ReadBytes;target.DirectAvccBytes+=part.DirectAvccBytes;
        target.IntermediateVideoBufferBytes=Math.Max(target.IntermediateVideoBufferBytes,part.IntermediateVideoBufferBytes);target.Reads+=part.Reads;
    }

}
