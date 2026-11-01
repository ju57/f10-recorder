# Output metadata comparison

This is a nonmedia collector check. It creates two ordinary800MB dummy outputs
with850MB preallocation, reads existing dummy source bytes through hardlinks,
and exercises real replay snapshots and output-capacity callbacks. It starts no
engine, child media process, device, window or recording.

```powershell
./tests/replay-output-metadata.ps1 -Plan -AssemblyPath <checks.dll> -OutputRoot <fresh-artifacts-root> -PlanFile <outside-root.json> -DummySource <dummy-session>
```

`-Prepare` binds consumers, SDK, launcher and the source-byte fingerprint. Review
the plan before `-Run -ExpectedPlanSha256 <reviewed-hash>`. One helper has a180s
whole-run limit, five-second cleanup reserve,20GB retained-family cap and60GB
caller-free floor. All modes and failed evidence remain.

The readers record separate native open, observation, refresh and close timings.
The proposed reader retains file identity through a metadata handle and
refreshes allocation/EOF on every scan using
[`FILE_STANDARD_INFO`](https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_standard_info).
It transfers the same handle by observed file identity across partial-to-final
rename. Cached allocation or directory-entry values do not replace native queries.

The writer pauses repeatedly so the sampler's allocation/EOF can be compared
with a separate fresh observation. Checks include growth, a head rewrite,
writer close, rename without reopening, and same-name identity replacement.
After producer absence and terminal coverage, release and post-release
observation share one sampler lock. Any allocation change is recorded; bytes and
identity must remain unchanged, with the metadata handle absent through that
observation. Cleanup failure makes the proposed mode fail.

The500ms failure remains unchanged. Passing qualifies this collector policy only
for the declared nonmedia workload. It does not repair failed live cases or
establish a universal I/O-latency guarantee; native collection requires a fresh
independently reviewed admission within current task authorization.

When this policy is used for native collection, keep a separate pre-release
sample. Only after terminal active coverage and bounded release should the
collector freeze final allocation and `sample.json`. The larger pre-release
allocation remains part of the observed peak. Main and short outputs are taken
from explicit result paths, including worker application subfolders.
