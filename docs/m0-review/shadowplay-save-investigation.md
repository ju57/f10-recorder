# ShadowPlay replay buffer and save process: focused findings

24 October 2026. **The current ShadowPlay build confirms a useful architectural direction for F10: retain encoded-sample information and feed selected samples directly to a muxer. Prototype that reader while retaining F10's immutable chunks and pins.** This investigation does not establish an export-speed pass, frame-exact ShadowPlay cuts, or a reason to replace F10's storage with an overwritten circular file.

The next engineering step is a controlled indexed-reader prototype. Fullscreen and coexistence remain deferred. E1 and M0 remain open.

## Scope, identity and method

Read-only native analysis of the installed NVIDIA app **11.0.9.565**, using REA 6.4.0, Ghidra 12.1.4 and the configured full JDK 21.0.12.1. The matching retained capcore snapshot was reused; new save-path queries extended its coverage. The current nvmf binary was opened separately and its routines were located from current diagnostic references. Old 11.0.9.251 addresses were leads only, never assumed to apply to the current target.

| Current installed file | SHA-256 |
| --- | --- |
| `NVIDIA app/ShadowPlay/capcore64.dll` | `bc987986717c1ada9b7a53bfbca0bac6e8e2cd72401a56689169f0082068ee83` |
| `NVIDIA app/ShadowPlay/nvmf64.dll` | `eb5a24d7472dd26e2315ead6bf18b1df97605e0894c35ee712b6f9b3b3e30161` |

The focused selection contains **31 distinct function dossiers: 23 capcore and eight nvmf**. Their selected high-p-code records report no truncation. This does not eliminate decompiler type errors, unresolved indirect calls, function-boundary assumptions, alias/ownership uncertainty or incomplete whole-program coverage. Exported bundles contain 54 capcore records and 20 nvmf records, including imported capcore evidence and navigation/search records; those counts are not all newly analyzed functions. A zero unknown-registry count is bookkeeping, not proof of complete recovery.

Two independent reviews compared current F10 source and challenged the native ownership/timing interpretation. Raw bundles, exact evidence IDs, function extracts, hashes and reviewer notes remain under ignored [local evidence](../../artifacts/shadowplay-save-focused-20261024/). They are not shipping code or dependencies. No NVIDIA source/pseudocode is added to production.

No application build, target execution, process attachment, desktop/system-audio capture, live performance trace, NVIDIA control command, settings change or fullscreen/coexistence trial was performed. Production F10 source and installed applications were unchanged. Each native analysis session was exported and closed.

## What the current code establishes

### 1. Encoded samples have an index before saving

Replay startup creates a video buffer and one or two audio buffers. The selected video-buffer creation reaches a constructor that allocates **56-byte sample records** and file backing. Append records include byte offset, stored length/payload size, a monotonically assigned sequence, flags, a keyframe marker, adjusted time and original time. Names/precise source types are reconstructed; the field writes and uses are observed.

Append wraps storage offsets and record indices, evicting overlapping older records. A separate memory-copy branch also exists. The traced startup constructor selects file storage; this does not prove every possible session uses it. Video backing capacity in this path uses bitrate/8 × duration × 1.2, while sample capacity is derived separately from duration and frame rate. These implementation constants are not recommended F10 limits.

**Apply to F10:** keep packet positions, timing, keyframe/dependency information and validated stream descriptions when a chunk closes. A duration-only index is insufficient. Retain source identity and completion state with the index.

### 2. The save worker consumes samples, not independent media files

The inspected read routine uses a sample record to seek and read the encoded bytes from the existing backing handle, or copy from memory. It copies timing, flags, keyframe and sequence into a sample object. The commit worker selects video/audio samples using their timestamps and starts the muxer on a video keyframe. The examined path does not open and analyze a separate media container for every sample.

At the lower writer boundary, `CMediaStream::WriteSampleToMux` supplies encoded buffers and converts 100 ns time units to microseconds. Its directly traced wrapper calls `MediaMuxer` sample writing. The lower adapter has a signal/wait handoff, and the writer's selected chunk routine chooses the lowest queued track timestamp. Queue capacity, stall duration and live throughput were not measured. The capcore wrapper uses indirect native media interfaces; this is selected-path evidence, not a fully resolved cross-DLL whole-program graph.

**Apply to F10:** a persistent demux/index service or small packet-reader/mux helper is a justified experiment. It must actually supply verified packet data and stream parameters to the writer. Adding a sidecar while keeping the same stock concat reader will not remove that reader's analysis work.

