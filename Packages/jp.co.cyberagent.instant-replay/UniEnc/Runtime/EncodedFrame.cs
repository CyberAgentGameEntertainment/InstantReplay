using System;
using System.Buffers;

namespace UniEnc
{
    /// <summary>
    ///     Represents an encoded video or audio frame with pooled memory management.
    /// </summary>
    public readonly struct EncodedFrame : IDisposable
    {
        private readonly byte[] _rentedArray;
        private readonly int _length;

        /// <summary>
        ///     The encoded data. Only valid until Dispose() is called.
        /// </summary>
        public ReadOnlySpan<byte> Data => _rentedArray.AsSpan(0, _length);

        /// <summary>
        ///     Timestamp of the frame in seconds.
        /// </summary>
        public double Timestamp { get; }

        /// <summary>
        ///     Whether this is a key frame (for video).
        /// </summary>
        public DataKind Kind { get; }

        /// <summary>
        ///     Creates a new EncodedFrame with data copied from the source.
        /// </summary>
        internal static EncodedFrame CreateWithCopy(ReadOnlySpan<byte> sourceData, double timestamp, DataKind kind)
        {
            var rentedArray = ArrayPool<byte>.Shared.Rent(sourceData.Length);
            sourceData.CopyTo(rentedArray.AsSpan());
            return new EncodedFrame(rentedArray, sourceData.Length, timestamp, kind);
        }

        /// <summary>
        ///     Creates a new EncodedFrame with pre-rented array (internal use only).
        /// </summary>
        private EncodedFrame(byte[] rentedArray, int length, double timestamp, DataKind kind)
        {
            _rentedArray = rentedArray;
            _length = length;
            Timestamp = timestamp;
            Kind = kind;
        }

        /// <summary>
        ///     Returns the rented array to the pool. After calling this, Data becomes invalid.
        /// </summary>
        public void Dispose()
        {
            if (_rentedArray != null)
                ArrayPool<byte>.Shared.Return(_rentedArray);
        }

        public EncodedFrame WithTimestamp(double timestamp)
        {
            return new EncodedFrame(_rentedArray, _length, timestamp, Kind);
        }
    }
}
