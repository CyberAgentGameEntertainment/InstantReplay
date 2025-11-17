// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using CriWare;
using UniEnc;
using UnityEngine;
using Object = UnityEngine.Object;

namespace InstantReplay.Cri
{
    /// <summary>
    ///     Audio sample provider captures CRI audio output.
    /// </summary>
    public class CriAudioSampleProvider : IAudioSampleProvider
    {
        private const int SampleBatchSize = 512;
        private readonly object _lock = new();
        private readonly Action _updateDelegate;
        private CriAtomExOutputAnalyzer _analyzer;
        private ulong _timestampInSamples;

        /// <summary>
        /// </summary>
        /// <remarks>
        ///     <see cref="CriAudioSampleProvider" /> will attach <see cref="CriAtomExOutputAnalyzer" /> to specified DSP bus.
        ///     A DSP bus can be attached to by only one analyzer at the same time and use of multiple
        ///     <see cref="CriAtomExOutputAnalyzer" /> may cause unintended behavior.
        /// </remarks>
        /// <param name="dspBusName"></param>
        /// <param name="configuredSamplingRate">
        ///     Sampling rate you specified for CRI initialization. If null,
        ///     <see cref="CriAudioSampleProvider" /> will try to get the value automatically.
        /// </param>
        /// <exception cref="ArgumentException"></exception>
        public CriAudioSampleProvider(string dspBusName = "MasterOut", int? configuredSamplingRate = null)
        {
            int sampleRate;
            if (configuredSamplingRate is { } value)
            {
                sampleRate = value;
            }
            else
            {
                var initializer = Object.FindObjectOfType<CriWareInitializer>();
                if (initializer)
                {
                    sampleRate = initializer.atomConfig.outputSamplingRate;
                    // If the sampling rate is 0, CRI defaults to 48000.
                    // See: https://game.criware.jp/manual/unity_plugin/latest/contents/cri4u_component_initializer.html
                    if (sampleRate == 0)
                        sampleRate = 48000;
                }
                else
                {
                    try
                    {
                        sampleRate = CriAtomPlugin.GetOutputSamplingRate();
                    }
                    catch (Exception ex)
                    {
                        throw new ArgumentException(
                            "Failed to get CRI output sampling rate. Specify configuredSamplingRate manually.", ex);
                    }
                }
            }

            var config = new CriAtomExOutputAnalyzer.Config
            {
                enablePcmCapture = true,
                enablePcmCaptureCallback = true,
                numCapturedPcmSamples = SampleBatchSize
            };

            var analyzer = _analyzer = new CriAtomExOutputAnalyzer(config);
            analyzer.SetPcmCaptureCallback((left, right, numChannels, numSamples) =>
            {
                if (left.Length < numSamples) throw new ArgumentException(nameof(left));
                if (numChannels > 1 && right.Length < numSamples) throw new ArgumentException(nameof(right));

                if (numChannels == 1)
                {
                    var timestamp = (double)_timestampInSamples / sampleRate;
                    _timestampInSamples += (ulong)numSamples;
                    OnProvideAudioSamples?.Invoke(left, 1, sampleRate, timestamp);
                }
                else
                {
                    // interleave
                    var numFlattenedSamples = numSamples * 2;
                    var bufferSize = Mathf.Min(1024 / sizeof(float), numFlattenedSamples);
                    Span<float> samples = stackalloc float[bufferSize];

                    var leftSpan = left.AsSpan();
                    var rightSpan = right.AsSpan();

                    for (var cursor = 0; cursor < numSamples;)
                    {
                        var remains = numSamples - cursor;
                        var current = Mathf.Min(remains, samples.Length / 2);
                        var timestamp = (double)_timestampInSamples / sampleRate;
                        _timestampInSamples += (ulong)current;

                        for (var i = current - 1; i >= 0; i--)
                            samples[i * 2] = leftSpan[i];

                        for (var i = current - 1; i >= 0; i--)
                            samples[i * 2 + 1] = rightSpan[i];

                        leftSpan = leftSpan[current..];
                        rightSpan = rightSpan[current..];

                        OnProvideAudioSamples?.Invoke(samples[..(current * 2)], 2, sampleRate, timestamp);

                        cursor += current;
                    }
                }
            });

            analyzer.AttachDspBus(dspBusName);

            PlayerLoopEntryPoint.OnAfterUpdate += _updateDelegate = () =>
            {
                lock (_lock)
                {
                    if (_analyzer == null) return;
                    // ExecutePcmCaptureCallback() seems must be called from the main thread.
                    // If the frame rate is low (about 20 FPS or less), internal buffer overflows and audio dropouts occur.
                    _analyzer.ExecutePcmCaptureCallback();
                }
            };
        }

        public event IAudioSampleProvider.ProvideAudioSamples OnProvideAudioSamples;

        public void Dispose()
        {
            lock (_lock)
            {
                if (_analyzer == null) return;

                if (_updateDelegate != null)
                    PlayerLoopEntryPoint.OnAfterUpdate -= _updateDelegate;

                _analyzer.DetachDspBus();
                _analyzer.Dispose();
                _analyzer = null;
            }
        }
    }
}
