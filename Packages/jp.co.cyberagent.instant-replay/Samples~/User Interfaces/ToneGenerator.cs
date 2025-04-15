// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;
using UnityEngine.UI;

namespace InstantReplay.Examples
{
    [ExecuteAlways]
    public class ToneGenerator : MonoBehaviour
    {
        // For video-audio sync test

        // C, D, E, F, G, A, B
        private static readonly byte[] _notes = { 0, 2, 4, 5, 7, 9, 11 };

        #region Serialized Fields

        [SerializeField] private Text _indicator;

        #endregion

        private bool _isActive;
        private bool _isPlaying;

        private double _phase;
        private int _sampleRate;

        #region Event Functions

        private void Update()
        {
            _sampleRate = AudioSettings.outputSampleRate;
            _isPlaying = Application.isPlaying;
            if (!_indicator || !_isPlaying) return;
            var time = AudioSettings.dspTime;
            _indicator.text = $"Note: {(int)Math.Floor(time % _notes.Length)}";
        }

        private void OnEnable()
        {
            _sampleRate = AudioSettings.outputSampleRate;
            _isActive = true;
        }

        private void OnDisable()
        {
            _isActive = false;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            var time = AudioSettings.dspTime;

            // note to freq
            var freq = 440f * (float)Math.Pow(1.059463094f, _notes[(int)Math.Floor(time % _notes.Length)]);

            var volume = _isActive && _isPlaying ? 0.05f : 0.0f;

            for (var i = 0; i < data.Length; i += channels)
            {
                var sample = volume * Math.Sin(_phase * 2.0 * Mathf.PI);
                _phase += freq / _sampleRate;
                if (_phase > 1.0f)
                    _phase -= 1.0f;

                for (var j = 0; j < channels; j++)
                    data[i + j] = (float)sample;
            }
        }

        #endregion
    }
}
