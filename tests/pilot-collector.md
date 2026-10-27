# R1 pilot preparation and supplied storage observations

This is a **non-executing preparation slice**, based on the reviewed
[output-demand report](../docs/research/replay-output-demand.md). It describes
one generated 304-second 1920x1080/60 H.264 NVENC 20 Mbps source with generated
stereo 48 kHz AAC 192 kbps and one 300-second stream-copy MP4 export. Generated
audio is not the actual system-audio reference. No production estimator or save
policy changes. Issues #9/#12/#13 and all release gates remain open.

`pilot_collector.py` uses only the Python standard library. It has no process,
engine discovery/repair/download, filesystem observation, media parsing or native
cleanup adapter. `execution_enabled` is always false, including successful
assessments and settlements. It cannot run the proposed experiment.

## Preview and commands

```powershell
python -B tests/pilot_collector.py --plan --root artifacts/r1-pilot-pending
python -B tests/pilot_collector.py --root artifacts/r1-pilot-pending
```

Both commands print the same JSON to stdout. They do not stat, resolve, hash,
create or inspect the root or any tool, and do not build anything. Lexical
`..`/control-character roots are rejected; this does not qualify aliases,
reparse points, path ownership or an actual destination volume.

The preview contains separate argument arrays, current/future effects, limits,
pending prerequisites and a SHA-256 of its canonical JSON values. This is a
contract digest, not a hash of current engine, source or collector files.
The source recipe comes from the existing generated-marker prototype without
importing its provisioning or launch route. It includes 20-bit frame numbers
and complementary rows, closed one-second GOP intent and TS segments/CSV.
It uses `-n`, never overwrites files, and has no codec fallback.

The export array contains `<qualified-relative-start>` and a pending verified
tool. Requested source interval is [2,302) seconds. An adapter must qualify
actual CSV/IDR/sample/stream boundaries, build the selected snapshot/concat
manifest with explicit durations and determine that relative offset. A filename
index is never a source clock. The template is not a ready command or the
production export path. Short controls, preflight commands, copying and final
publication are not implemented. Limits retain at most two controls of at most
two seconds, 12 child invocations and 1800 cumulative seconds; enforcing actual
child/time/order bounds remains adapter work.

## Public Python boundaries

Import `pilot_collector` with `tests` on Python's import path:

| Boundary | Result and verification limit |
| --- | --- |
| `preview(root="artifacts/r1-pilot-pending")` | Inert description; no source/tool/root qualification. |
| `assess(plan, observation)` | Storage/preparation eligibility under supplied assumptions; no native observations or execution admission. |
| `settle(plan, assessment, after)` | Controlled settlement/accounting receipt; it never stops, waits for or inspects a process. |

`assess` requires the exact unmodified preview. Changing its root, recipe,
arguments or limits invalidates a previous plan binding. Its observation is a
JSON object with these fields (all byte/time integers exclude booleans, floats,
negative values and values exceeding signed 64-bit range):

```json
{
  "provenance": "caller-supplied-unverified",
  "plan_sha256": "COPY THE PREVIEW DIGEST",
  "snapshot_binding": {
    "source_manifest_sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "cutoff": "302/1",
    "destination": "artifacts/r1-pilot-pending",
    "volume": "controlled-volume",
    "allocation_unit": 4096
  },
  "current_binding": {
    "source_manifest_sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "cutoff": "302/1",
    "destination": "artifacts/r1-pilot-pending",
    "volume": "controlled-volume",
    "allocation_unit": 4096
  },
  "capacity_status": "known",
  "age_ms": 0,
  "used_bytes": 1000000000,
  "next_unmaterialized_bytes": 2000000000,
  "unwritten_closure_bytes": 128000000,
  "poll_reserve_bytes": 1000000000,
  "caller_free_bytes": 43128000000
}
```

These are controlled literals, not machine facts. `controlled-fixture` is the
other accepted provenance label; neither label authenticates the observation.
The source digest and cutoff are opaque supplied bindings, not parsed media
qualification. Bindings must match, include the same nonempty volume identity
and power-of-two allocation unit, and name the plan's exact destination.
Unknown/denied capacity (`caller_free_bytes` may then be null), invalid binding,
or age above 2000 ms makes the snapshot ineligible. This freshness field is
supplied; there is no native clock or observer in this slice.

Hard budget is 4,500,000,000 bytes; used-byte stop is 3,350,000,000. Initial
assessment requires the full 128,000,000 unwritten closure and 1,000,000,000 poll
reserves. The stop gap covers both with 22,000,000 slack. Known used bytes plus
next unmaterialized bytes plus both reserves must fit the hard budget. Fresh
caller-free must cover the 40,000,000,000 floor plus only future obligations:
already allocated used bytes are not deducted again. These supplied byte values
must already account for the appropriate allocation units and retained files;
this code neither inventories nor rounds actual file allocation.

The example gives 4,128,000,000 used-plus-future and requires exactly
43,128,000,000 caller-free. One byte less fails. At the used stop, no next trial
is eligible. These are operational planning guards, not a kernel quota or a
proof of physical peak storage.

```powershell
python -B tests/pilot_collector.py --assess artifacts/my-controlled-observation.json
```

