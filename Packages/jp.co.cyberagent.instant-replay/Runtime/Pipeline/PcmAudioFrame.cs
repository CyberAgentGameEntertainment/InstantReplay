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

        public PcmAudioFrame(short[] rendArray, ReadOnlyMemory<short> data, double timestamp)
        {
            _array = rendArray;
            Data = data;
            Timestamp = timestamp;
        }

        public void Dispose()
        {
            if (_array != null)
                ArrayPool<short>.Shared.Return(_array);
        }
    }
}
