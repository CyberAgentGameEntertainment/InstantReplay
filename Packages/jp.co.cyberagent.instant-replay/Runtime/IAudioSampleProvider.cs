// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;

namespace InstantReplay
{
    public interface IAudioSampleProvider : IDisposable
    {
        /// <summary>
        ///     Provides audio samples (interleaved by channels) to the consumer.
        /// </summary>
        public delegate void ProvideAudioSamples(ReadOnlySpan<float> samples, int channels, int sampleRate,
            double timestamp);

        event ProvideAudioSamples OnProvideAudioSamples;
    }
}
