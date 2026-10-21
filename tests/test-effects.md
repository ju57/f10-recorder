# Verification launch effects

Preview the actual launch plan before building, creating output directories or
starting a process:

```powershell
./tests/window-capture.ps1 -Plan | ConvertTo-Json -Depth 5
./tests/window-capture.ps1 -Mode Window -Plan | ConvertTo-Json -Depth 5
./tests/worker.ps1 -Plan | ConvertTo-Json -Depth 5
./tests/worker.ps1 -LiveDesktop -Plan | ConvertTo-Json -Depth 5
```

| Route | Real UI/focus | Real pixels/audio | GPU encoding | Startup writes | NVIDIA Replay interaction |
| --- | --- | --- | --- | --- | --- |
| Window checks, default Synthetic | No | No | Yes | No | No excluded UI surfaces |
| Window checks, Window | Yes | Controlled application window and default output audio | Yes | No | Treat visible UI as a potential interaction |
| Worker checks, default Synthetic | Yes | No | Yes | Settings apply may write the existing value; preservation is checked | Excluded monitor-mode UI can stop Replay |
| Worker checks, LiveDesktop | Yes | Primary display and default output audio | Yes | Same preservation contract | Excluded monitor-mode UI can stop Replay |

Generated media does not make the worker UI suite headless. UI/live routes keep
their existing desktop ownership and task-specific authorization requirements;
the plan adds no confirmation prompt. Headless checks still use the GPU and
isolated worker shortcuts, so they are not a performance-neutral concurrent task.

`verification-launch.ps1` supplies both the command and its effect declaration.
The scripts preview and launch that same object. A contradictory command,
headless flag or effect declaration is rejected before `Start-Process`.
`verification-launch-checks.ps1` previews the real scripts and exercises these
negative cases without launching UI, GPU work or capture. Windows CI runs it.
This protects known dispatch behavior; it cannot infer every indirect effect
inside arbitrary native code. Review the mapping when changing a test entry point.
