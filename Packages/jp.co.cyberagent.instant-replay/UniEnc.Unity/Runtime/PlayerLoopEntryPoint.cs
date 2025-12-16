// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System.Threading;
using UnityEngine;

namespace UniEnc.Unity
{
    internal static class PlayerLoopEntryPoint
    {
        private static Thread _mainThread;
        public static bool IsMainThread => Thread.CurrentThread == _mainThread;
        public static SynchronizationContext MainThreadContext { get; private set; }

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            _mainThread = Thread.CurrentThread;
            MainThreadContext = SynchronizationContext.Current;
        }
    }
}
