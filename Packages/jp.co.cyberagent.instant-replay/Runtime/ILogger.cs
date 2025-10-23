// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace InstantReplay
{
    public interface ILogger
    {
        void Log(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogException(Exception exception);

        private class DefaultLogger : ILogger
        {
            public static readonly DefaultLogger Instance = new();

            public void Log(string message)
            {
                Debug.Log(message);
            }

            public void LogWarning(string message)
            {
                Debug.LogWarning(message);
            }

            public void LogError(string message)
            {
                Debug.LogError(message);
            }

            public void LogException(Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        #region static members

        // ReSharper disable once ArrangeTypeMemberModifiers
        public static ILogger Logger { get; set; }

        private static ILogger EffectiveInstance => Logger ?? DefaultLogger.Instance;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void LogCore(string message)
        {
            EffectiveInstance.Log(message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void LogWarningCore(string message)
        {
            EffectiveInstance.LogWarning(message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void LogErrorCore(string message)
        {
            EffectiveInstance.LogError(message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void LogExceptionCore(Exception exception)
        {
            EffectiveInstance.LogException(exception);
        }

        #endregion
    }
}
