// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Buffers;

namespace InstantReplay
{
    /// <summary>
    ///     Represents audio frame data for processing.
    /// </summary>
    internal readonly struct PcmAudioFrame : IDisposable
    {
        private readonly short[] _array; // Rented from ArrayPool
        public readonly ReadOnlyMemory<short> Data;
        public readonly double Timestamp;

        /// <summary>
        ///     The exact sample position (in the output sample rate) where this frame's data begins.
        ///     Carried as an integer so the encoder receives contiguous positions without the rounding
        ///     error a seconds-based (double) round trip would introduce, which the native side would
        ///     otherwise detect as a spurious gap and fill with silence.
        /// </summary>
        public readonly long SamplePosition;

        public PcmAudioFrame(short[] rendArray, ReadOnlyMemory<short> data, double timestamp, long samplePosition)
        {
            _array = rendArray;
            Data = data;
            Timestamp = timestamp;
            SamplePosition = samplePosition;
        }

        public void Dispose()
        {
            if (_array != null)
                ArrayPool<short>.Shared.Return(_array);
        }
    }
}
