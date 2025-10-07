using System;
using System.Threading;
using CriWare;
using UnityEngine;

namespace InstantReplay.Cri
{
    /// <summary>
    ///     Audio sample provider captures CRI audio output.
    /// </summary>
    public class CriAudioSampleProvider : IAudioSampleProvider
    {
        private readonly Action _updateDelegate;
        private CriAtomExOutputAnalyzer _analyzer;
        private ulong _timestampInSamples;

        /// <remarks>
        ///     <see cref="CriAudioSampleProvider" /> will attach <see cref="CriAtomExOutputAnalyzer" /> to specified DSP bus.
        ///     A DSP bus can be attached to by only one analyzer at the same time and use of multiple
        ///     <see cref="CriAtomExOutputAnalyzer" /> may cause unintended behavior.
        /// </remarks>
        public CriAudioSampleProvider(string dspBusName = "MasterOut")
        {
            var sampleRate = CriAtomPlugin.GetOutputSamplingRate();

            if (sampleRate == 0)
                sampleRate = 48000;

            var config = new CriAtomExOutputAnalyzer.Config
            {
                enablePcmCapture = true,
                enablePcmCaptureCallback = true,
                numCapturedPcmSamples = 512
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

            PlayerLoopEntryPoint.OnAfterUpdate += _updateDelegate = () => { analyzer.ExecutePcmCaptureCallback(); };
        }

        public event IAudioSampleProvider.ProvideAudioSamples OnProvideAudioSamples;

        public void Dispose()
        {
            var analyzer = Interlocked.Exchange(ref _analyzer, null);
            if (analyzer == null) return;

            PlayerLoopEntryPoint.OnAfterUpdate -= _updateDelegate;
            analyzer.DetachDspBus();
            analyzer.Dispose();
        }
    }
}
