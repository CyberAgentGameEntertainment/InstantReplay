// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading.Tasks;
using Debug = UnityEngine.Debug;

namespace InstantReplay
{
    internal class AudioTemporalAdjuster : IPipelineTransform<AudioInputData, AudioFrameData>
    {
        private const double AllowedLag = 0.1;
        
        private readonly IRecordingTimeProvider _recordingTimeProvider;
        
        private bool _disposed;
        private double? _audioTimeDifference;
        private long? _currentSamplePosition;
        
        private readonly double _sampleRateInOption;
        private readonly double _numChannelsInOption;

        public AudioTemporalAdjuster(IRecordingTimeProvider recordingTimeProvider, double sampleRateInOption, double numChannelsInOption)
        {
            _recordingTimeProvider = recordingTimeProvider;
            _sampleRateInOption = sampleRateInOption;
            _numChannelsInOption = numChannelsInOption;
        }

        public bool Transform(AudioInputData input, out AudioFrameData output)
        {
            var realTime = _recordingTimeProvider.Now;

            var samples = input.UnsafeSamples;
            var timestamp = input.Timestamp;
            var channels = input.Channels;
            var sampleRate = input.SampleRate;
            
            output = default;

            if (_disposed || _recordingTimeProvider.IsPaused || samples.Length == 0)
                return false;

            // adjust timestamp
            if (!_audioTimeDifference.HasValue)
            {
                _audioTimeDifference = timestamp - realTime;
                timestamp = realTime;
            }
            else
            {
                var expectedTime = realTime + _audioTimeDifference.Value;
                var diff = timestamp - expectedTime;
                if (Math.Abs(diff) >= AllowedLag)
                {
                    Debug.LogWarning(
                        "Audio timestamp adjusted. The timestamp IAudioSampleProvider provided may not be realtime.");
                    _audioTimeDifference = timestamp - realTime;
                    timestamp = realTime;
                }
                else
                {
                    timestamp -= _audioTimeDifference.Value;
                }
            }

            timestamp -= _recordingTimeProvider.TotalPausedDuration;

            var numSamples = samples.Length / channels;

            var samplePosition = (long)Math.Round(timestamp * _sampleRateInOption);
            var currentSamplePosition = _currentSamplePosition ??= samplePosition;
            var lag = samplePosition - currentSamplePosition;

            var numScaledSamples = _sampleRateInOption == sampleRate
                ? numSamples
                : (long)Math.Round(numSamples * ((double)_sampleRateInOption / sampleRate));
            long blankOrSkip;
            if (Math.Abs(lag) > AllowedLag * _sampleRateInOption)
            {
                // if there is too much lag, skip input or insert blank
                blankOrSkip = lag;
            }
            else
            {
                // scale to position
                blankOrSkip = 0;
                numScaledSamples += lag;
            }

            var writeLength = (int)((numScaledSamples + blankOrSkip) * channels);

            if (writeLength <= 0)
                return false;

            var writeBufferArray = ArrayPool<short>.Shared.Rent(writeLength);
            var writeBuffer = writeBufferArray.AsSpan(0, writeLength);

            if (blankOrSkip > 0)
                writeBuffer[..(int)blankOrSkip].Clear();

            var skip = (int)Math.Max(0, -blankOrSkip);

            for (var i = skip; i < numScaledSamples; i++)
            for (var j = 0; j < _numChannelsInOption; j++)
            {
                // NOTE: should we interpolate samples?
                var pos = numSamples == numScaledSamples
                    ? i
                    : (int)Math.Floor(i * ((double)numSamples / numScaledSamples));
                var sample = samples[pos * channels + j % channels];
                var scaledSample = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
                writeBuffer[i * (int)_numChannelsInOption + j] = scaledSample;
            }

            output = new AudioFrameData(writeBufferArray, writeBufferArray.AsMemory(0, writeLength), timestamp);

            _currentSamplePosition = currentSamplePosition + numScaledSamples + blankOrSkip;
            return true;
        }

        public void Dispose()
        {
        }
    }
}
