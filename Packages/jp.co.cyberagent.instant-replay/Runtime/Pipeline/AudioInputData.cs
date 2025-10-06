// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;

namespace InstantReplay
{
    internal readonly unsafe struct AudioInputData
    {
        internal ReadOnlySpan<float> UnsafeSamples => new(Samples, NumSamples);
        private float* Samples { get; }
        private int NumSamples { get; }
        public int Channels { get; }
        public int SampleRate { get; }
        public double Timestamp { get; }

        public AudioInputData(float* samples, int numSamples, int channels, int sampleRate, double timestamp)
        {
            Samples = samples;
            NumSamples = numSamples;
            Channels = channels;
            SampleRate = sampleRate;
            Timestamp = timestamp;
        }
    }
}
