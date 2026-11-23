using System.Runtime.CompilerServices;

// Desktop and its attachment test consume the same internal worker APIs in
// release builds and in source-linked qualification packages.
[assembly: InternalsVisibleTo("F10Recorder.Desktop")]
[assembly: InternalsVisibleTo("F10Recorder.MigrationTests")]
