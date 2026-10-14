using System;
using System.Collections.Generic;

namespace F10Recorder
{
    // Platform adapters resolve the engine; configuration never stores a binary path.
    public static class RecorderRuntime
    {
        public static Func<string> MediaEngineResolver;
        internal static Func<string> CaptureHealthProbe = null;
        // Verification-only before/after overrides; the application never sets this.
        internal static Func<List<string>, List<string>> CaptureArgumentProbe = null;
        internal static string ResolveExecutable()
        {
            if (MediaEngineResolver == null) throw new InvalidOperationException("The recording platform has not been initialized.");
            return MediaEngineResolver();
        }
    }
}
