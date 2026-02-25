// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

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

            var system = PlayerLoop.GetCurrentPlayerLoop();
            InsertAfter<Update.ScriptRunBehaviourUpdate>(
                new PlayerLoopSystem
                {
                    type = typeof(AfterUpdate),
                    updateDelegate = RuntimeWrapper.Tick
                },
                ref system);

            PlayerLoop.SetPlayerLoop(system);
        }

        private static bool InsertAfter<T>(in PlayerLoopSystem newSystem, ref PlayerLoopSystem target)
            where T : struct
        {
            var subSystems = target.subSystemList;
            if (subSystems == null) return false;

            for (var i = 0; i < subSystems.Length; i++)
            {
                if (subSystems[i].type != typeof(T)) continue;

                var list = subSystems.ToList();
                list.Insert(i + 1, newSystem);
                target.subSystemList = list.ToArray();
                return true;
            }

            for (var i = 0; i < subSystems.Length; i++)
                if (InsertAfter<T>(newSystem, ref subSystems[i]))
                    return true;

            return false;
        }

        private struct AfterUpdate
        {
        }
    }
}
