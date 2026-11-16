# Application migration regression

This suite exercises the maintained Avalonia application. The narrower
[profile migration checks](profile-migration.md) cover profile-format and
compatibility behavior separately. Preview supported routes and effects in
[the launch-effects index](test-effects.md) before execution.

## Full application route

Run the migrated application regression with `.\tests\migration.ps1`. It uses controlled temporary windows, actual Windows keyboard input, native-resolution screenshots, GPU-generated media and a short display/system-audio capture. It validates all available NVENC codecs by saving and fully decoding MP4s. Reports and previews are kept under ignored `artifacts/`. This suite needs an interactive Windows desktop and NVIDIA GPU; it must not be run on a machine without permission to capture its display/audio.

## Paused focus route

Use `.\tests\migration.ps1 -FocusOnly` for the paused focus regression. It opens only task-owned test windows and checks Settings restoration after switching away, explicit Settings activation, and return to previously focused Settings. It starts no recording, takes no screenshots, injects no input, and submits no settings or startup changes. Encoder availability is still probed, and the interactive desktop must be available for the test windows to activate.

## Isolated shortcut selection

If another isolated recorder owns the regression shortcuts, use `-ShortcutModifiers 3` for Ctrl+Alt instead of the default Ctrl+Alt+Shift (`7`). This changes only the test profile; an occupied shortcut still fails rather than modifying or stopping another recorder. Modifier bits are Alt=`1`, Ctrl=`2`, Shift=`4`.
