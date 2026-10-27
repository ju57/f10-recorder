# One guarded nonmedia process stage

This verification-only adapter supplies process evidence for issue #13. It runs
one reviewed tiny fixture through WorkerChecks' early `--pilot-stage*` dispatcher.
It does not run a calibration engine or qualify recordings. Issue #13's four
original criteria, issues #9/#12 and all twelve release gates remain open.

## Preview and explicit execution

```powershell
./tests/pilot-storage.ps1 -Plan -ProcessChecks
./tests/pilot-storage.ps1 -Plan -ProcessRun
```

Default/`-Plan` is lexical: no target stat/hash, native observation, directory,
build or process. Missing explicit DLL/hash prerequisites remain visible.
`PilotStageRunner.Preview(request)` likewise accepts missing identities without
reading their paths. Preview describes intent and does not admit execution.

Build WorkerChecks explicitly with the pinned SDK and locked, audited restore;
reuse an existing cache. Use dedicated task artifacts and the README build rules.
Neither this wrapper nor the runner provisions tools or builds implicitly.
For an already reviewed task build:

```powershell
$sdk = 'D:/repository/f10-recorder/.tools/dotnet/dotnet.exe'
$assembly = Join-Path $PWD 'artifacts/pilot-process-writer/build/bin/F10Recorder.WorkerChecks/release/F10Recorder.WorkerChecks.dll'
$sha256 = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
$root = Join-Path $PWD ('artifacts/pilot-process-' + [guid]::NewGuid().ToString('N'))
./tests/pilot-storage.ps1 -Plan -ProcessChecks -Sdk $sdk -AssemblyPath $assembly -ExpectedAssemblySha256 $sha256 -DataPath $root
./tests/pilot-storage.ps1 -ProcessChecks -NoBuild -Sdk $sdk -AssemblyPath $assembly -ExpectedAssemblySha256 $sha256 -DataPath $root
if ($LASTEXITCODE) { throw 'Guarded process checks failed; retain their evidence.' }
```

The sample SDK location is local; CI supplies its explicitly installed pinned
SDK. Computing a hash identifies bytes, not their review or authorization.
Execution requires a reviewed build and the declared effects in
[test effects](test-effects.md). The wrapper holds its own DLL read-only, rejects
missing/mismatched identity before controller launch, and records expected/actual
hashes. Its fixed modes are mutually exclusive: `-Checks`, `-Observe`,
`-ProcessRun`, `-ProcessChecks`.

For targeted checks, append `-CheckCase <name>` only with `-ProcessChecks`.
Registered names are exactly `all`, `S01`, `exit7`, `timeout`, `cancel`, `flood`,
`receipt-collision`, `cleanup-unknown`, `admission`. The actual C# dispatcher
requires exact spelling/case. It accepts only `--pilot-stage-checks <root>` or
`--pilot-stage-checks <root> --only <name>`, `--pilot-stage-run <request.json>`
and the internal fixture route. Unknown/mixed/count/case variants are intercepted
before general WorkerChecks initialization. `-CheckCase` cannot choose executable,
argv, fixture mode or a different entry point.

## Plan and execution boundary

`-ProcessRun -DataPath <absolute-request.json>` reads a strict request of at most
64 KiB, returns stage JSON and exits 0 only for stage success; stage failure is
1, malformed CLI/JSON is 2. `-ProcessChecks` exits 0 only when all selected
expectations pass. Expected negative stages therefore appear inside a passing
verification report. Existing final evidence is never reusable as a new request
admission or freshness proof.

`PilotStageRequest` contains `plan`, `plan_sha256`, `arguments`. The plan binds
stage ID, closed mode (`success`, `missing-product`, `exit7`, `wait`, `flood`),
canonical fresh root, tiny input path/hash/native identity, parent native identity,
host path/hash, assembly path and hashes of the five runtime build files, supplied
source hash/cutoff and a 1000–2000 ms deadline. Its digest is SHA256 of the UTF-8
snake-case JSON plan with ordinally sorted build keys. Exact argument equality is
also required: own assembly, `--pilot-stage-fixture`, owned root's `fixture.json`.
The checks' `BindFixture` creates this request; S01 retains standalone request
JSON in the suite root, and every stage retains its own plan.

Only the current controller's explicit `dotnet.exe` and its own WorkerChecks DLL
can launch. The build set is WorkerChecks DLL/deps/runtimeconfig, Windows DLL and
Core DLL. Ordinary file leases, SHA checks and directory anchors cover the host,
these files and input through process supervision; unsupported aliases/reparse
ancestry fail closed. Input is an ordinary task `.txt` artifact, at most 4 KiB.
Fresh native root ownership and parent identity prevent an existing root from
being adopted. The fixture receives a root/plan/nonce/input admission file, and
the runner clears inherited child environment injection settings before adding
SystemRoot, DOTNET_ROOT/X64, system PATH and its nonce. JSON cannot select a
process backend or supply a PID to kill.

