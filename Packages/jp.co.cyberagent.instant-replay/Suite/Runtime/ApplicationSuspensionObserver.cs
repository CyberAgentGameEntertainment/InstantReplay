// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;

namespace InstantReplay.Suite
{
    /// <summary>
    ///     Reports when the application is suspended and resumed, so that a recorder that is not a component can observe
    ///     <c>OnApplicationPause</c>.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class ApplicationSuspensionObserver : MonoBehaviour
    {
        private Action<bool> _onChanged;

        private void OnApplicationPause(bool pauseStatus)
        {
            _onChanged?.Invoke(pauseStatus);
        }

        public static ApplicationSuspensionObserver Create(Action<bool> onChanged)
        {
            var gameObject = new GameObject(nameof(ApplicationSuspensionObserver))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            DontDestroyOnLoad(gameObject);
            var observer = gameObject.AddComponent<ApplicationSuspensionObserver>();
            observer._onChanged = onChanged;
            return observer;
        }

        public void DestroySelf()
        {
            _onChanged = null;
            Destroy(gameObject);
        }
    }
}