This command reads only the specified JSON, bounded to 1 MiB, and prints an
envelope containing its byte hash, supplied provenance and assessment. No files
are written by the CLI. The API receipt retains the supplied observation, plan
digest, resource arithmetic, eligibility/reasons and its own JSON-value digest.
`facts_verified_natively` stays false. Exit 0 means eligible supplied storage
snapshot, 1 means ineligible, and 2 means malformed/unreadable input or unsupported
JSON nesting. The entire result is serialized before stdout is written. A
recursion failure during decoding, assessment or final formatting returns a
shallow structured rejection with exit 2; it never prints a partial success.
The Python recursion limit and 1 MiB input budget are unchanged. An
execution flag is unsupported and rejected before any child could start.

`settle` takes the exact assessment and this supplied `after` shape:

```json
{
  "provenance": "controlled-fixture",
  "assessment_sha256": "COPY THE ASSESSMENT RECEIPT DIGEST",
  "binding": {"source_manifest_sha256": "SAME", "cutoff": "SAME", "destination": "SAME", "volume": "SAME", "allocation_unit": 4096},
  "closure_written_bytes": 32000000,
  "retained_used_bytes": 3382000000,
  "owned_producer_state": "unknown",
  "cleanup_outcome": "unknown"
}
```

Use the full actual binding, not the illustrative `SAME` placeholders. Written
closure must remain charged in retained used bytes and reduces only its matching
unwritten balance. Starting at the stop, 32,000,000 written bytes leaves
96,000,000 unwritten and retains the 1,000,000,000 poll reserve: total
4,478,000,000. Failed/unknown cleanup, producer presence/unknown liveness or stale
binding cannot produce a clean settlement or enable another stage. A supplied
zero exit code is insufficient. Poll reserve can be released only with matching
complete bindings, supplied absent producer and verified cleanup. `closed_cleanly`
then describes consistency of those supplied observations, never an observed
process/experiment outcome. `next_stage_enabled` remains false. There is no
settlement CLI or receipt writer; external persistence failure cannot be called
a successfully saved experiment by this preparation code.

## Controlled verification and deferred acceptance

```powershell
python -B -m unittest discover -s tests -p test_pilot_collector.py -v
python -B -m unittest discover -s tests -p test_experiment_contract.py -v
```

New checks use public preview/assessment/settlement/CLI boundaries. They retain
tiny JSON inputs and stdout receipts under fresh `artifacts/calibration-collector-writer/cli-fixtures-*`
directories, including rejected inputs. No fixture file exceeds 1 MiB. The
oversize-input guard uses a controlled in-memory file boundary. CLI tests use the
entry function without a child; external preview launch is just Python. No
media, GPU, UI, worker, profile, startup or vendor effects. The existing game
contract suite remains separate and removes only its own temporary fixtures.
The nested-JSON regression retains real bounded 5000-level decoder and 2000-level
extra-field fixtures plus an ordinary eligible control under fresh
`artifacts/calibration-collector-json-depth-writer/` directories. Deep objects may
serialize completely or be rejected for unsupported nesting; no nesting support
level is promised across Python runtimes.
Windows CI runs this standard-library suite beside experiment admission and
propagates its exit code. This route adds no engine, native, GPU or capture work;
hosted success cannot qualify the deferred measurement adapters or release gates.

| Independent matrix criterion | Coverage in this admitted slice |
| --- | --- |
| C01 inert preview | Implemented/controlled: default and explicit preview, missing root, filesystem/process tripwires, execution flag rejection. |
| C02 fixed R1 recipe | Implemented/controlled: source array and one export template; actual source/offset qualification and control/preflight arrays deferred. |
| C03 positive literal | Implemented/controlled: exact 4,128,000,000 and 43,128,000,000 observations; execution remains blocked. |
| C04 unknown/insufficient capacity | Implemented/controlled: one-byte/zero/unknown/denied/stale/binding cases; native capacity adapter deferred. |
| C05 stop/reserves | Implemented/controlled: used stop, hard-boundary one-byte excess, malformed integers, old insufficient stop-gap rejection. |
| C06 closure/failed cleanup | Implemented/controlled accounting only: retained written charge, held poll, incomplete settlement; actual producer/receipt writer deferred. |
| C07 identity | Plan/argument/source/cutoff/destination supplied bindings implemented; native executable trust/lease/current host and collector file hashing deferred to future adapter/handoff. |
| C08 source selection | Explicit requested interval and blockers only. CSV/IDR/media qualification, recovered source availability and stream schema deferred. |
| C09 observation quality | Supplied snapshot age checked. Actual time-series gaps, EOF/allocation/peak/packet/configuration parsing all deferred/unmeasured. |
| C10 invocation bounds | Preview limits only. No runner or stage/time/retry enforcement; no actual command attempted. |
| C11 owned cleanup | Supplied outcome accounting only. No PID/tree ownership inspection, cancellation or native cleanup adapter. |
| C12 paths/retention | Lexical root validation and retained tiny test attempts only. No output creation/publication/deletion, alias/ownership/native-path qualification. |
| C13 receipts/sensitivity | Implemented/controlled: plan/input/receipt digests, full supplied observations, zero launches, adverse capacity/identity responses and malformed input rejection. No durable experiment receipt writer. |
| C14 preservation/limits | No production changes or recorder/native effects. No engine readiness, source identity, marker/A/V/physical peak/storage margin/speed/tail/release acceptance established. |

The next implementation scope must supply reviewed native observation, trust,
selection, snapshot, bounded execution and owned cleanup adapters before seeking
separate engine/GPU/media execution authorization. Passing these controlled
checks does not make that scope implemented or authorize the pilot.