### 3. Keyframe selection is not our exact-cut contract

Current clip selection finds a timestamp position, scans backward for a video keyframe, and scans forward if necessary. The save loop waits for a video keyframe before starting normal mux output. This establishes decodability-oriented selection; it does not demonstrate physical removal of all pictures before an arbitrary requested start or the accepted one-frame start/end/duration tolerance.

Historical runtime evidence produced a valid **300.222938-second** NVIDIA replay. It corroborates a working reference recording, not F10's stricter precision requirement. Its **32.018 ms** MCP response was command acknowledgement, not request-to-finished-file latency. Existing reference media may have been retired; this investigation read retained reports and did not perform a fresh decode.

**Apply to F10:** retain separate precision handling. The earlier hybrid boundary-encode/copy candidate remains useful for testing physical cuts; edit-list copying needs an explicit preroll/player-compatibility contract. Do not move the requested boundary to gain speed.

### 4. Object lifetime and selected-byte preservation are different

Commit increments the buffer manager's reference count. Release destroys its buffers only when that count reaches zero. Read and append share the per-buffer critical section, and read holds it through validity checking and copying the selected sample's bytes.

The studied validity check verifies current head/tail membership. It does not compare an expected generation for a frozen save request. Append can evict records under byte overlap or metadata wrap. Video/audio push paths compare sequence progression, but the mismatch branch is a diagnostic after mux handoff, not a mismatch-specific rejection or source lease.

These findings establish manager lifetime and per-sample atomic copying. They **do not establish that the complete selected range remains immutable throughout a slow save**. Other coordination may exist outside the studied scope; this is not a demonstrated NVIDIA corruption defect.

**Apply to F10:** preserve immutable closed chunks, admission pins and owned snapshots. Hard links protect file lifetime, not bytes modified in place. A mutable circular-file design would need separately specified range leases or copies, generation checks, retention pressure, concurrency and crash recovery. That redesign is unnecessary for the first indexed-reader experiment.

### 5. Timing behavior includes more than a shared offset

The ring append helper rejects an input earlier than a stored threshold and subtracts its current origin while retaining original time in metadata. The mux wrapper stores a start base. Audio then subtracts that base; video also applies an adjustment and can add one nominal frame when the proposed advance is less than one fifth of a nominal frame period. Sequence continuity and output timing are separate mechanisms.

Commit contains an unconditional **`Sleep(200)`** after retaining the manager and copying boundary fields. A future-end request has additional waiting, and the worker adds a further 200 ms margin before reevaluating future footage. Sample flags 1/2 dispatch separate control slots; the complete pause/resume contract and all writers of timing adjustments were not reconstructed.

**Apply to F10:** preserve its common acquisition clock and explicit session-time mapping. Do not copy fixed sleeps or timestamp corrections to make output appear smoother. Measure tail availability, scheduling and actual frame identity separately.

### 6. MP4 completion is a real stage

The current writer finalization stops tracks, checks track duration, patches media-data size and writes movie/track metadata. It includes a conditional reserved-metadata placement branch. This is not proof that every live save selects that branch or that it accounts for a particular speed difference. Malformed-track checks cover zero samples, missing video sync samples and required codec configuration. A selected finalize callback waits for the stream count, handles failure and can delete malformed output before reporting completion.

**Apply to F10:** keep finalization, validation and final-file publication explicit. Reuse a reviewed media writer rather than implementing MP4 boxes from recovered NVIDIA pseudocode. Do not advertise Saved at request acceptance. Faststart remains a measured stage; removing it did not help our earlier controls.

## Comparison with F10 and measured evidence

F10 already knows segment start/end, pins sources, snapshots inputs and **stream-copies** its export. The application does not run a separate ffprobe for every segment. Its concat manifest includes explicit duration lines. Repeated stream discovery occurs inside the FFmpeg concat demuxer: tagged 9.0.2 `open_file` invokes both `avformat_open_input` and `avformat_find_stream_info` per child, even when duration is supplied. [Primary concat source](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/libavformat/concatdec.c).

