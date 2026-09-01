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
        ///     Discards native log records below the given level. Native records go to the Unity log, so they reach the
        ///     Editor console and the player log — which on Android is logcat, under Unity's own tag. Records emitted
        ///     before Unity has loaded the plugin fall back to logcat under an <c>unienc</c> tag on Android, and to
        ///     stdout/stderr elsewhere. Without an explicit call the default is <see cref="NativeLogLevel.Warn" /> for
        ///     release builds of the native library and <see cref="NativeLogLevel.Debug" /> for debug builds.
        /// </summary>
        public static void SetLevel(NativeLogLevel level)
        {
            NativeMethods.unienc_set_log_level((UniencLogLevel)level);
        }
    }
}
