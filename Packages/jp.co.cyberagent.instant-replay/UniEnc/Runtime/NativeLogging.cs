// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using UniEnc.Native;

namespace UniEnc
{
    /// <summary>
    ///     Severity threshold for the native (Rust) side logging.
    /// </summary>
    public enum NativeLogLevel
    {
        Off = 0,
        Error = 1,
        Warn = 2,
        Info = 3,
        Debug = 4,
        Trace = 5
    }

    /// <summary>
    ///     Controls the native side logging.
    /// </summary>
    public static class NativeLogging
    {
        /// <summary>
        ///     Discards native log records below the given level. Native records are written to the Unity player log on
        ///     Unity platforms, and to logcat on Android. Without an explicit call the default is <see cref="NativeLogLevel.Warn" />
        ///     for release builds of the native library and <see cref="NativeLogLevel.Debug" /> for debug builds.
        /// </summary>
        public static void SetLevel(NativeLogLevel level)
        {
            NativeMethods.unienc_set_log_level((UniencLogLevel)level);
        }
    }
}
