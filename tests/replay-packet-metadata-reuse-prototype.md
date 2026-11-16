# Packet metadata reuse prototype effects

`replay-packet-metadata-reuse-prototype.ps1 -PlanFile <plan> -Plan` only reads and
displays a prospective `packet-metadata-reuse-file-only-v1` plan. Run requires its
exact hash and a fresh output under the named ignored prototype root.

This diagnostic reads one exact retained five-minute cohort of 301 inputs, at
most 1,000,000,000 bytes. It opens native identity/extent observations and short
read-only no-write/no-delete leases, uses the existing four-worker cancellable
parser and bounded packet-preparation/rehash seams, and writes JSON plus tiny
nonplayable adversarial fixtures. It does not invoke the retired full hybrid
file-profile exporter. It starts only one isolated managed helper; no capture,
codec, media generation, playback, actual worker Save, publication, account
catalog, installed recorder, startup or game/vendor setting is involved.

The finite sequence is tiny controls, one 300-input metadata build, then
control / hit (one newest miss) / hit / control, followed by metadata retirement.
Every measured request opens all 301 protected inputs, reacquires identity,
acquires metadata or parses, extracts and freshly rehashes every source, consumes
the same packet payloads into a digest sink, joins workers and closes all readers.
Misses/copies use fresh parsing. Exact/unsupported fallback eligibility controls
use fresh parsing plus full-byte verification; corruption must win before fallback.
No IndexBatch or shipping completion fact is synthesized from a cache lookup.

Bounds: 90 seconds outer, 75 seconds helper work, 5 seconds owned-process drain,
512 MiB helper working set, 4096 handles, 64 MiB diagnostic storage and a 60 GB
free-space floor. These are cooperative/sampled limits, not OS quotas; samples
and process counter reads have included diagnostic overhead. A mismatch, closure
uncertainty or bound failure stops with evidence preserved. No retry, added
campaign, source deletion or expanded authorization follows from this route.

The experiment-admission discipline in `experiment-contract.md` supplies frozen
identities/order/limits/stop/retention. Its game-benchmark schema is inapplicable:
this route has no desktop, capture, profile or PresentMon collection roles. Its
dedicated Plan/Run wrapper supplies the file-only admission instead.

Setup recovery additionally supports `packet-metadata-hardlink-setup-v1` through
the same Plan/Run wrapper. It creates one 1,504-byte nonplayable fixture and its
required native hardlink, immediately captures the numeric Win32 error on
failure, and records decoded message, exact paths and process/runtime/filesystem
context. On success it verifies native same-object identity, a protected read
lease and closure, then removes only that pair. No retained source is opened.
Setup plans use fresh roots, 15 seconds outer, 512 MiB/4096 handles, 1 MiB reports
and the same 60 GB floor. Native-access approval may be required; no security
setting is changed and a copy never substitutes for the hardlink.
