// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using UnityEngine;

namespace UniEnc.Unity
{
    /// <summary>
    ///     Applies the level baked into the build, before any scene has had a chance to start encoding.
    ///     Unlike <see cref="NativeLoggingPlayerConnection" /> this is not limited to development players:
    ///     baking is how a release build gets a level at all.
    /// </summary>
    internal static class NativeLogLevelBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void Apply()
        {
            // Absent unless the preferences page asked for a level to be baked in. Its absence has to stay
            // free: calling into the plugin here would load it in every project that never configured one.
            var asset = Resources.Load<NativeLogLevelAsset>(NativeLogLevelAsset.ResourcePath);
            if (asset == null) return;

            NativeLogging.SetLevel(asset.Level);
        }
    }
}
