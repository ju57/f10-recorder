# Controlled replay precision prototype: command and validation review

Checked 24 October 2026. Read-only source/primary-documentation review for the controlled prototype. No media, GPU workload, recorder, audio endpoint, desktop capture or benchmark was started by this review. The recipes below are experiment candidates, not runtime results. Parent experiment receipts determine which exact commands actually ran.

## Source baseline

`ReplaySelection.Select` deliberately chooses segments starting at/after the requested start and permits up to one GOP short ([selection, lines 36–40](../../src/ReplaySelection.cs)). Export then concatenates protected completed segments, uses `-t`, copies streams, shifts leading timestamps with `make_zero`, writes faststart MP4 and publishes after the current size check ([ReplayCache, lines 299–317](../../projects/F10Recorder.Windows/ReplayCache.cs)). Acquisition requests one-second closed GOPs, no B-frames and forced IDRs ([ReplayEngine, line 115](../../src/ReplayEngine.cs)). Retain that exact route as the negative/control baseline.

FFmpeg distinguishes input seeking that retains pre-seek content during stream copy from accurate-seek transcoding that decodes/discards it. Output seeking and concat `inpoint` do not establish arbitrary-frame independent decodability. These are documented semantics; inspect the exact packaged binary at runtime rather than inferring success from option availability. [FFmpeg CLI seeking](https://www.ffmpeg.org/ffmpeg.html), [tagged concat documentation](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/doc/demuxers.texi).

## Finite candidate order

1. **Current start-ceiling stream copy:** expect arbitrary start loss; verify it rather than accepting reported container duration.
2. **Full interval decode/re-encode:** correctness comparison with explicit frame/sample boundaries. Measure its export time, including validation; do not call a precision-only pass a joint precision/speed pass.
3. **MP4 edit-list stream copy:** potentially preserves compressed preroll while presenting only the requested interval. Test this low-cost candidate before claiming boundary re-encoding is necessary. It has compatibility and hidden-preroll caveats below.
4. **Boundary-only re-encode plus copied interior:** pursue only if preceding candidates cannot satisfy the joint contract. It is more complex than using identical encoder flags.

Do short correctness cases before committing to a twenty-export speed batch. Retain every failed candidate and exact arguments/logs. No full-file decoding or hash inventory should run concurrently with timing collection.

## Frame/sample-accurate correctness baseline

Use a normalized decoded fixture whose frame zero and audio sample zero are qualified against the generator ledger. For CFR rate `F`, choose a half-open frame interval `[S,E)` and use integer frame indices. At 60 FPS and 48 kHz, the corresponding audio interval is `[800*S,800*E)`; for arbitrary time requests, freeze an explicit rational rounding policy instead of relying on floating-point string rounding.

Conceptual command, with numeric placeholders expanded by the harness:

```text
ffmpeg -i normalized-source.mp4
  -filter_complex "[0:v]trim=start_frame=S:end_frame=E,setpts=N/(F*TB)[v];[0:a]atrim=start_sample=A:end_sample=B,asetpts=N/SR/TB[a]"
  -map "[v]" -map "[a]" -c:v h264_nvenc -preset p3 -tune ll
  -rc cbr -b:v 20M -maxrate 20M -bufsize 40M
  -g F -bf 0 -forced-idr 1 -flags +cgop
  -fps_mode passthrough -enc_time_base:v 1:F
  -c:a aac -ar 48000 -ac 2 -b:a 192k -movflags +faststart output.mp4
```

This linear baseline decodes preceding material, so its time is not an optimized seek result. An optimized version can seek to a known preceding IDR, but must update local frame/sample indices from the qualified packet/frame ledger. Never apply global frame indices to a seeked input as though its decoded frame zero were unchanged. `setpts`/`asetpts` establish output origin; they do not prove which source content survived. [Trim/atrim/setpts documentation](https://www.ffmpeg.org/ffmpeg-filters.html).

For two audio streams, trim/map each explicitly; for audio-off omit the audio branch and require no fabricated track. Preserve intended source-to-output timing rather than independently zeroing a genuinely delayed audio source and calling it synchronized.

NVENC's options and features depend on build SDK, driver and GPU. The recipe deliberately matches current capture's broad H.264 settings, but initialization and runtime output must be checked. [NVENC source/options](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/nvenc_h264.c), [FFmpeg codecs documentation](https://www.ffmpeg.org/ffmpeg-codecs.html).

## MP4 edit lists: a real candidate with distinct limits

Candidate on a normalized indexed MP4 input:

```text
ffmpeg -ss START -i normalized-source.mp4 -t DURATION
  -map 0:v:0 -map 0:a:0 -c copy -avoid_negative_ts disabled
  -use_editlist 1 -movflags +faststart edit-copy.mp4
```

The FFmpeg 9.0.2 MOV muxer explicitly calculates post-edit duration so negative starting PTS are removed by an edit list. The MOV demuxer can mark excluded index entries with `AV_PKT_FLAG_DISCARD`; these behaviors warrant testing. This is stronger than merely setting a duration label, but does not itself prove the resulting frame IDs or arbitrary endpoint precision. [Tagged muxer source, `calc_pts_duration`](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/libavformat/movenc.c), [tagged demuxer source, discard handling](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/libavformat/mov.c).

The format options document edit-list processing and timestamp shifts. Preserve necessary negative decode preroll when testing this route; copying the production `-avoid_negative_ts make_zero` would change the experiment. Validate default demux/decode and separately decode with `-ignore_editlist 1` to inventory physically retained preroll/postroll. Record packet flags, offsets, edit atoms and decoded source IDs, not only `format.duration`. [MOV/MP4 and timestamp options](https://ffmpeg.org/ffmpeg-formats.html).

Limits:

- A FFmpeg-demux presentation pass is not a player-compatibility pass. Later qualify the intended Windows players and editor/import paths; a player ignoring edits can reveal preroll.
- Retained compressed pre-start footage is still present in the file. If exact-file-content/privacy or new-footage rules require absence of those packets, a presentation-only edit is not sufficient. Do not silently resolve that product distinction from a successful duration label.
- An indexed monolithic MP4 result is not automatically evidence for direct export from reset-timestamp TS segments. Repeat the winning route on the real segmented snapshot layout, charging any full remux/index creation to export time.
- AAC packets and priming differ from video frame boundaries. Verify decoded sample alignment/markers and skip-sample/discard-padding signaling, including non-AAC-aligned starts/ends. An audio track's nominal duration is insufficient.

## Hybrid boundary encoding

For a frame interval `[S,E)`, let `B` be the first qualified IDR boundary after `S` and `C` the last such boundary before `E`. Decode/re-encode `[S,B)`, stream-copy complete interior GOPs `[B,C)`, and re-encode `[C,E)` only if necessary; handle empty interior/overlapping head-tail ranges once. Each copied interval must begin with a proven independently decodable IDR and must not depend on excluded pictures.

For video-only assembly, normalize each component's timestamps and container/time base, then concatenate with explicit durations. Decode the entire resulting file under strict error handling and inspect each join. Encode the audio interval once continuously rather than concatenating separately primed AAC boundary pieces unless the latter is explicitly validated. This is a proposed experimental simplification, not a shipping architecture.

Identical codec name, dimensions and command flags do not prove compatible SPS/PPS. Inventory profile/level, chroma/bit depth, time base, VUI/color metadata, extradata and in-band parameter sets at every join; verify IDR state. MP4 `avcC`/sample-entry handling may not describe changing parameter sets safely for every player. FFmpeg's concat requirement is compatible stream layouts/time bases; its H.264 Annex-B conversion is not a promise that arbitrary independently encoded fragments join correctly. [Concat demuxer](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/doc/demuxers.texi), [H.264/ADTS conversion filters](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/doc/bitstream_filters.texi).

## Independent correctness oracle and admission

- Generate every frame with a robust pixel-coded source frame ID, complementary/checksum cells and separately logged rational PTS. Decode and read those pixels; never infer frame identity from output PTS or frame count alone. Use sufficiently large cells to survive lossy encoding, and reject ambiguous/checksum-invalid IDs rather than guessing.
- Require the exact ordered desired IDs: first, last, count and every interior ID, with no duplicate/skipped/out-of-range frame. Packet DTS/PTS ordering and full decode errors are separate checks.
- Generate timestamped audio markers from a known PCM sequence; decode to PCM and correlate against the source/reference at boundary/middle/end markers. AAC is lossy, so PCM hashes are not an AAC content oracle. Measure encoder delay/priming instead of assuming it is zero.
- Before the speed batch, include exact-IDR, one-frame-after-IDR, half-GOP, one-frame-before-IDR, non-frame-aligned request, same-GOP short interval and arbitrary five-minute boundaries. Exercise audio off/one/two tracks on short fixtures. Start the performance reference at the accepted 1080p60/H.264/20 Mbps/one-system-track configuration; additional modes do not inherit its result.
- Separate generation, qualification decode and timed export. Freeze source/output hashes, exact binary identities, trial order, quantile calculation, storage reserves, concurrency condition and validation cost before collection. Keep command failures/timeout receipts and slow exports.
- A first five-minute batch can answer this controlled prototype's precision/export question. It does not establish live request-to-file behavior, encoder competition with real game capture, pipeline backpressure, NVENC quality, acquisition timestamps or broad codec/fullscreen acceptance.

All official upstream links were checked on the date above. Tagged 9.0.2 source semantics do not prove the locally authenticated binary contains or behaves exactly like that source; the parent must retain exact executable hashes and command/output evidence.

## Follow-up: direct TS preroll without a preliminary MP4 index

The parent reported short-case results in which ordinary concat-TS input seeking dropped to the next GOP while a preliminary indexed MP4 permitted exact displayed IDs. Those are parent observations, not experiments run by this reviewer.

The next bounded variant should start its snapshot at the **preceding proven IDR**, avoid both input/output `-ss`, and retain all its nonnegative packets through stream-copy admission. Apply negative preroll shifting only in the output `setts` BSF:

```text
ffmpeg -f concat -safe 1 -i preceding-idr-snapshot.txt
  -t (DELTA+DURATION) -map 0:v:0 -map 0:a:0 -c copy
  -bsf:v "setts=pts=PTS-DELTA/TB:dts=DTS-DELTA/TB"
  -bsf:a "setts=pts=PTS-DELTA/TB:dts=DTS-DELTA/TB"
  -avoid_negative_ts disabled -use_editlist 1 -movflags +faststart output.mp4
```

Expand the placeholders numerically and qualify the concat timeline origin first. `DELTA` is the desired start minus this snapshot's preceding-IDR source origin, not the absolute original-file start. A nonzero initial A/V offset must be preserved with one common shift, not independently reset away. `-t` is evaluated against pre-BSF stream-copy time, so its bound includes preroll. Validate the actual endpoint, AAC skip/padding, edit duration and every decoded ID; do not assume the recipe supplies them.

Why this is a distinct candidate: FFmpeg 9.0.2 calls `of_streamcopy` before output bitstream filtering. The former can reject packets based on key/start rules; a later `setts` filter cannot recover an already rejected packet. Leaving the initial IDR nonnegative until that gate, then shifting timestamps negative, avoids that specific ordering problem. The `copyinkf` option bypasses only the initial nonkey rule, not absent references; `copypriorss=1` affects the prior-start rule, not packets a seek/demux already omitted. Output-side `-ss` has a separate DTS-before-start gate. [Tagged stream-copy/filter ordering and admission](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/fftools/ffmpeg_mux.c), [option declarations](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/fftools/ffmpeg_opt.c).

If this works, it still retains compressed preroll and depends on edit-aware presentation. It is not equivalent to a file containing only requested encoded pictures, and must retain the compatibility/hidden-content caveats above.

## Independent code-only methodology audit

Reviewed `tests/precision-export-prototype.py` while the parent collected reference timing; this review ran no media probe, hash, decoder or benchmark. The pass predicate checks decoded pixel IDs/complement rows, contiguous source-ID order, first/end/duration rounding within one frame, explicit exact-ceil status, strict packet timestamp ordering/cadence, an audio-duration bound and periodic pulse phase. No critical false-video-pass condition was found within those declared tolerances. The complementary row is an error check, not an independent cryptographic source identity.

The final result must retain these limits:

- The generated one-second audio pulses establish alignment only modulo their repetition period. They do not uniquely identify absolute source audio or exclude an integer-second content shift. Unique coded audio is needed for the full source-content/A/V gate.
- First-pass/repeat-pass labels do not establish an uncached OS/disk state. Fixed method order is not counterbalanced and cannot by itself support causal comparisons between methods.
- Producer initialization waits for frames, but each reference trial only checks process liveness afterward. Unless retained progress evidence brackets the trial, the measured condition is an alive generated NVENC producer, not proof of uninterrupted 60-FPS acquisition or ordinary live recorder behavior.
- Pre-commit validation is a structural probe. Full pixel/audio/packet checks occur offline and their cost is not inside export timing. Report that timing boundary precisely; it does not settle the future product's complete pre-publication validation cost.
- The summary mechanically calculates median/p95 for every method, including single-observation controls. Those n=1 values must be reported as individual observations, never as qualified distribution statistics. The two twenty-observation candidate groups remain distinct from the controls.
- Generated H.264/60-FPS closed files, hard-link snapshots and a competing generated encoder do not qualify other codecs/FPS, capture-tail wait, device clocks, games/fullscreen, disk-cold behavior, media corruption recovery or production request/UI latency.

The parent already identified the periodic-audio and cache-label limitations before this audit. There is no request here to modify the harness or acceptance thresholds during the frozen timing collection.

## Follow-up: per-segment probing cost hypothesis

After the original frozen batch, the parent requested a source-only investigation of concat overhead. FFmpeg 9.0.2's `open_file` calls both `avformat_open_input` and `avformat_find_stream_info` for every child file. User-supplied duration and declared virtual streams do not bypass these calls. Child-specific option dictionaries are forwarded to the nested open/probe; top-level concat options are not equivalent to those dictionaries. [Tagged concat source, lines 313–367](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/libavformat/concatdec.c).

First bounded amendment: add this directive after each trusted file line while retaining the independently established duration:

```text
option skip_estimate_duration_from_pts 1
```

FFmpeg documents that option as skipping additional tail-PTS duration probing for MPEG-TS/PS. If insufficient, separately test child `probesize 262144` and `analyzeduration 100000` (100 ms; zero selects default behavior). These are specialized known-fixture input-admission candidates, not permission to weaken final media validation. [Format options](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/doc/formats.texi), [child option syntax](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/doc/demuxers.texi).

Keep source-ID/full-decode, timestamp cadence and audio/schema predicates unchanged. Reduced probing can leave parameters/streams unidentified; reject rather than reinterpret those outputs as valid. Do not substitute packet-dropping/buffering flags merely to improve timing. These source facts support a cost hypothesis, not a measured attribution; the parent's next separately admitted controls must establish any improvement.

The tagged stream-info implementation expands zero analysis duration to defaults (five seconds generally and seven seconds for MPEG-TS stream analysis) and can initialize a single-thread H.264 decoder to extract parameters. Repeated child-file analysis can therefore have CPU cost as well as I/O cost; the source alone does not quantify either. [Tagged `avformat_find_stream_info`, lines 2445–2452 and 2488–2504](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/libavformat/demux.c).

Safety boundary: the tagged concat parser marks `option` as `NEEDS_FILE | NEEDS_UNSAFE` and rejects it with safe mode enabled. The controlled prototype's `-safe 0` on its own generated absolute-path manifest is therefore not directly transferable to production's `-safe 1` path. Any implementation must separately enforce generated-manifest ownership, canonical contained filenames, immutable pinned inputs, an exact option/value allowlist and local-file-only protocol access. Do not accept arbitrary manifests/options or globally remove production checks to copy the timing configuration. [Concat syntax/parser, lines 428 and 468–470](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/libavformat/concatdec.c).
