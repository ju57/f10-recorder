# Storage admission and producer commitments

Capture requires qualified poll/stop headroom for the actual profile and volume.
Missing qualification remains `Unmeasured` and rejects acquisition even above
2 GB. Initial startup, explicit Resume and an active Apply preflight before
engine/audio discovery or preference changes. The prepared capture arguments
must match the scope again before process creation. A storage stop remains
durable; restored capacity alone never clears it.

The current empirical cell is primary 1920x1080, H.264 NVENC, 60 fps, 20 Mbps,
five minutes, actual 48 kHz stereo system audio, and the qualified local NTFS
volume/unit. No model ships with the app. This candidate consequently cannot
start unmeasured profiles; a successful calibration and separately authorized
activation are still required. This is an admission restriction, not proof of
physical exhaustion safety or qualification of other profiles.

The production replay engine always joins the account-wide reservation ledger.
An accepted save must have numeric output/finalization demand. An unfinished
tail in the reference envelope reserves output at the full 1,000,000,000-byte
input-domain ceiling, plus a complete snapshot envelope: rounded input ceiling,
305 allocation units for per-file rounding, and 64 KiB rounded concat metadata.
This reserves capacity for empirical domain coverage; it does not predict an
arbitrary unfinished segment. Closed inputs must qualify again before copying
or export. If the tail is late, the selection exceeds the domain, or evidence
changes, the original accepted request fails truthfully with protected footage
retained. It is never silently moved, shortened to a later request or retried.

Allocated prefixes already affect caller-visible free space. The ledger charges
only remaining growth, sums same-volume obligations and keeps different volumes
independent. Capture reserves its own poll/stop allowance. The capture policy
excludes that same owner before applying its margin, avoiding a double charge.
Snapshot copies, concat and output/finalization join the same ledger. Native
identity, actual volume and allocation unit remain bound through materialization.
New admission and capture polling return a distinct busy result without waiting
for a competing ledger owner. Once a replay is accepted, snapshot/export claim
updates, producer sealing and release may retry transient lock contention for
up to 200 ms. Capture supervision retries a busy observation after 5 ms within
the same 500 ms freshness window; contention never refreshes its timestamp.
Persistent contention still fails work and retains uncertain claims and media.

Export checks storage and its model every 200 ms through the actual process
adapter, including after capture stops. More than 500 ms since a successful
observation, lost capacity, withdrawal or peak exceedance fails the export;
the adapter stops only its owned child. No publication occurs before confirmed
producer absence and successful claim release. Unknown cleanup retains claims
and evidence. These checks cannot prevent an arbitrary external writer from
exhausting a disk between observations; native pressure/quota qualification
remains a separate requirement.

Verified executable loading, process creation/job assignment and durable producer
callbacks can block. The adapter owns and joins each such operation while its
caller continues the same real storage checks. A failed check still waits for
an in-flight creation to yield its actual child handle before owned cleanup;
it never releases the executable lease or reports absence for an orphaned launch.
This preserves observation coverage through startup without widening500ms.

| Producer | Current integration | Outstanding obligation |
| --- | --- | --- |
| Replay acquisition | Qualified poll/stop growth, durable guard, actual job/worker proof | Fresh empirical qualification and active-worker acceptance |
| Accepted replay export | Input/copy/concat and output/finalization before acceptance; serial export; retained original endpoint/destination | Fresh saved-media proof for this exact consumer |
| Other profile workers | Same account-private ledger and actual-volume remaining growth | Native supported-volume/quota matrix |
| Queued replay requests | The current prototype rejects a second concurrent save | [Issue 15](https://github.com/ju57/f10-recorder/issues/15): one active plus two waiting; each waiting request must reserve its worst required inputs/output at acceptance, preserve failed-predecessor coverage and release only its own future growth |
| Manual recording | No manual producer/Start API exists in this worker | [Issue 28](https://github.com/ju57/f10-recorder/issues/28), after strategy issue 27: join before acquisition and extend working/finalization claims before each new growth interval; do not reserve one replay window for an unlimited recording |
| Recovery writer | Retained files are counted; no repair producer is introduced here | [Issue 31](https://github.com/ju57/f10-recorder/issues/31): reserve separate scratch/output before starting, yield to capture/accepted work, preserve originals and release only owned temporary resources |
| Interrupted/legacy owner | A sealed worker/job proof can reconcile future growth only after both are absent | Unknown, unsealed or legacy obligations stay reserved; PID age or model installation cannot clear them |

Queue, manual and recovery integration are open feature dependencies, not zero
obligations or delivered beta functionality. Issue 13 criteria 2–4 and G-STORE
remain open until their complete evidence and acceptance are mapped. The
verification collector may measure training sources without the production
ledger under its separately approved experiment budget; held-out sources join
the production coordinator and a task-private ledger without activating a model.

Focused checks live in [replay ownership](../tests/replay-ownership.md),
[output admission](../tests/replay-output-demand.md) and
[test effects](../tests/test-effects.md). Machine receipts and models stay under
ignored `artifacts/`.

Calibration startup failures retain an immutable engine/audio/progress/segment
health snapshot before shutdown. The controlled source requires the local
unlocked desktop, observes display-on and temporarily requests display/system
availability on its fixture thread, restoring that thread's prior flags on exit.
These verification-only measures do not change installed-worker power behavior,
the acquisition clock, the15second capture guard or the500ms storage gate. A
short acquisition diagnostic can establish startup availability; it cannot
qualify a storage model or replace the complete recording/held-out evidence.
