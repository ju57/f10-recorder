# Replay output demand and finalization

Research baseline: `ecdedc69f390cc5f63cfcbeb8a96974232c58a1a` (merged PR70), 26 October 2026. Issue #13 remains partial; its four criteria and the twelve release gates remain open. This report changes no production behavior.

## Recommendation

Develop a versioned, profile-scoped **output-demand estimate**, backed by prospective calibration, with closed-selection identity and an explicit unfinished-tail extension policy. Keep a source-derived finite bound, a calibrated estimate and an unmeasured result as different outcomes. The accepted requirement permits calibrated conservative reference-profile margins; it does not require a theorem for arbitrary third-party media. These four tiny software fixtures provide mechanism evidence, not those margins.

The existing known-input-copy/concat check is useful but insufficient for total save capacity. Input container bytes, coded packet bytes, final MP4 bytes, peak coexisting allocation and finalization RAM describe different quantities. Neither an arbitrary multiplier, a second whole MP4 reservation, `-fs`, nor an implicit zero for unknown output supplies the missing contract. Preserve ordinary saves while developing and qualifying a usable reference estimate; do not ship a universal rejection policy as a substitute for that work.

| Evidence category | Current result |
| --- | --- |
| Primary source | Applicable codec conversions, sample/table/configuration costs, same-file faststart, soft file-size checking and the open-tail boundary are identified below. |
| Approved-binary observation | 24 frozen commands exited normally with code 0. A 4,096-byte size setting produced a 41,022-byte MP4; adding metadata changed output size without changing its coded-data region. |
| Finite mathematical components | Some MP4 table sizes can be computed from validated counts. They are not the entire mux output or its peak filesystem demand. |
| Calibrated reference estimator | **Not established.** No coefficient, guard band or profile-wide capacity claim is derived from this batch. |
| Unmeasured premises | NVENC/reference profiles, long/high-bitrate selections, tail envelope, metadata/configuration changes, approved-binary peak allocation, storage faults and full path/volume behavior remain pending. |

## Current production path and provenance

