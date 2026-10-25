# Managed indexed-reader experiment (E1 follow-up, throwaway)

This follow-up measures whether a persistent managed reader and combined head/tail encoding reduce the remaining cost in [the original indexed-reader prototype](indexed-reader-prototype.md). It reuses the same hashed controlled footage, packet/time index, pinned FFmpeg 9.0.2, full frame oracle and unchanged median <=3 s / nearest-rank p95 <=5 s targets. No production integration or fullscreen/coexistence work is included.

## Candidate and honest cost boundaries

`IndexedReaderPrototype/Program.cs` is a small .NET 10 console helper, built with the repository's pinned SDK. It loads metadata once and then waits for requests on its owned standard input. A request receives only a duplicated write handle for that trial's owned FFmpeg stdin. The helper reads pinned chunks, patches the same fixture transport timestamps/continuity fields and writes directly to that handle. There is no public listener, NVIDIA process attachment, injection or private vendor API invocation.

The helper stays alive between saves in a collection; its build, index load and startup costs are separate and recorded. No source payload or merged MP4 is prepared outside the save timer. It allocates reusable buffers, but actual selected bytes are read during the request. Its first real job can include JIT/allocation work. Tiered compilation is disabled prospectively for this experiment. Parent thread CPU and reader-process CPU are reported separately; parent thread CPU alone would undercount the new reader.

Combined-boundary mode uses two encoder outputs in one FFmpeg process per save. It still initializes independent NVENC encoders; it does not reuse NVENC contexts between saves. The first queued boundary call is not consumed until the second runs the combined command. An odd pending boundary is flushed before reading. All actual work remains within the original total save timer; odd-flush work can move between stage labels. The declared five-second/five-minute intervals have either zero or two partial boundaries.

All source, timestamp, codec, playback and audio limitations of the original experiment remain. In particular, this is a known H.264/AAC/one-second-GOP transport fixture, not a general packet/dependency index. Prepared metadata does not establish live index-maintenance overhead. Periodic audio markers do not establish unique absolute audio identity or live A/V sync.

## Reproduction

Start from a fresh ignored experiment directory. The seed is the completed first indexed-reader experiment, not a user media folder. Preparation verifies every source against its retained index and the engine against source pins.

```powershell
python tests/indexed-reader-optimized.py --root artifacts/indexed-reader-new --plan
python tests/indexed-reader-optimized.py --root artifacts/indexed-reader-new --seed artifacts/indexed-reader-20261024-a --prepare
./.tools/dotnet/dotnet.exe build tests/IndexedReaderPrototype/IndexedReaderPrototype.csproj -c Release -o D:/repository/f10-recorder/artifacts/indexed-reader-new/reader-build -p:BaseIntermediateOutputPath=D:/repository/f10-recorder/artifacts/indexed-reader-new/reader-obj/ --ignore-failed-sources
python tests/indexed-reader-optimized.py --root artifacts/indexed-reader-new --qualify
python tests/indexed-reader-optimized.py --root artifacts/indexed-reader-new --pilot
python tests/indexed-reader-optimized.py --root artifacts/indexed-reader-new --benchmark
python tests/indexed-reader-optimized.py --root artifacts/indexed-reader-new --validate
```

Use the workspace Python runtime with NumPy. No media package is installed. The helper project has no package references and builds without the desktop UI. Output/obj paths are explicit so generated build files remain in the experiment. Windows handle duplication/hard links and executable leases can need an authorized outside-sandbox invocation. Only task-owned processes/handles may be controlled.

## Prospective progression and evidence

Five short phase-varied qualifications precede three five-minute controls in fixed order: original Python reader/sequential boundaries, managed reader/sequential boundaries, managed reader/combined boundary process. All controls receive the unchanged offline precision checks. A pilot improvement under 0.20 seconds versus the same-run Python control triggers user involvement before expanded optimization/tracing. A useful qualified pilot permits one fresh twenty-save batch, without pooling or discarding slow observations. Pilot ranking is preliminary, not a statistical estimate that the managed variants differ.

The saved optimization plan binds both Python harnesses, the C# source/project, exact helper/host bytes and index. Each reader lifetime has PID/startup/hash/exit receipts. The original harness and original completed evidence are preserved. A generated real-time NVENC producer runs during reference timing; its bounded progress sampling has the same limitations as the first experiment. Stop it and the reader before exhaustive decoding. Budgets remain 30 GB unique-file evidence, 2 GB next-trial reserve and 40 GB free disk reserve; preserve rejected trials and source versions.

Save time includes snapshots, boundary processing, authenticated launches, actual reader/pipe/mux work, faststart, structural validation and publication. Helper startup and index preparation are explicitly separate; live request/tail latency and fsync durability remain unmeasured. No capture devices, app UI, shortcuts, startup, installed recorder or NVIDIA settings are changed. Generated NVENC/CPU/disk load is real and must not overlap another measurement session.

This is a feasibility experiment. A controlled pass cannot choose the production helper/packaging/IPC interface, prove all audio/codecs/schema changes, settle ownership/recovery, or qualify actual game capture. Those remain separate work.
