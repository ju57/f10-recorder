# Monitor WGC: target, clock and presentation boundaries

25 October 2026. Primary-source research for the bounded follow-up to the [capture-recovery prototype](capture-recovery-20261025.md). No capture, source, NVIDIA control or settings mutation was performed for this note. It does not select a production backend or change an accepted ADR.

## Findings in the pinned FFmpeg source

- `hmonitor` selects the supplied handle directly and takes precedence over `hwnd`. `monitor_idx` instead enumerates `EnumDisplayMonitors`; it is not a DXGI output index. The monitor path calls `CreateForMonitor`, rather than `CreateForWindow`. [FFmpeg n9.0.2, target selection](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavfilter/vsrc_gfxcapture_winrt.cpp#L783-L808), [capture-item creation](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavfilter/vsrc_gfxcapture_winrt.cpp#L366-L401).
- The filter reads each frame's `SystemRelativeTime`, uses its duration as PTS, then subtracts its first PTS. Its output time base is 1/10,000,000. The absolute first timestamp is therefore absent from emitted frames. [Frame processing](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavfilter/vsrc_gfxcapture_winrt.cpp#L1275-L1332), [time base](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavfilter/vsrc_gfxcapture_winrt.cpp#L1151-L1155).
- `max_framerate` requests `MinUpdateInterval` when available; the code warns if unavailable or rejected. It does not guarantee actual picture cadence. Session setup also requests cursor/border settings; `StartCapture` begins capture. The reviewed implementation contains no `SetFullscreenState` call. [Session setup](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavfilter/vsrc_gfxcapture_winrt.cpp#L295-L401).

These are source-code observations, not proof that a local binary delivers fresh pictures from every source. Exact executable identity and actual option/help inventory still belong in the live plan.

## Microsoft contracts

`CreateForMonitor` accepts an `HMONITOR` identifying the desired display and is documented from Windows 10 version 1903/build 18362. Resolve and retain the actual monitor identity before collection; do not assume numerical enumeration order matches a different capture API. [Microsoft: CreateForMonitor](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createformonitor).

Microsoft defines `SystemRelativeTime` as QPC time associated with compositor rendering. This is a compositor reference event, not a promise of application Present-return, physical display or perceptual A/V timing. [Microsoft: SystemRelativeTime](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe.systemrelativetime?view=winrt-26100).

The capture overview describes display/window acquisition, support checks, frame-pool lifetime and resize/device-loss recreation. It does not provide a blanket guarantee for fresh content from every traditional exclusive-fullscreen application or promise that capture preserves a hardware presentation path. Successful item creation or `IsSupported` is consequently insufficient. [Microsoft: screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture).

## Measurement implications

**Inference from the filter's rebasing:** downstream `-copyts` cannot recover an absolute timestamp already subtracted inside the filter. A normal MP4 alone cannot establish the absolute source-QPC-to-WGC phase. Preserve raw first-frame `SystemRelativeTime` in a separate observation if that claim is required; do not equate controller launch time with acquisition time or estimate the missing epoch from a convenient nearest source picture.

A bounded unmodified-engine check can still establish decoded source IDs, their order/freshness, relative media intervals and differences from independently identified source-submission intervals. A first-picture-normalized interval comparison cannot exclude a constant stale offset affecting the whole clip. This limit must remain explicit even if every timestamp increases.

**Presentation-mode hypothesis, not a documented cause:** WGC could coincide with a changed source presentation path; this review does not establish whether it causes such a change on this machine. Microsoft explains that a windowed flip source can bypass composition, and desktop changes can return it to composition or use another hardware path. Thus monitor-sized geometry, API fullscreen state or fresh captured pixels cannot replace actual mode evidence. [Microsoft: DXGI flip-model guidance](https://devblogs.microsoft.com/directx/dxgi-flip-model/).

Keep a process-scoped presentation trace before, during and after capture. If useful WGC pictures occur only after composed/Independent Flip events replace Legacy Flip, record a useful acquisition result with an exclusive-mode limitation. Do not relabel it a traditional-exclusive pass. The accepted displayed-presentation requirement in [ADR 0006](../adr/0006-match-displayed-presentation.md) also remains separate from successful monitor acquisition.
