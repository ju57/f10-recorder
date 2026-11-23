# Completed replay manifest publication

The segmenter sends one completed CSV record to stdout using
`-segment_list pipe:1 -segment_list_type csv -segment_list_size 0`. Progress uses
that same pipe. The worker dispatches segment records separately from progress
and maintains a bounded, atomically published `segments.csv`: replay seconds plus
twenty entries, with a1MiB publication cap. The pipe does not retain an unbounded
file journal. Captured media stays in the existing independently decodable TS/
Matroska files; acquisition clocks, keyframe selection and snapshot ownership
are unchanged. `-segment_wrap` is never used.

The worker owns publication and reading under one manifest lock. FFmpeg therefore
does not replace a CSV name which the application may have open. This addresses
the native Windows MoveFileEx conflict reproduced even with delete-sharing reads.
Final stdout metadata must drain within two seconds after confirmed process exit;
missing completion, malformed/nonprogressing entries and publication I/O failures
fail capture health and preserve the last complete list. Shutdown errors do not
skip job/process cleanup. A stream failure does not become healthy after late EOF.

Run the nonmedia preview with an exact helper and fresh evidence root:

```powershell
./tests/replay-manifest.ps1 -AssemblyPath <helper.dll> -DataDirectory <fresh-root>
```

Add `-Run -ExpectedAssemblySha256 <sha256>` for the native/file controls. Their
effects are listed in [test effects](test-effects.md). CI runs the same route.
The separately approved software control uses the actual CaptureSession but
substitutes generated inputs and a CPU encoder; its preview is
`tests/replay-manifest-software.ps1`. Neither controlled evidence nor a short
software test qualifies desktop/system-audio/NVENC calibration.

Changing the capture arguments or Windows assembly invalidates previous model
qualifications. Never rewrite a historical receipt to the new exporter identity.
The production catalog loader must continue to return `Unmeasured` until a fresh
qualified receipt matches the corrected consumer, host, scope and destination.

## Launch effects

These route-specific declarations are indexed by [test-effects.md](test-effects.md).
Read the selected route's complete effects and bounds before execution.

### tests/replay-manifest.ps1 -AssemblyPath

`tests/replay-manifest.ps1 -AssemblyPath <reviewed-helper> -DataDirectory <fresh-root>`
previews the completed-segment publication checks. Add `-Run` and the exact
`-ExpectedAssemblySha256` to execute tiny CSV fixtures, native replacement
conflict sensitivity, concurrent managed publication/read/storage observation,
malformed/failed publication, bounded shutdown drain and a30-minute metadata
window. Under2MiB remains; cumulative ordinary writes are about128MB. It starts
no engine, worker, child, GPU, capture/audio, window/shortcut or settings/startup
action. This is not proof of the corrected real-engine stdout or live media path.

### tests/replay-manifest-software.ps1

`tests/replay-manifest-software.ps1` previews a hash-bound two-child software
verification plan; `-Run` is separate execution. It uses the actual CaptureSession
with a verification-only argument probe: generated640x360/30fps video and a
generated440Hz tone, CPU MPEG2/AAC, one natural EOF and one graceful stdin-q stop.
It plays no audio, acquires no desktop/device input, opens no window, uses no GPU,
changes no profile/startup/vendor preference and activates no model. Limits are
two media children,60seconds/32MiB under a65second outer controller, with no retry.
It checks real stdout progress/CSV dispatch, concurrent readers/inventory, final
segment metadata, normal producer absence and absence of rename diagnostics.
It must receive its own resource authorization when earlier child ceilings have
already been consumed; it does not replay or relabel an earlier failed batch.
