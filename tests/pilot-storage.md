# Actual task-root storage observations

This verification-only adapter measures a **fresh flat tiny-file root on Windows**.
It reuses `ReplayStorage.ObserveCapacity`, `Observe`, `Bytes`, `ReplayFiles` and
`Native` directory anchors. It does not change production recording/storage policy,
authenticate source media, or implement an engine/process lifecycle adapter.

```powershell
./tests/pilot-storage.ps1 -Plan
./tests/pilot-storage.ps1 -Plan -Observe -DataPath artifacts/request.json
```

Default/explicit preview returns the exact proposed host/assembly/arguments and
effects without stat/hash/resolve/build/mkdir or process/native observation.
Actual execution requires `-Observe` or `-Checks` and `-NoBuild`. Supply the
absolute pinned SDK host and this checkout's task-built verification assembly.
No PATH/engine discovery, provisioning or automatic build happens in the wrapper.

## Build and checks

Use the pinned SDK from `global.json`, an existing package cache and a dedicated
artifact output. For example, with `$sdk` set to that absolute `dotnet.exe` path:

```powershell
$build = Join-Path $PWD 'artifacts/pilot-storage-build'
& $sdk restore projects/F10Recorder.WorkerChecks/F10Recorder.WorkerChecks.csproj --locked-mode --artifacts-path $build -p:NuGetAudit=true -p:NuGetAuditMode=all
& $sdk build projects/F10Recorder.WorkerChecks/F10Recorder.WorkerChecks.csproj -c Release --no-restore --artifacts-path $build -m:1 --disable-build-servers -p:UseSharedCompilation=false
$assembly = Join-Path $build 'bin/F10Recorder.WorkerChecks/release/F10Recorder.WorkerChecks.dll'
./tests/pilot-storage.ps1 -Plan -Checks -NoBuild -Sdk $sdk -AssemblyPath $assembly
./tests/pilot-storage.ps1 -Checks -NoBuild -Sdk $sdk -AssemblyPath $assembly
```

Set the normal SDK root, CLI home and package-cache variables consistently with
the existing local SDK/cache. No publish, RID duplication, lock update or package
provisioning is part of this route. The Windows CI job invokes the same checks
against its already-built WorkerChecks assembly and propagates the exit code.
No hosted result is implied by a local run.

The wrapper starts only that DLL's early storage entry point through dotnet,
without a window. It records the exact invocation/PID/start time and retains
bounded raw stdout/stderr plus a receipt in new `artifacts/pilot-storage-launch-*`
files opened with CreateNew. Deadline is 55 seconds plus up to five seconds to
stop/verify the exact owned process; combined raw output is capped at 128 KiB.
There is no child tree/other-PID cleanup. Timeout, truncation or unconfirmed exit
cannot be reported as a successful observation. Only complete normally exited
child JSON is emitted as success. The helper itself spawns no processes.

**Overall effects include writes:** initializer creates a fresh root/marker;
checks/Observe create fixed tiny nonmedia files; checks persist reports; wrapper
persists its three launch records. Observation itself opens metadata/read handles
and never writes, truncates or deletes observed files. No native UI, shortcut,
recorder, pipe, startup, NVIDIA/vendor, engine/probe, GPU or capture operation.

## Public session and bounded inventory

`PilotStorageObservation.Create(root, ReplayFiles?)` is the verification session
boundary. It restricts roots to the current checkout's `artifacts`, pins ordinary
ancestors, rejects unverified aliases/reparse ancestry and atomically creates the
root with the Windows create-directory primitive. Existing/colliding directories
or files are not reused. Marker allocation is part of the inventory; marker text
from caller JSON never establishes ownership. The creator retains the real root
identity and anchors until disposal. A rejected initializer may leave its own
partially initialized fresh root as evidence; it never deletes prior material.

`Observe(binding, declaredLeafFiles)` measures only that session's root. It
allows at most 31 declared ordinary leaf files plus its marker, each at most
1 MiB. Flat enumeration is bounded and rejects missing/unlisted/duplicate,
nested-directory or reparse entries. There is no recursive inventory. Read-only
file handles prevent write/delete while extents are observed and rechecked.
Root identity, frozen plan/source/cutoff bindings and destination volume/unit
must remain consistent. Unsupported unit sizes/aliases fail explicitly.

Complete output separates file identity, logical EOF, allocated bytes, actual
volume/allocation unit and caller-available capacity. The final capacity call
follows inventory; retained allocation already affects caller-free and must not
be subtracted again. Shared physical extents count once; contradictory shared
allocation/EOF/unit records and cross-volume surprises are incomplete results.
Native zero allocation/free is distinct from an unavailable observation.

If any member fails, `Complete=false` and `UsedBytes=null`; partial extents and
the reason remain. An observed first file followed by a denied/changed second
file can never become a cheaper complete total. No automatic retry/reset.
Native window timestamps and monotonic elapsed time/age describe only this
observation; a window over two seconds rejects. External growth after the window,
physical peak/scratch, quota enforcement and future output remain unmeasured.