At this baseline, H.264/HEVC capture writes one-second MPEG-TS segments; AV1 writes Matroska. Audio is optional AAC stereo, 48 kHz/192 kbps. The requested CBR/maxrate/VBV and closed-GOP settings are configuration inputs, not a serialized-file ceiling established by this investigation. [Capture arguments](https://github.com/ju57/f10-recorder/blob/ecdedc69f390cc5f63cfcbeb8a96974232c58a1a/src/ReplayEngine.cs#L113-L120).

An accepted replay retains its original request cutoff, source/session and destination. Selection may expand after the final segment closes. Snapshot inputs are linked/copied and an exact-duration concat file is written. Export uses stream copy, negative-timestamp normalization and faststart; HEVC adds `hvc1`, AV1 adds `setts` and a 48,000 video timescale. The opaque source adapter supplies completion/failure, without a precomputed output demand or synchronous allocation-admission callback. [Selection and late planning](https://github.com/ju57/f10-recorder/blob/ecdedc69f390cc5f63cfcbeb8a96974232c58a1a/projects/F10Recorder.Windows/ReplayCache.cs#L248-L297), [export/publication](https://github.com/ju57/f10-recorder/blob/ecdedc69f390cc5f63cfcbeb8a96974232c58a1a/projects/F10Recorder.Windows/ReplayCache.cs#L358-L467), [process adapter](https://github.com/ju57/f10-recorder/blob/ecdedc69f390cc5f63cfcbeb8a96974232c58a1a/src/ReplayEngine.cs#L236-L249).

The approved executable is FFmpeg 9.0.2 essentials from Gyan, SHA-256 `3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec`; the package and probe have separate source pins. The retained private test directory contained only that executable, so no ffprobe was discovered, copied or run. Version/build text reported the Gyan build and libavformat 63.1.102; its stdout receipt has SHA-256 `711056f2bc922237113d5defe7da4dbd97beaab41098e5f8d425753dbfea99ff`. These observations do not prove vendor-source equivalence. [F10 trust constants](https://github.com/ju57/f10-recorder/blob/ecdedc69f390cc5f63cfcbeb8a96974232c58a1a/src/MediaDependency.cs#L23-L32).

The independent source review resolved upstream tag `n9.0.2` to commit `946fcce07b6dcd0331c8cc609192aeff5e1924f8` and compared raw tag/commit/blob text. The writer subsequently reread the immutable relevant sources through the GitHub connector. That source identity is separate from the executable hash, enabled libraries and observed behavior. [Tag object](https://api.github.com/repos/FFmpeg/FFmpeg/git/tags/ce8f11b9fac4cd7ccfb41ca7c01bc17f5cda8cc3).

## Mechanisms the demand model must represent

| Component | Source-backed mechanism | Consequence |
| --- | --- | --- |
| H.264 payload | Annex-B NAL boundaries are rewritten using four-byte lengths; start-code removal and prefix insertion can change packet size. [NAL writer](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/libavformat/nal.c#L74-L110) | Stream copy does not mean identical serialized payload. Include framing/configuration applicability. |
| HEVC payload/configuration | The converter can filter VPS/SPS/PPS when `hvc1` is selected. [HEVC converter](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/libavformat/hevc.c#L1204-L1249) | A generic H.264 constant is insufficient; packet and configuration terms differ. |
| AV1 payload | The mux helper filters selected OBU classes and sums the retained data. [AV1 helper](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/libavformat/av1.c#L33-L85) | Timestamp filtering is not an output-size guarantee; qualify AV1/Matroska separately. |
| AAC payload/configuration | ADTS framing can be removed and decoder configuration emitted as side data. [AAC filter](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/libavcodec/bsf/aac_adtstoasc.c#L50-L118) | Track presence, packet headers and configuration contribute separately. |
| MP4 index/configuration | Sample/chunk/timing tables and sample descriptions depend on actual structure, including changed extradata. [Tables](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/libavformat/movenc.c#L211-L345), [configuration changes](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/libavformat/movenc.c#L7046-L7076) | Duration/bitrate alone do not specify the complete header. |
| File-size switch | The CLI checks current size before another packet; trailer writing occurs separately. Its documentation allows exceeding the requested size. [Packet check](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/fftools/ffmpeg_mux.c#L204-L242), [trailer](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/fftools/ffmpeg_mux.c#L752-L779), [option contract](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/doc/ffmpeg.texi#L844-L847) | `-fs` is neither a strict byte reservation nor proof of preserving the requested footage. |

For ordinary faststart, the source computes the movie header, adjusts offsets and shifts data within the output. The shift helper reopens the same URL for reading and allocates two shift-sized memory buffers. That supports **same-file rewrite plus header growth**, not an automatic second complete disk MP4. It does not establish the approved binary's peak physical allocation, transient files or every filesystem's behavior. Keep RAM, I/O traffic, EOF and coexisting file extents separate. [Header/offset computation](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/libavformat/movenc.c#L8907-L8990), [same-output shift](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/libavformat/mux_utils.c#L71-L124).

Some finite table components are useful once their counts and applicability are validated:

| Table | Serialized bytes under the cited ordinary writer |
| --- | --- |
| Variable-size `stsz` | `20 + 4 × samples` |
| `co64` | `16 + 8 × chunks` |
| `stsc` | `16 + 12 × chunk runs` |
| `stts` / `ctts` | `16 + 8 × timing runs`, each when emitted |
| `stss` | `16 + 4 × sync entries`, when emitted |
| `sdtp` | `12 + entries`, when emitted |

These are components, not a complete `moov` formula: codec/sample descriptions, metadata, nesting, optional tracks/side data and applicable box versions remain. Offset widening near the 32-bit boundary must be included; this batch did not exercise it. [Timing tables](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/libavformat/movenc.c#L3200-L3286).

## Frozen experiment and approved-binary observations

The writer froze its plan and 24 argument vectors before execution; the independent 22-question matrix was also frozen before results. Root source pointers arrived before the writer plan froze and were credited as received context. Independent findings files were read only after the execution gate. This is not a claim that the writer independently invented every source premise or adversarial case.

Plan SHA-256: `d2ab8b4a1e222c3c0d8e25c9d23916860969d8702fa879b08f094e1932d1da3d`; argument packet: `a6158ec32825025764a3da56ad89d97c2f386635f64ebb197ac5c19edb043816`. The batch used the existing production executable verifier, holding root/ancestor/file read-only leases for each whole child lifetime. Exact commands and input/output hashes are retained in the ignored local research packet; no executable/runtime copy or production edit was made.

The four families used 64×64 software encoders at 30 FPS: H.264 and HEVC video selections of two seconds, an H.264/AAC three-second generation with its two later closed segments selected, and a one-frame AV1/MKV selection. Video rate 128 kbps is an intentionally tiny surrogate, not a supported F10/NVENC reference profile. Generated sine audio is not a device/clock test. All four remuxes and their decodes succeeded; no requested codec was unavailable.

| Family | Selected container bytes | Source copy-view packets | Output indexed sample bytes | Final MP4 bytes | `moov` bytes | Observed final allocation |
| --- | ---: | --- | --- | ---: | ---: | ---: |
| H.264/TS, video | 55,836 | 60 video; 39,928 coded bytes | 60 video; 39,929 bytes | 41,022 | 1,045 | 45,056 |
| H.264/TS + AAC | 102,836 | 60 video/94 audio; 37,281/47,103 bytes | 60 video/94 audio; 37,281/46,445 bytes | 86,849 | 3,075 | 90,112 |
| HEVC/TS, video | 56,400 | 60 video; 40,569 bytes | 60 video; 40,407 bytes | 41,567 | 1,116 | 45,056 |
| AV1/MKV, video | 2,037 | 1 video; 962 bytes | 1 video; 962 bytes | 1,780 | 770 | 4,096 |

These are selected source-demux and output-index views, not normalized packet identity comparisons. They illustrate why framing/configuration terms matter; they do not identify every cause of a byte difference. All baseline MP4s were smaller than their container inputs, so this writer batch **did not falsify** an input-container upper-bound hypothesis. It also does not prove one. The output allocation observations use one local 4,096-byte allocation unit and qualify no other volume/quota behavior.

The three controls supply narrower evidence:

- **Metadata:** adding a 4,096-byte title changed H.264 output from 41,022 to 45,142 bytes, while sample counts, the 39,929-byte `mdat` payload and its SHA-256 stayed identical. The added title atom occupied 4,120 bytes. A payload-only formula omitting permitted metadata is therefore incomplete. This was an explicit nondefault metadata probe, not a claim that current capture emits arbitrary titles.
- **Layout:** AV1 faststart and no-faststart outputs were both 1,780 bytes, with the same 962-byte `mdat` hash and one indexed sample. Faststart placed `moov` before `mdat`; the control placed it afterward. This does not prove equal intermediate allocation or a speed benefit.
- **Soft limit:** `-fs 4096` still produced a decodable 41,022-byte H.264 MP4 with 60 indexed samples and the same `mdat` hash as its baseline. This is a concrete failure of a strict 4,096-byte interpretation. The observation does not isolate the relative contributions of buffering, packet checks and trailer writing.

The 100 ms size/directory/memory observer was deliberately coarse. These short jobs could finish between samples; sampled working sets were visibly incomplete as peak evidence. No sampled maximum is reported as an allocation/RAM bound, proof of no transient scratch, or a speed result. Decoding these controlled files establishes neither source-picture lineage nor perceptual A/V alignment, NVENC, fullscreen, gaming or long-run acceptance.

### Packet-view and analysis limits

The planned `-c copy -f framehash` outputs report six base fields plus version-2 optional side-data suffixes. Headers report codec extradata separately. The suffix supplies ordered size/hash pairs without side-data type identifiers; equal base hashes alone are insufficient for full packet/configuration equivalence. Without stream copy, this muxer's default raw-video/PCM behavior would describe another representation. [Payload/header/suffix writers](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/libavformat/hashenc.c#L235-L317), [defaults](https://github.com/FFmpeg/FFmpeg/blob/946fcce07b6dcd0331c8cc609192aeff5e1924f8/libavformat/hashenc.c#L329-L341).

All raw headers/rows were retained. Printed video extradata sizes were 35/36/84/16 bytes for the four families. The TS views printed 60/154/60 one-byte side-data entries, respectively; AV1 printed none in this version-2 view. No semantic type was inferred. The audio configuration was not declared empty merely because an audio extradata header was not printed.

The original runner exited with a postprocessing error after all 24 tool processes had exited normally: its parser incorrectly required exactly six framehash fields. A separate read-only analyzer accepted and retained the suffixes; no media process was repeated. An intermediate PowerShell aggregation serialized null sums for ordered dictionaries; its individual entries remained intact, and the final reproducible script explicitly validated/summed them. Original failures, sources, binaries and receipts remain retained. Experiment runner builds had zero warnings/errors; the separate analysis wrapper had one unused-field warning and zero errors. The bounded MP4 inspector is not a complete codec/container validator.

## Proposed production-consumable demand contract

The following is a research recommendation, not an implemented interface or qualified numerical policy.

1. **Bind the evidence.** Describe a closed immutable selection by file identity/length or digest, selected boundaries/cutoff, codecs/tracks/timebases, packet/sample counts, framing/configuration changes, allowed metadata envelope, exporter executable hash and argument-contract version. Bind it to the accepted request's original actual destination volume. Reject stale evidence rather than silently reusing it after a source/configuration/exporter change.
2. **Name the evidence kind.** Return a finite source-backed bound under validated premises, a calibrated conservative estimate with its reference-envelope/calibration revision, or an unmeasured result with missing premises. Unmeasured is not numeric zero or a hidden infinity. Do not activate an output guard that permanently rejects every ordinary save: first qualify at least one useful supported reference envelope and agree the treatment of unmeasured cells.
3. **Separate terms and domains.** Model converted coded payload, applicable container/index/configuration/metadata, finalization peak growth and any independently evidenced coexisting scratch. Distinguish final EOF from peak logical output and rounded physical allocation. The same partial's phases are not independent full-size copies; final rename does not require another payload. Account only remaining distinct growth against fresh caller-free capacity; already allocated bytes already reduce free space.
4. **Make the tail policy explicit.** The initial request can lack its last closed packet set. A measured, applicable conservative tail envelope can be a separately qualified estimate; otherwise the later closure must trigger an atomic demand extension before corresponding growth, with truthful accepted-save failure if it cannot fit. That later failure is not pre-acceptance sufficiency. Preserve the original trigger/cutoff and protected inputs throughout.
5. **Connect to the real exporter.** The current `IReplaySessionSource`/replay ownership seam is the narrow place to provide selection demand evidence and export stage/progress/failure observations. A polling callback or `-fs` flag alone cannot enforce arbitrary future writes or a drive floor. Define the trust and failure limits of any callback before integrating it. Keep cache-budget accounting, output-volume commitments, capture low-space stop and durable explicit Resume as coordinated but separate requirements.
6. **Preserve completion semantics.** Hold claims while accepted work can still grow. A failed cleanup leaves materialized extents charged through observation. Publication remains success; cleanup/logging cannot reverse it. No automatic quality/duration reduction, protected-media deletion, cross-volume subsidy or reservation migration is part of this recommendation.

For an already closed selection, a useful accounting shape is:

`required future allocation on V = sum of remaining distinct input/output/finalization/scratch commitments on V`

Each commitment needs a declared bound/estimate and materialization observation on the same actual volume. Rounding a logical prediction does not itself qualify filesystem overhead or a peak. An incomplete term must remain identified, rather than disappearing into an unexplained coefficient.

## Bounded next action and remaining coverage

Agree a calibration plan for one real recorder-produced reference cell before implementing numerical output admission: actual source dimensions/FPS/codec/bitrate, optional AAC, encoder/exporter identity, supported duration/tail behavior, metadata/configuration constraints and destination filesystem. Freeze sample selection, training/held-out split, required conservative coverage, guard-band method, failure behavior and invalidation criteria **before** collecting results. Exercise packet/count/configuration boundaries and positive/sensitivity controls; keep unmeasured cells explicit. Do not choose a percentage from these four clips or tune against the held-out failures.

A later admitted closed-segment packet/configuration index may reduce rescanning, but its cost and correctness need measurement. A dry null mux is not an exact MP4 size planner; a second full mux pass, custom counting I/O, fragmented output or `moov_size` are separate design options requiring their own source, compatibility and performance validation. None is adopted here. The response/save-speed contracts remain unchanged; tiny CPU jobs qualify neither feedback latency nor full five-minute export timing.

The independent matrix's packet-density, many-segment, malformed-input, overflow, metadata-applicability, stale-receipt and long/reference-profile questions are not all answered by four writer families. External exhaustion, actual peak allocation, filesystem/quota/volume/reparse behavior, 5 GB warning / before-2 GB acquisition stop, durable Resume, queue/manual commitments and final Storage UI remain mandatory. Future live/NVENC calibration requires a separate effects/budget/source plan and authorization; no such action occurred here.

## Evidence and citation corrections

The ignored local packet `issue13-output-demand/writer` retains the frozen plan/commands, pre-engine source/build identity, per-command argv/PID/exit/observer receipts, input/output hashes, raw logs, original failed analysis and successful read-only derivations. Final derivation `batch-01/analysis-final.json` has SHA-256 `872b2c21c2436e6237fb59df5f6fa31ba0615fc31582b3659b4563cb2628c07d`. Post-engine executable/ACL/cleanup receipt has SHA-256 `16a50e51bc1d88b4f27374502fdf9589403278f2a4f98f506b9356c2f550ddd0`. No task-owned engine process remained at controller handoff. Independent experiments and review are separately attributed; they are not included in the 24 writer processes.

Older local packets cited `movenc.c:8369/8457` and `mux_utils.c:67–116` for faststart without establishing their exact source text. Those positions are not valid references for the pinned file used here. The verified relevant ranges are `movenc.c:8978–8990/9079–9086` and `mux_utils.c:71–124`; original packets were preserved, not rewritten. Immutable blobs include movenc `6f93eb3e36fb47bd2b8d0f6f7acccbf82fcf505e`, mux_utils `86d333dbe7f5374302415998f46d1bd0cfe32526`, and hashenc `b108eca41b7a23b4b04353bf099e61a9a863ff89`. Source-only findings and the later framehash clarification remain separate frozen artifacts.

No production code, user settings/media, startup/vendor state, GPU/device capture, recorder UI/worker, engine installation or release action changed. This research neither completes issue #13 nor waives any release gate.