This is a direct-process contract: one `Run`, at most one source-bound child,
no retry, fallback or descendants. Exact `Process`/SafeHandle, PID and start time
identify the owned child. No global process enumeration or PID-based reopening
occurs. The managed-engine allowlist and leases remain separate and unchanged.

## Outcomes and retained evidence

Success requires all of: actual exit 0, exact product `stage-ok` + LF (9 bytes),
exact progress `partial` + LF (8 bytes), complete capture, verified owned producer
absence/cleanup, complete closed-file observations and committed receipt readback.
Exit 0 with a missing product is failure. Launch failure, exit 7, timeout,
cancellation, output limit, cleanup uncertainty and evidence publication failure
remain distinct. Native exit is null when no child exit was observed. Stopping
the outer controller alone never confirms stage-child cleanup.

Each stage retains its atomic owner marker, plan, input/admission, launch record,
bounded stdout/stderr, available progress/product and receipt. Captured stdout
and stderr share a 64 KiB cap. Timeout/cancellation/flood retain partial capture;
`capture_complete=false` does not claim drained pipe tails. Flood additionally
sets `truncated=true`. Stop is directed only to the owned child, with a 3-second
cleanup allowance. Wait fixture work self-bounds at 10 seconds; each child must
remain within 15 seconds. The controller suite has a 45-second cancellation
budget inside the wrapper's 55-second deadline and 5-second controller cleanup.

Receipt publication uses a fresh pending leaf, Flush(true), close, nonoverwriting
rename and bounded readback/hash. The persisted evidence records
`candidate_success` and the facts before publication; only the returned stage
result can set `receipt_committed` and `success` after readback. A pending file or
preexisting sentinel is not a completed receipt. Collision preserves both and
returns `evidence-failed`, even after correct product/exit 0. This establishes
observed publication, not power-loss or crash-recovery durability.

`poll_reserve_held` stays true until verified producer absence and committed
evidence; unknown cleanup or failed publication cannot release it. Output failure
still blocks success. `execution_enabled` and `next_stage_enabled` remain false:
they never authorize media or another stage. `PilotStageIo` controls only external
start/cleanup/publication operations for checks. Controlled launch failure creates
no actual child; controlled cleanup unknown is returned only AFTER the real child
has exited. Its origin explicitly differs from native process cleanup.

Before/after observations reuse [the native storage observer](pilot-storage.md)
for CLOSED flat tiny roots. `after_closed_pre_receipt` excludes later receipt and
controlled collision writes. It does not cover a growing producer, whole suite,
peak, finalization or future free space. Native measurements, controlled outcomes
and supplied source/cutoff bindings remain distinct. The fixture source hash is
a declared test binding; candidate source hashes are recorded separately in the
delivery packet. No supplied hash authenticates media.

Stage receipts are capped at 64 KiB; request/control/suite JSON at 128 KiB
(CLI request read bound is 64 KiB). Each retained stage has at most twelve ordinary
files, each at most 1 MiB. The suite launches at most eight sequential children
(S01 pair, exit7, timeout, cancel, flood, collision, unknown); admission and
pre-cancel controls launch none. Count suite controls/build/wrapper evidence
separately from native stage totals. Retain failures and use a bounded task
artifact budget; these guards are not a filesystem quota or whole-pilot estimate.

## Criterion coverage and limits

| Frozen criteria | Evidence seam |
| --- | --- |
| P01–P07 | Inert preview/launcher checks; public Run identity and sentinel controls; actual Main malformed-route checks; static lease/dispatcher review |
| P08–P09 | Independently chosen S01 literal positive and exit-zero/missing-product pair |
| P10–P13 | Actual exit7, timeout, partial-ready cancel and finite combined-output flood; controlled failed launch |
| P14–P18 | Native process identity/exit, retained plans/logs, closed storage, publication collision and labeled unknown-cleanup control |
| P19–P20 | Existing Python preparation checks and unchanged closure arithmetic; supplied/native/controlled provenance distinctions |
| P21–P24 | File/JSON/output bounds, scoped verification-only diff, existing 13 native/4 launcher/13 preparation regressions, narrow Windows CI command |

Local checks, independent cross-tests, separate Standards/Spec reviews and hosted
CI each need a candidate-specific receipt. An earlier green build does not qualify
changed source. CI here adds no media or hardware acceptance. Deferred work still
includes managed-engine execution, qualified media/source/IDR/A/V/cutoff, concurrent
storage, full physical output/peak/tail, calibrated margins, supported-volume and
quota/permission/exhaustion cases, production low-space/Resume policy, installation
and all release acceptance.