The [controlled precision/export report](../../artifacts/precision-export-results-20261024/report.md) measured hybrid median 11.265 s / p95 12.123 s, and direct-copy/edit median 10.209 s / p95 10.768 s, with twenty observations in each group. Both passed their exact-video checks and failed median <=3 s / p95 <=5 s. One indexed control spent 8.745 s assembling segmented input and 0.521 s on a subsequent monolithic-input copy. This localizes a promising cost area, without isolating per-file probing, I/O, decoding or CPU as the sole cause. No new latency experiment was run here.

F10's current source locations are `src/ReplayEngine.cs:115` (segmentation), `src/ReplaySelection.cs:17`/`:37` (validated manifest and current whole-GOP selection), `projects/F10Recorder.Windows/ReplayCache.cs:159` (accepted-save ownership), `:285` (snapshots), `:297` (duration-bearing concat), and `:310` (publication after exit/size check). Current production validation is weaker than the throwaway prototype's structural probe, and durable recovery is not established by a disposable-cache index. See the [F10 source comparison](../../artifacts/shadowplay-save-focused-20261024/f10-comparison.md) for hashes and details.

## Recommended next prototype

This is a proposed independent implementation, not an approved production architecture change:

1. **Index each closed immutable chunk once.** Bind session identity, file identity/integrity, stream schema/extradata, timebases, packet PTS/DTS/duration, keyframe/dependency facts and byte locations where valid. Handle manifest rollover/rewrite and incomplete tails. A demuxed packet can span container regions; an offset and length alone are not universally sufficient. Use a format-aware reader or owned packet storage where required.
2. **Make the reader consume the index.** Supply selected encoded packets and verified stream descriptions to one mux operation, avoiding one process or full stream-info analysis per chunk at save time. FFmpeg's public mux API supports caller-supplied stream parameters and encoded packets; it requires correct timestamp units/order and explicit packet ownership. This is a candidate interface, not a claim that the shipped essentials executable exposes a reusable library ABI. Exact helper/library packaging, authentication and licensing need their own review. [Tagged public API](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/libavformat/avformat.h).
3. **Keep precise boundaries and source leases.** Test hybrid head/tail handling with the same frozen request endpoint and pins. Do not adopt NVIDIA's keyframe movement, diagnostic-only discontinuity policy, one-frame timing correction or arbitrary sleeps.
4. **Measure the entire trade.** First reuse the frozen generated inputs to compare the reader paths. Record index construction cost separately, then include it in continuous-acquisition overhead. Measure process startup, input preparation, packet reading, boundary encoding, muxing, validation and publication. Count file opens and I/O/CPU only with a declared observer and measure observer cost. Do not claim a speed win by moving work outside the timer unnoticed.

After offline correctness, prospectively admit twenty phase-varied five-minute 1080p60 saves with controlled trial order. Keep median <=3 s / p95 <=5 s and <=one-frame start/end/duration rounding, full coded-frame identity, strict per-stream timestamps and required stream schema. Use uniquely coded audio markers, rather than modulo-one-second pulses. The eventual end-to-end timer includes real request admission, tail closure, pins, authentication, validation and publication; the old closed-input fixture omitted several of these stages.

Before integration, exercise pruning during overlapping requests, session retirement, absent/one/two audio tracks, torn/missing/corrupt indexes and inputs, disk admission/copy fallback, cancellation/failure, checkpoint stability and every advertised codec/schema transition. Queue ownership needs an explicit update/successor to ADR 0005, which currently describes one save without a queue. No ADR or production policy was changed by this investigation.

## Question closure and remaining work

| Investigation question | Status and practical conclusion |
| --- | --- |
| Current buffer records/storage | Answered for the examined startup/append/read path: indexed encoded samples with file backing; keep the idea, not NVIDIA's constants. |
| Current save and sample ordering | Answered for selected routines: timestamp/keyframe selection, copied sample reads, ordered mux handoff and finalization. Indirect interfaces remain partly unresolved. |
| Whole-save overwrite protection | Partially answered: manager refcount and per-read locking established; complete immutable-range protection unproved. Preserve F10 pins. |
| Exact arbitrary-frame cuts | Unresolved for ShadowPlay and not needed to imitate: F10 must satisfy its own accepted precision contract. |
| Actual ShadowPlay save latency | Unmeasured here. Acknowledgement is not completion. Use a separate controlled Windows trace if needed. |
| Useful F10 adaptation | Answered as a prototype direction: closed-chunk index plus consuming packet reader/mux path, with explicit timing and validation. No speed gain is claimed yet. |

