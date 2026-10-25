# F10 Recorder

F10 Recorder keeps recent local footage available for replay saves.

## Language

**Recording profile**:
A set of recording preferences and associated local recorder state, including its rolling replay cache. A custom profile has the same meaning as the default profile.
_Avoid_: FFmpeg installation

**Profile ownership**:
The exclusive right of one recorder worker to manage a recording profile and its rolling replay cache.
_Avoid_: UI ownership

**Managed recording engine**:
The approved FFmpeg dependency maintained by F10 Recorder, separate from recording profiles and external FFmpeg installations.
_Avoid_: Discovered FFmpeg, system FFmpeg

**Capture session**:
A continuous recording period whose footage shares one timeline. Changing applications during monitor capture remains part of the same capture session.
_Avoid_: Application session

**Displayed presentation**:
The visible shape and framing of a capture source on the selected display, including stretching or aspect-ratio borders.
_Avoid_: Raw render proportions

**Accepted replay save**:
A replay request the recorder has accepted responsibility for completing, ending in either a saved video or a reported failure.
_Avoid_: Save notification

**Full replay**:
A replay save containing the available rolling history up to its request time, limited by the configured replay duration. Successive files may overlap.
_Avoid_: Buffer dump

**New-footage replay**:
A replay save containing retained footage after the preceding successful replay's end within the same capture session. With no preceding successful replay in that session, it contains the available rolling history.
_Avoid_: Incremental recording

**Replay checkpoint**:
The end of the most recent successfully saved replay in a capture session, used to distinguish footage already saved from new footage.
_Avoid_: Last button press

**Manual recording**:
A recording session explicitly started and stopped by the user, whose requested footage is independent of the replay retention duration.
_Avoid_: Long replay
