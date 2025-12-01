// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Linq;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace UniEnc
{
    internal static class PlayerLoopEntryPoint
    {
        public static event Action OnAfterUpdate;

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            var system = PlayerLoop.GetCurrentPlayerLoop();

            InsertAfter<Update.ScriptRunBehaviourUpdate>(
                new PlayerLoopSystem
                {
                    type = typeof(AfterUpdate),
                    updateDelegate = () =>
                    {
                        try
                        {
                            OnAfterUpdate?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex);
                        }
                    }
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

        public static void PostAfterUpdate<T>(Action<T> action, T context)
        {
            OnAfterUpdate += PooledActionOnce<(Action<T>, T)>.Get(static (ctx, wrapper) =>
            {
                OnAfterUpdate -= wrapper;

                var (action, context) = ctx;
                action(context);

            }, (action, context)).Wrapper;
        }
    }
}
