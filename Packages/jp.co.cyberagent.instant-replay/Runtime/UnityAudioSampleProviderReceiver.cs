// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;

namespace InstantReplay
{
    internal class UnityAudioSampleProviderReceiver : MonoBehaviour
    {
        private int _sampleRate;

        #region Event Functions

        private void Update()
        {
            _sampleRate = UnityAudioOutput.GetSampleRate();
        }

        private void OnEnable()
        {
            _sampleRate = UnityAudioOutput.GetSampleRate();
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            OnProvideAudioSamples?.Invoke(new ReadOnlySpan<float>(data), channels, _sampleRate, AudioSettings.dspTime);
        }

        #endregion

        public event IAudioSampleProvider.ProvideAudioSamples OnProvideAudioSamples;
    }
}
