using System;

namespace F10Recorder
{
    // Platform adapters resolve the engine; configuration never stores a binary path.
    public static class RecorderRuntime
    {
        public static Func<string> MediaEngineResolver;
        internal static string ResolveExecutable()
        {
            if (MediaEngineResolver == null) throw new InvalidOperationException("The recording platform has not been initialized.");
            return MediaEngineResolver();
        }
    }
}
