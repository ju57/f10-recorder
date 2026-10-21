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

**Accepted replay save**:
A replay request the recorder has accepted responsibility for completing, ending in either a saved video or a reported failure.
_Avoid_: Save notification
