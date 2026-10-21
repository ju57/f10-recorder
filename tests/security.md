# Focused security checks

Use the pinned SDK and an approved FFmpeg package archive. These checks preserve
the installed app, startup entry, preferences and existing media. Output belongs
in isolated ignored `artifacts/` directories.

```powershell
./tests/dependencies.ps1 -Package <pinned-archive.zip>
./tests/security.ps1 -Package <pinned-archive.zip>
```

Both suites build the modern worker checks. Neither displays UI, records the desktop/system audio, injects
input, changes startup or installs a dependency into the user's active recorder.
The package is verified and unpacked only inside the test directory. The pipe
tests use marker bytes; screenshot checks generate a small synthetic image.

| Boundary | Focused evidence |
| --- | --- |
| Engine trust | Reject PATH/profile/direct-launch candidates before execution; reject forged metadata and same-size/timestamp changes; verify offline reuse, explicit repair and a read-only launch lease |
| Audio admission | Expected child accepted; wrong PID rejected before bytes; timeout/cancellation cleanup |
| Profile ownership | Filesystem aliases share a lock; distinct profiles coexist; a blocked worker preserves cache; process termination releases ownership; retargeting an alias does not redirect an active worker's cache operations |
| Storage | Other-account write grants and unrestricted null DACLs are rejected |
| Settings state | Pending apply, lost reply, rejection, stale reply and worker replacement preserve authoritative capture-mode acknowledgement |
| Screenshot publication | Invalidated source rejects partial output; valid generated image saves; existing files survive |

Run both suites' account-only pipe checks under an ordinary Windows user token. A restricted
sandbox token may be denied even when it has the same account SID; record that
failure rather than weakening the production ACL to accommodate the sandbox.

## Coordinated acceptance

On 21 October 2026 the authorized native UI and live audio checks below passed.
NVIDIA Instant Replay was verified enabled, stopped before showing excluded UI,
and restored afterward; all reported overlay settings matched the initial state.
No second Windows account was available. The user accepted skipping that fixture,
so genuine different-account behaviour remains unverified, without an estimated
success percentage. The procedures remain available for later execution.

Build `F10Recorder.MigrationTests` and run its `--security-ui` mode with an
isolated data directory and `--engine-root <verified-test-managed-root>` to
inspect native window affinity during pending/rejected/successful transitions.
It remains paused and uses a fake startup writer, with no capture or input
injection. It displays task-owned capture-excluded UI, which may turn off NVIDIA
Instant Replay; coordinate and preserve replay state before running it.

Genuine cross-account pipe denial and protected ownership storage need a client
running under a different Windows security token. Same-account process tests or
ACL inspection do not establish that result. Prepare and coordinate the exact
fixture/account steps before executing them. The worker-check binary provides
`--security-account-server <new-empty-directory>` and
`--security-account-client <challenge.json>` for the coordinated test. Run the
server as the recorder user and the client as an existing different Windows
account within three minutes. The fixture refuses a same-account client and
never creates accounts or requests credentials.

Live WASAPI/FFmpeg integration requires separate system-audio authorization;
synthetic worker recording bypasses that path. The explicit
`--security-live-audio <new-directory> <verified-test-managed-root>` mode captures
about eight seconds of default-output system audio with generated video and
decodes both saved tracks. It captures no desktop pixels, changes no devices or
startup settings and displays no UI.

The separate opt-in screenshot continuity check captures only task-owned white
and black windows, with no desktop pixels, audio, input injection, startup changes
or capture-excluded UI:

```powershell
./bin/security-checks/F10Recorder.WorkerChecks.exe --screenshot-binding <new-empty-directory> <verified-test-managed-root>
```

Build WorkerChecks first. This test copies its apphost to a uniquely named fixture
executable in the build output directory and removes that task-created copy on
exit. It deliberately withholds WinEvent dispatch, cycles native windows for up
to four minutes, and requires exact numeric HWND reuse with unchanged process
identity. Slot reuse alone or early destroy-event delivery is inconclusive. It
checks normal/rebound screenshot pixels, rejection with no replacement/partial
PNG, independent tracker markers and cleanup. The evidence directory is private.

The reproduced pre-patch case published replacement pixels while tracking still
reported the original generation. The lifetime-marker fix rejects that case.
This establishes a conditional continuity fence, not typical callback latency,
attack frequency or resistance to a malicious owner copying its window property.
UIPI can prevent stamping an elevated window; that screenshot fails with a reason
while ordinary recording retains its existing policy. No elevated-window trial
is implied by the owned-window check.

Directory leases use directory-listing access, with write sharing retained for
normal child-file updates and delete sharing withheld for path stability. See
[CreateFile sharing semantics](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)
and [directory access masks](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-smb2/0a5934b1-80f1-4da0-b1bf-5e021c309b71).
