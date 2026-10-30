using System;
using System.Collections.Generic;

namespace F10Recorder
{
    // Verification-only capture overrides; configuration has no engine dependency.
    public static class RecorderRuntime
    {
        internal static Func<string> CaptureHealthProbe = null;
        // Verification-only before/after overrides; the application never sets this.
        internal static Func<List<string>, List<string>> CaptureArgumentProbe = null;
        internal static Action<IReadOnlyList<string>> CaptureArgumentObserver = null;
        internal static Action<IReadOnlyList<string>> ExportArgumentObserver = null;
    }
}
