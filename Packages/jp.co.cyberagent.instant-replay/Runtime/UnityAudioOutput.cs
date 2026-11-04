// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Reflection;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Provides the output sample rate of the Unity audio system without querying it while the audio system is
    ///     disabled.
    /// </summary>
    /// <remarks>
    ///     When "Disable Unity Audio" is enabled in the project settings, for example in a project that uses Wwise or
    ///     CRIWARE, <see cref="AudioSettings.outputSampleRate" /> returns 0 and logs a warning on every call.
    /// </remarks>
    internal static class UnityAudioOutput
    {
        /// <summary>
        ///     Sample rate returned when the Unity audio system is disabled or does not report a valid sample rate.
        /// </summary>
        public const int FallbackSampleRate = 48000;

        private static readonly bool? IsDisabledByProjectSettings = QueryIsDisabled();

        private static int _cachedSampleRate;
        private static bool _subscribed;

        /// <summary>
        ///     Returns the output sample rate of the Unity audio system, or <see cref="FallbackSampleRate" /> if the audio
        ///     system is disabled. Call it on the main thread.
        /// </summary>
        public static int GetSampleRate()
        {
            if (IsDisabledByProjectSettings == true) return FallbackSampleRate;

            if (!_subscribed)
            {
                _subscribed = true;
                AudioSettings.OnAudioConfigurationChanged += static _ => _cachedSampleRate = 0;
            }

            if (_cachedSampleRate <= 0)
            {
                // If the disabled state could not be determined, the query below logs a warning when the audio system is
                // disabled. Caching the fallback value limits the warning to once.
                var sampleRate = AudioSettings.outputSampleRate;
                _cachedSampleRate = sampleRate > 0 ? sampleRate : FallbackSampleRate;
            }

            return _cachedSampleRate;
        }

        private static bool? QueryIsDisabled()
        {
            // AudioSettings.unityAudioDisabled is not public. If it is unavailable, GetSampleRate falls back to querying
            // the sample rate.
            try
            {
                var property = typeof(AudioSettings).GetProperty("unityAudioDisabled",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.PropertyType == typeof(bool) && property.GetMethod != null)
                    return (bool)property.GetValue(null);
            }
            catch (Exception)
            {
                // ignored
            }

            return null;
        }
    }
}
