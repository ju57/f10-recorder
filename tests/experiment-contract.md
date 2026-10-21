# Expensive experiment admission

Game benchmark collection requires a prospective JSON plan. Ordinary builds and
core checks do not use this contract. All evidence stays in a fresh directory
under ignored `artifacts/`. The controller still owns game/source setup, desktop
authorization and restoration; these scripts do not launch a game or change
NVIDIA settings.

## Required plan inputs

| Field | Meaning |
| --- | --- |
| `version` | `1` |
| `experiment_id` | Unique experiment identity |
| `environment` | Commit, machine, driver, display, game and measurement-tool identities |
| `source_qualification` | Evidence and method qualifying the workload/source phase; status alone is insufficient |
| `acceptance_criteria` | Nonempty object recording the intended claim, thresholds and decision method; admission freezes it but does not replace the statistical/media reviewer |
| `identity_files` | Repository-relative file paths mapped to SHA-256 hashes; include actual product assemblies/apphost, the approved test FFmpeg and the isolated profile settings |
| `window_seconds` | `[start,end]` relative to the collector trace, fixed across the batch; source-phase equivalence needs separate qualification |
| `sampling_interval_seconds` | Nominal status polling interval, retained from qualification |
| `maximum_sample_gap_seconds` | Largest admissible actual gap, including call and save waits |
| `trial_seconds` | Whole seconds, covering the window plus its end guard |
| `qualification` | One recording-on trial: `{"label":"qualification","mode":"On"}` |
| `trials` | Ordered finite list of unique label/mode objects; modes are `Off` or `On` |
| `storage` | Explicit byte limits described below |
| `retention` | Objects with experiment-relative `path`, `class` and `needed_for`; classes are `report`, `recording`, `build`, `worktree` |

Declare all six storage fields: `expected_bytes`, `budget_bytes`,
`trial_reserve_bytes`, `finalization_reserve_bytes`, `poll_growth_reserve_bytes`
and `minimum_free_bytes`. Expected storage covers qualification, the complete
batch, pinned/copied inputs and planned derivatives. Trial reserve is a
conservative estimate for one further trial. Finalization reserve covers output
needed for stopping, save completion and failure receipts. Poll growth reserve
covers writes between storage observations, including blocking control calls.
Minimum free bytes protects disk headroom independently of the experiment cap.
Choose these values for the concrete workload; there is no universal budget.

## Collection sequence

Validate the schema without building, starting a worker, contacting a pipe,
capturing anything or creating evidence:

```powershell
./tests/game-benchmark.ps1 -ExperimentPlan <plan.json> -ValidatePlan
```

Admission also verifies the declared file hashes. Build and prepare the isolated
profile first; use its explicit directory with the existing `-Action Start`.
For an authorized qualification, use:

```powershell
./tests/run-game-benchmark-batch.ps1 -ExperimentPlan <plan.json> -DataDirectory <experiment/worker> -Qualification
```

The controller prepares and resumes the game scene for the armed label using
the existing resume-marker procedure. Qualification output is retained separately
and contributes no batch observation. Review it, select the prospective window
and freeze the sample plan before running the batch with the same command without
`-Qualification`. The first batch admission stores the immutable plan snapshot.
Qualification source/environment/build identity and polling interval must match;
the final window must also fit the qualification trace. No individual window is
shifted automatically to accommodate late readiness.

Each admission checks trial order, prior validity, build/configuration hashes,
storage budget and disk reserve. An invalid or interrupted trial blocks the next
one, including manual invocations. Retain the rejected experiment and correct the
setup in a new experiment directory; do not clear its state to retry secretly.
The controller can explicitly resume a partially completed valid batch by
invoking the next declared trial through `game-benchmark.ps1`. The batch wrapper
always starts from the declared first trial and never skips existing receipts.

During collection, existing worker polls check readiness/control state, process
identity, recovery and gaps. Storage accounting runs in that same polling loop;
no new observer process runs per sample. Save waits contribute their existing
status polls too. Plan for their polling cadence and output growth. Sampling and
storage scans have observer cost and belong in setup qualification. Polling plus
declared reserves is an early-stop policy, not an operating-system disk quota.

Completion requires status samples bracketing both measurement boundaries,
acceptable gaps, a continuous capture identity, progressing capture, and complete
PresentMon window coverage. Failure and incomplete-trial receipts are retained.
Sampled health does not prove uninterrupted health between observations, image
identity, perceptual synchronization or overhead. Collector and presentation
clocks do not establish exact source-phase alignment.

The analyzer reads the frozen batch labels/window and checks receipt hashes.
Qualification is excluded. Valid but statistically inconclusive results stay
inconclusive: admission neither adds observations nor changes thresholds. The
older analyzer path remains available for historical evidence without a frozen
plan; it does not retrospectively qualify that evidence.

## Evidence closure

After the controller stops task-owned producers, inventory the experiment:

```powershell
python tests/experiment_contract.py close <plan.json> --root <experiment>
```

Closure records the plan, file hashes/sizes, retention purposes and present/missing
availability. It deletes nothing and prevents further collection in that
experiment. Future authorized retirement must retain a dated receipt and explicit
availability update alongside the immutable inventory. Recordings, builds and
worktrees are distinct retention classes; ignored paths are not disposable by
default. Retired media cannot support fresh decoding, listening or lineage checks.

## Offline verification

```powershell
python -m unittest discover -s tests -p test_experiment_contract.py -v
./tests/experiment-runtime-checks.ps1
```

These use fabricated traces/files and exercise missing fields, readiness,
process/recovery changes, boundary coverage, sampling gaps, storage limits,
qualification, frozen order, interrupted/invalid batch stopping and closure.
They run in Windows CI without capture or a GPU. Live gaming acceptance remains
separate.
