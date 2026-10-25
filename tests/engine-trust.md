# Managed recording engine feedback checks

Preview `./tests/engine-trust.ps1 -Plan`, then run with `-Package <approved.zip>`
and optionally `-Dotnet <pinned-sdk-path>`. The archive is authenticated before
use; repair acquisition is supplied by the verification executable from that
local archive or a deliberately rejected checksum fixture. Production download,
hash, lease and install rules remain in use. No network acquisition occurs.
The default build performs locked NuGet restores, which may contact package and
vulnerability-audit services. `-NoBuild` reuses the existing binaries; the plan
reports restore effects separately from the offline verification process.

This route starts a real isolated worker and uses the production worker client,
capability/repair commands and Settings controls. It renders section copy/status to PNGs
without creating a visible Window or requesting capture exclusion. It registers
hidden Ctrl+Alt+Shift+F1–F3 shortcuts, initializes GPU encoders with one generated
256×256 frame each, and saves/fully decodes one short synthetic 640×360 replay
with generated tone. It captures no desktop pixels, microphone or system audio,
injects no input, changes no NVIDIA setting, and uses no installed recorder.
An ordinary Windows desktop token and free fixture shortcuts are needed.

Evidence stays in a unique ignored `artifacts/engine-trust-*` directory:
authenticated package/capability receipts, missing/tampered/repair-failure
feedback, offscreen Storage-section copy/status PNGs, worker logs, a decoded synthetic replay,
preservation assertions and owned-process cleanup. Rejected attempts remain.
The PNGs show section copy/status rendering; styled fields and repair/recheck
button visuals are not rendered by this unattached fixture. Control-state
assertions verify behavior independently. This is not a native Settings visual
review. Checks include repeated restart without provisioning, verified repair, persisted
pause, preferences/media/startup preservation, stale/replacement worker state and
protocol-version rejection. Existing dependency/security/settings suites provide
the lower-level hash, archive, lease, channel and preflight regressions.

The receipt qualifies only the generated-frame initialization it describes.
It does not qualify arbitrary resolutions/rates, live capture, fullscreen,
NVIDIA coexistence, perceptual A/V sync, performance, native window accessibility
or a shipping offline installer. Offscreen rendering does not establish focus,
DPI or screen-reader acceptance. All beta release gates remain separate.

## No-GPU worker capability response checks

After the usual Release build of WorkerChecks, run:

```powershell
dotnet run --project projects/F10Recorder.WorkerChecks/F10Recorder.WorkerChecks.csproj -c Release --no-build -- --engine-capabilities artifacts/engine-capabilities-local
```

This early route uses the production worker-client transport seam with controlled
responses. It verifies an empty codec result, rejected missing capabilities,
rejected delayed capabilities during Apply without a context change, and a
rejected error-bearing reply. It writes `verification.json` and exits before
recorder initialization: no Window, RecorderHost, named pipe, engine, GPU,
capture, startup write or installation is involved. It does not establish byte
authentication, actual hardware initialization or native pipe/UI integration;
those remain covered by the separate trust/dependency suites and release gates.

Windows CI also repeats this route with a separate output directory and
`--sensitivity-control`. That intentionally supplies a complete receipt in the
missing-capabilities fixture; exactly that rejection assertion must fail with
exit code 1. This proves the assertion can go red when the fixture changes, not
that production code was mutated. CI checks the failure identity and keeps the
normal and control results in separate output directories.
