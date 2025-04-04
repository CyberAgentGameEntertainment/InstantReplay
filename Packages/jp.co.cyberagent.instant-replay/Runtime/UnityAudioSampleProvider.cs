// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace InstantReplay
{
    /// <summary>
    ///     An audio sample provider that captures audio from the Unity audio system.
    /// </summary>
    public class UnityAudioSampleProvider : IAudioSampleProvider
    {
        // NOTE: keep it public to make UnityAudioSampleProvider(AudioListener) available for users

        private UnityAudioSampleProviderReceiver _receiver;
        private readonly SynchronizationContext _synchronization;

        public UnityAudioSampleProvider() : this(SelectAudioListener())
        {
        }

        public UnityAudioSampleProvider(AudioListener listener)
        {
            var receiver = _receiver = listener.gameObject.AddComponent<UnityAudioSampleProviderReceiver>();
            receiver.OnProvideAudioSamples += OnListenerProvideAudioSamples;
            _synchronization = SynchronizationContext.Current;
        }

        public event IAudioSampleProvider.ProvideAudioSamples OnProvideAudioSamples;

        public void Dispose()
        {
            if (_receiver == null) return;
            _receiver.OnProvideAudioSamples -= OnListenerProvideAudioSamples;
            var receiver = _receiver;
            _receiver = null;
            _synchronization.Post(_ => { Object.Destroy(receiver); }, null);
        }

        private static AudioListener SelectAudioListener()
        {
            var listeners =
                Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (listeners.Length == 0)
                throw new InvalidOperationException("No active AudioListener found in the scene.");

            var listener = listeners[0];

            if (listeners.Length != 1)
                Debug.LogWarning(
                    $"Multiple active AudioListeners found in the scene. Using the first one: {listener.gameObject.name}",
                    listener);

            return listener;
        }

        private void OnListenerProvideAudioSamples(ReadOnlySpan<float> samples, int channels, int sampleRate,
            double timestamp)
        {
            OnProvideAudioSamples?.Invoke(samples, channels, sampleRate, timestamp);
        }
    }
}
