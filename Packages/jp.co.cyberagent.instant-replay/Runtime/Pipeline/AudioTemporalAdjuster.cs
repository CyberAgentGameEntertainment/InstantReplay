// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Buffers;

namespace InstantReplay
{
    internal class AudioTemporalAdjuster : IPipelineTransform<InputAudioFrame, PcmAudioFrame>
    {
        private const double DefaultAllowedLag = 0.1;
        private readonly double _allowedLag;
        private readonly double _numChannelsInOption;

        private readonly IRecordingTimeProvider _recordingTimeProvider;

        private readonly double _sampleRateInOption;
        private double? _audioTimeDifference;
        private long? _currentSamplePosition;

        private bool _disposed;

        public AudioTemporalAdjuster(IRecordingTimeProvider recordingTimeProvider, double sampleRateInOption,
            double numChannelsInOption, double? allowedLag = null)
        {
            _recordingTimeProvider = recordingTimeProvider;
            _sampleRateInOption = sampleRateInOption;
            _numChannelsInOption = numChannelsInOption;
            if (allowedLag is < 0)
                throw new ArgumentOutOfRangeException(nameof(allowedLag), "allowedLag must be non-negative.");
            _allowedLag = allowedLag ?? DefaultAllowedLag;
        }

        public bool WillAcceptWhenNextWont => true; // we need to keep advancing position even if next won't accept

        public bool Transform(InputAudioFrame input, out PcmAudioFrame output, bool willAcceptedByNextInput)
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
                if (Math.Abs(diff) >= _allowedLag)
                {
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

            // input sample position (in output scale)
            var samplePosition = (long)Math.Round(timestamp * _sampleRateInOption);

            // expected sample position
            var currentSamplePosition = _currentSamplePosition ??= samplePosition;

            var lag = samplePosition - currentSamplePosition;

            var numScaledSamples = _sampleRateInOption == sampleRate
                ? numSamples
                : (long)Math.Round(numSamples * (_sampleRateInOption / sampleRate));

            long blankOrSkip;
            if (Math.Abs(lag) > _allowedLag * _sampleRateInOption)
            {
                // if there is too much lag, skip input or insert blank
                blankOrSkip = lag;
                ILogger.LogWarningCore(
                    "Audio timestamp adjusted. The timestamp IAudioSampleProvider provided may not be realtime.");
            }
            else
            {
                // if there is slight lag, scale samples to fit
                blankOrSkip = 0;
                numScaledSamples += lag;
            }

            var writeLength = (int)((numScaledSamples + blankOrSkip) * _numChannelsInOption);

            if (writeLength <= 0)
                return false;

            _currentSamplePosition = currentSamplePosition + numScaledSamples + blankOrSkip;
            if (!willAcceptedByNextInput)
            {
                // advance _currentSamplePosition even if we cannot output
                ILogger.LogWarningCore("Dropped audio frame due to full queue.");
                output = default;
                return false;
            }

            var writeBufferArray = ArrayPool<short>.Shared.Rent(writeLength);
            var writeBuffer = writeBufferArray.AsSpan(0, writeLength);

            var blank = (int)Math.Max(0, blankOrSkip);

            if (blank > 0)
                writeBuffer[..blank].Clear();

            for (var writePos = blank; writePos < numScaledSamples + blankOrSkip; writePos++)
            {
                var inputPos = writePos - blankOrSkip;
                var inputPosInputScaled = numSamples == numScaledSamples
                    ? inputPos
                    : (int)Math.Floor(inputPos * ((double)numSamples / numScaledSamples));

                for (var j = 0; j < _numChannelsInOption; j++)
                {
                    // NOTE: should we interpolate samples?
                    var sample = samples[checked((int)(inputPosInputScaled * channels + j % channels))];
                    var scaledSample = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
                    writeBuffer[writePos * (int)_numChannelsInOption + j] = scaledSample;
                }
            }

            output = new PcmAudioFrame(writeBufferArray, writeBufferArray.AsMemory(0, writeLength), timestamp);

            return true;
        }

        public void Dispose()
        {
        }
    }
}
