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
