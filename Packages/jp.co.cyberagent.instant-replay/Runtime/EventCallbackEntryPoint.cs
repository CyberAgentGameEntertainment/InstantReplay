// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections;
using UnityEngine;

namespace InstantReplay
{
    internal class EventCallbackEntryPoint : MonoBehaviour
    {
        private static WaitForEndOfFrame _waitForEndOfFrame;
        private bool _endOfFrameCoroutineRunning;

        #region Event Functions

        private void LateUpdate()
        {
            if (EndOfFrameInner != null && !_endOfFrameCoroutineRunning)
                StartCoroutine(EndOfFrameCoroutine());
        }

        #endregion

        private IEnumerator EndOfFrameCoroutine()
        {
            _endOfFrameCoroutineRunning = true;
            try
            {
                while (EndOfFrameInner != null)
                {
                    yield return _waitForEndOfFrame ??= new WaitForEndOfFrame();
                    try
                    {
                        EndOfFrameInner?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        ILogger.LogExceptionCore(ex);
                    }
                }
            }
            finally
            {
                _endOfFrameCoroutineRunning = false;
            }
        }

        #region static members

        private static EventCallbackEntryPoint _instance;

        public static event Action EndOfFrame
        {
            add
            {
                EnsureCreated();
                EndOfFrameInner += value;
            }
            remove => EndOfFrameInner -= value;
        }

        private static event Action EndOfFrameInner;

        private static void EnsureCreated()
        {
            if (_instance) return;
            var obj = new GameObject();
            DontDestroyOnLoad(obj);
            obj.hideFlags |= HideFlags.HideAndDontSave;
            _instance = obj.AddComponent<EventCallbackEntryPoint>();
        }

        #endregion
    }
}