REA's native Windows static provider worked. Its relevant native runtime-observation/process-capture tools remain unsupported on Windows; IDA is optional and unconfigured, not a blocker for this completed static pass. A future runtime trace must use a declared Windows tracing method, finite workload/storage plan and restoration receipts. No runtime approval, access or tool configuration was silently broadened here.

Historical testing attributed one desktop protection failure to a task-owned Computer Use helper and recovered after normal helper cleanup. Avoid creating that helper during a prospective replay measurement. The preserved successful reference report is [here](../../artifacts/nvidia-isolation-retry-20261014-215249/assessment.md); this is historical evidence, not a current NVIDIA status check.

## Evidence ledger

All addresses below are **current 11.0.9.565**, bound to the hashes above. Full selected entries, complete analyzed body ranges, native limitations and corresponding `.c` extracts are in the [index](../../artifacts/shadowplay-save-focused-20261024/evidence-index.json).

| Finding | Native entry | REA Evidence ID |
| --- | --- | --- |
| Replay startup | capcore `0x180078ab0` | `ev_66301e56e0a7329ba040e9632f2ca7c41d50cdf4200d48930cfc22a85f5a4d93` |
| File-backed ring constructor | capcore `0x180079c50` | `ev_3ab893b0e1d5ca477b64aba9c525a1dc878e1de4d57e6098550c1b79b5a861d5` |
| Metadata append/overlap eviction | capcore `0x18007c620` | `ev_2489c89be54c598b2de137647b8f9ee5ae3954bbfd4aa9f6de7fbd341c5da526` |
| Current range/keyframe selection | capcore `0x18007a920`, `0x18007ae90` | `ev_fdcc4c923cb33e72a2fb9b6da4af48170a7ccb53799c115ee4937b9f627a1b8c`, `ev_d401147503d12d33c2e259298bf7f153f0e24efa9306be17e823e7f815453f32` |
| Save entry / commit | capcore `0x180077b30`, `0x180072380` | `ev_5702ee27e6f7e03d685932adefbdd2d1ee35980eb92242ac2d5220b9c6f1e925`, `ev_0b4e272a9c396f11199da44978471dd50e353a6fa95cd64e9494dd661aaed021` |
| Save worker / per-sample read | capcore `0x180072c00`, `0x18007bd80` | `ev_e26554257afdf9a343adee13d4613ee44c6350b82ee77969121461e22e8dc9dc`, `ev_ea0055f668c5cdebf6423d7eca3e1d85fcffda648906e9afb806feba2d56a09b` |
| AddRef / Release | capcore `0x18007d240`, `0x18007d9e0` | `ev_2ea095b5608bde216da12f250bda67df9af3703d5c1902d964d0371c8495e2dc`, `ev_09abd96250d33093bb0d369bb9932c22f33f37c6506ad63e869cfd0a83b4f382` |
| Video/audio timestamp handoff | capcore `0x180084990`, `0x1800845b0` | `ev_30518a0553985f320eba1dbb8b56a5dbbfb1b2c469dc1a0760efed9eab628ccd`, `ev_e79f303f439b61a2ba5b95d223139250ca27989d3724f90a3339ce75952d523d` |
| Lower stream write / mux write | nvmf `0x180037340`, `0x18003ffe0` | `ev_ce902d84c17ee389bc74cc3f9ce7b6d7c9ac0b5e4963ef5c8c40aea5c84a5314`, `ev_fde3430f2a73a567287a9ca8ca908ead30a0708e544abe52077a8d035905f70d` |
| Writer chunk ordering | nvmf `0x180052120` | `ev_e6f543ac4c20888a28077ff016dc3d7eec40db4ee082be471182e5b693013a41` |
| MP4 finalization / stream completion | nvmf `0x180055230`, `0x1800353d0` | `ev_a58bc6b90b613dbaa9b6e24948b89e6a3653eaaa3434aa69488cc7bcdaabd498`, `ev_32158a080cab7d151da28fdab2c110bcbaf91a41d3a9da926d349bc1aa8f1a87` |

Local supporting material: [prospective scope](../../artifacts/shadowplay-save-focused-20261024/plan.md), [old-build review](../../artifacts/shadowplay-save-focused-20261024/retained-review.md), [current-build skeptical review](../../artifacts/shadowplay-save-focused-20261024/current565-skeptical-review.md), [closure](../../artifacts/shadowplay-save-focused-20261024/closure.json). Original vendor binaries, raw bundles and derived third-party extracts remain excluded from commits.
