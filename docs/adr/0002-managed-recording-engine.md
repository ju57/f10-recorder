# Use an approved managed recording engine

F10 Recorder will automatically use only an approved, verified managed recording engine, stored separately from recording profiles. Automatic PATH and conventional-location FFmpeg reuse is removed because compatibility probes execute code before establishing trust, and profile-controlled metadata cannot authenticate its own executable. Preserve automatic dependency setup so users do not need to install FFmpeg themselves; support for explicitly approved external engines is outside this remediation scope.

Setup downloads and verifies the approved engine when no verified managed copy exists, and enables sign-in startup only after dependency setup succeeds. A verified managed copy supports offline reuse. A failed initial download produces a clear error and retry instructions.

For the accepted Windows beta, Q14/Q61 refine first installation: bundle the runtime and approved engine in the per-user installer so installation needs no dependency download. Verify bundled inputs against the approved trust root before activation; bundling does not permit package/profile metadata to authorize an arbitrary executable. New-user capture and startup activation still wait for successful Finish; engine verification alone does not complete setup. The earlier download path describes prototype provisioning and explicit repair, not permission for worker restarts or offline beta installation to fetch dependencies silently. Exact package/source provenance and installer qualification remain mandatory release gates.

If the engine later disappears or fails verification, stop recording, report the problem, and offer explicit repair that provisions a verified replacement. Worker restarts must not repeatedly download dependencies or fall back to an unapproved executable.

Upgrades preserve recording preferences, clips and screenshots, ignore old profile-controlled engine manifests and runtime locations, and provision the managed engine separately. Leave old dependency files untouched during this remediation rather than trusting or deleting files referenced by an old profile.
