# Storage controls and editable cache budget

Issue #13 criterion1 is implemented through the current Settings editor and worker.
Separate Recordings, Screenshots and Replay cache fields have inset Phosphor
folder buttons, accessible names and field-specific errors. Budget is editable
in decimal GB; 10 GB is 10,000,000,000 bytes. Existing arbitrary-byte budgets
round-trip without rounding. Invalid, fractional-byte, overflowing and nominally
impossible budgets reject without saving. Reset edits restores applied values;
picker cancellation leaves the draft intact. Apply uses the existing correlated
worker acknowledgement and preserves drafts on rejection.

New profiles retain Videos/F10 Recorder and Pictures/F10 Recorder defaults.
Existing paths, shortcuts, explicit false/zero values, opaque preferences, pause
and media are preserved. The empty internal cache preference keeps the historical
`<profile>/buffer` location. An explicit folder must exist; the worker binds its
canonical directory before saving and uses a deterministic per-profile
`F10-cache-<identity>` child. Profiles do not share or clear each other's cache.

Location changes never move/delete existing media or retained cache. Worker-owned
opaque history keeps old roots charged to the aggregate budget after reopening.
The historical default root is always included, even when an older application
retained the custom folder as an opaque field without location history.
New buffers use the selected location; old history is not adopted as new replay
footage. The cache directory is anchored for the engine lifetime. Existing Apply
busy/pause/ownership restrictions remain; this does not implement manual recording
or the later deferred-Apply workflow. Protocol6 rejects older editors/workers
that cannot preserve the new setting. No Drive space dashboard is added. Usage
requires a timestamped worker measurement, otherwise it displays unavailable.

## Integration checks

After building MigrationTests with the pinned SDK and locked dependencies:

```powershell
dotnet run --project projects/F10Recorder.MigrationTests -c Release --no-build -- artifacts/storage-ui-unique --storage-controls-ui
```

The actual SettingsPanel/WorkerRecorder use a controlled transport with real
RecordingProfile persistence. Checks cover three picker handlers at the OS
dialog boundary, cancel/invalid/reset, exact budgets, rejected/successful
Apply/reopen, opaque/pause/media preservation, profile isolation and retained-root
accounting. CI runs this route. A software-rendered bitmap uses a never-shown
ordinary Avalonia Window to instantiate native control templates; no capture
exclusion is set. The native folder dialog is replaced only at its selection
boundary; manual picker/DPI/accessibility acceptance is not inferred.

For the separate actual-worker route, supply the built WorkerChecks executable
and an existing task-private approved engine root:

```powershell
MigrationTests.exe artifacts/storage-native-unique --storage-controls-ui <WorkerChecks.exe> <retained-engine-root>
```

Effects to review before running: a fresh profile, one paused/activation-held
worker, current-user pipe, isolated hidden Ctrl+Alt+Shift+F4-F6 shortcuts,
status/Apply/Quit and verified owned cleanup. Engine verification is read-only;
no engine/probe, recording, GPU, visible/excluded UI, actual startup write,
installation or provisioning occurs. The worker has a fixture startup callback;
UI codec choices are supplied, not GPU qualification. Media/cache fixtures are
small preservation sentinels. Never use an active profile or installed recorder.
Both routes require fresh evidence and retain failures.

These checks establish Storage controls integration, not output calibration,
low-space/Resume, full-shell migration, native media or release acceptance.
Issue13 remains open for its other criteria; all twelve gates stay unresolved.