`UsedBytes` covers the complete declared **flat root only**, including its marker.
Wrapper records, request/plan files, build outputs and other control artifacts
outside that root are separate retained evidence charged to the task's artifact
budget. They are not silently included in a root's native total. This slice does
not establish a complete physical allocation budget for an eventual media pilot;
that integration must account for all additional control/source/output paths and
unwritten finalization obligations. Fixture budgets do not consume or activate
the proposed 4.5 GB media experiment allowance.

## Native output and preparation assessment

`--pilot-storage-observe <request.json>` is distinct from preview. The request is
an ordinary UTF-8 JSON file under this checkout's artifacts, at most 1 MiB, with
only these fields (unknown properties are rejected):

```json
{
  "root": "ABSOLUTE FRESH ARTIFACT ROOT FROM THE PREVIEW",
  "plan_sha256": "EXACT PREVIEW DIGEST",
  "source_manifest_sha256": "64 HEX CHARACTERS, STILL SUPPLIED",
  "cutoff": "302/1"
}
```

Generate a preparation preview using that exact absolute root before creating
the request. Hashes must be 64 hex characters; source digest/cutoff/plan remain
opaque supplied bindings. The command atomically creates the root, marker and
two fixed literal text files (`first\n`, `second\n`), observes them and prints the
existing Python observation fields plus separate `native_storage` evidence.
It cannot open an old media root. Root/source/IDR/CSV/stream qualification is not
inferred from a hash-shaped string. Every `--pilot-storage*` option, including
bad counts/unknown routes, stays in the storage-only dispatcher and cannot fall
through to the general worker/UI/capture entry point.

```powershell
./tests/pilot-storage.ps1 -Plan -Observe -NoBuild -Sdk $sdk -AssemblyPath $assembly -DataPath artifacts/request.json
./tests/pilot-storage.ps1 -Observe -NoBuild -Sdk $sdk -AssemblyPath $assembly -DataPath artifacts/request.json > artifacts/native-storage.json
python -B tests/pilot_collector.py --assess artifacts/native-storage.json --root 'THE SAME ABSOLUTE ROOT'
```

Native command exit 0 means complete storage observation, 1 means incomplete,
2 means rejected input/launch. A Python assessment exit is its separate existing
storage/preparation contract. Feed only complete output to the assessment.
`provenance=caller-supplied-unverified`, `facts_verified_natively=false` and
`execution_enabled=false` remain unchanged in the assessment. A storage-eligible
toy-root example is only a contract demonstration, not complete pilot admission.

Fresh in-process native metadata has origin `native-storage`; fixtures injected
through `ReplayFiles` have origin `controlled-system-boundary`. JSON origin
labels cannot authenticate themselves after persistence. Re-reading an old
receipt performs no current OS check and its stored age does not advance.
Retain completion timestamps, source/binary/input hashes and provenance; rerun a
separately admitted observation for current facts. A snapshot cannot be rebound
to a changed supplied contract while converting it to preparation JSON.

## Frozen independent criteria mapping

| Criteria | Implementation/controlled checks and remaining limits |
| --- | --- |
| N01 | Inert default/explicit preview; separate explicit native route and fail-closed dispatcher. |
| N02 | Real fresh-root volume/unit/free and literal EOF/byte preservation, including marker allocation. |
| N03 | Separate native/controlled/supplied domains; forged request labels reject; Python flags stay false. |
| N04 | Atomic creator, anchors, root identity, immutable supplied/native destination bindings and snapshot conversion. |
| N05 | Known-zero vs unavailable; missing/regular-file ancestry, invalid unit and lexical aliases reject. Physical permission/reparse/mount changes deferred. |
| N06 | Positive two-file control then second-file denial/change retains partial provenance with null total. |
| N07 | Controlled shared extents count once; allocation/EOF contradictions reject. No actual links created. |
| N08/N09 | Existing preparation arithmetic/reserve policy unchanged; native-to-assess format demonstration does not promote execution or full-budget proof. |
| N10 | Finite flat root, tiny files, unchanged bytes and retained failures. No recursive inventory or physical exhaustion. |
| N11 | Exact native interval/IDs, source/build/command receipts and bounded serialization/output; persistence/cleanup failures cannot be success. Old JSON stays supplied. |
| N12/N13 | Engine/process/trust/lease/producer cleanup adapters deferred. Wrapper only supervises its own nonmedia verification invocation. |
| N14 | Existing project links/seams reused; early kernel-storage route, maintained headless CI check, no production policy changes. |
| N15 | No system-audio/NVENC/source-clock/IDR/CSV/speed/tail/physical-peak/margin or release qualification. Original issue13 criteria, #9/#12 and all release gates remain open. |
