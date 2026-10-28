// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    /// <summary>
    ///     Circular buffer for encoded frames with memory bounds.
    /// </summary>
    /// <remarks>
    ///     The payloads of the samples are copied into an <see cref="EncodedFrameRingStorage" /> whose capacity is the
    ///     memory budget, so the memory held for the footage never exceeds the budget. When the storage is full, the
    ///     oldest samples are evicted in the order they were added, regardless of the stream they belong to. The codec
    ///     configuration records are small and are kept outside the storage for the lifetime of the buffer.
    /// </remarks>
    internal class BoundedEncodedFrameBuffer : IEncodedFrameBuffer
    {
        private readonly List<EncodedFrame> _audioMetadata = new();
        private readonly object _lock = new();
        private readonly long _maxMemoryBytes;
        private readonly Action<Exception> _onError;

        // Samples of both streams in the order their payloads were written to the storage.
        private readonly Queue<(StoredEncodedFrame Frame, bool IsVideo)> _samples = new();
        private readonly List<EncodedFrame> _videoMetadata = new();
        private bool _disposed;

        // Null once the frames have been taken out for an export or a capture.
        private EncodedFrameRingStorage _storage;
        private double? _videoQueueLatestTimestamp;

        public BoundedEncodedFrameBuffer(long maxMemoryBytes, Action<Exception> onError = null)
        {
            if (maxMemoryBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxMemoryBytes),
                    "The memory budget for compressed frames must be positive.");

            _maxMemoryBytes = maxMemoryBytes;
            _onError = onError;
            _storage = new EncodedFrameRingStorage(maxMemoryBytes);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                EncodedFrameSelector.DisposeAll(_videoMetadata.ToArray(), _onError);
                EncodedFrameSelector.DisposeAll(_audioMetadata.ToArray(), _onError);
                _videoMetadata.Clear();
                _audioMetadata.Clear();
                _samples.Clear();
                _storage = null;
            }
        }

        /// <summary>
        ///     Adds a video frame to the buffer.
        /// </summary>
        public bool TryAddVideoFrame(EncodedFrame frame)
        {
            return TryAdd(frame, true);
        }

        /// <summary>
        ///     Adds an audio frame to the buffer.
        /// </summary>
        public bool TryAddAudioFrame(EncodedFrame frame)
        {
            return TryAdd(frame, false);
        }

        public RecordingBufferStatistics GetStatistics()
        {
            var bufferedDuration = 0.0;
            long usedBytes;

            lock (_lock)
            {
                if (_videoQueueLatestTimestamp is { } latest)
                    foreach (var (frame, isVideo) in _samples)
                        if (isVideo && frame.Kind == UniencSampleKind.Key)
                        {
                            bufferedDuration = Math.Max(0, latest - frame.Timestamp);
                            break;
                        }

                usedBytes = _storage?.UsedBytes ?? 0;
            }

            return new RecordingBufferStatistics(bufferedDuration, usedBytes, _maxMemoryBytes, false);
        }

        /// <summary>
        ///     Gets frames for the specified duration, adjusted to start from a keyframe.
        /// </summary>
        /// <remarks>
        ///     The segment can only begin at a keyframe, so the IDR interval bounds how precisely the requested
        ///     duration can be honoured, and no frames at all are returned while the buffer holds no keyframe. Raising
        ///     <see cref="UniEnc.VideoEncoderOptions.IdrIntervalSeconds" /> beyond the retained duration therefore
        ///     yields an empty export. Eviction also works one frame at a time rather than one group of pictures at a
        ///     time, so the frames preceding the oldest surviving keyframe are retained but unusable, and a longer IDR
        ///     interval leaves a larger share of the memory budget unusable.
        /// </remarks>
        public ValueTask<EncodedFrameSelection> GetFramesForDurationAsync(double? durationSeconds)
        {
            // Nothing muxable. No audio frame is selected either in that case, so nothing is left to release.
            if (!TryTakeFrames(durationSeconds, out var storage, out var videoMetadata, out var video,
                    out var audioMetadata, out var audio))
                return default;

            // The readers take over the configuration records, and the payloads stay in the storage, which is no
            // longer written to.
            return new ValueTask<EncodedFrameSelection>(new EncodedFrameSelection(
                new RingStorageFrameReader(storage, videoMetadata, true, video),
                new RingStorageFrameReader(storage, audioMetadata, true, audio)));
        }

        /// <summary>
        ///     Gets frames for the specified duration, adjusted to start from a keyframe.
        /// </summary>
        /// <remarks>
        ///     The segment can only begin at a keyframe, so the IDR interval bounds how precisely the requested
        ///     duration can be honoured, and no frames at all are returned while the buffer holds no keyframe. Raising
        ///     <see cref="UniEnc.VideoEncoderOptions.IdrIntervalSeconds" /> beyond the retained duration therefore
        ///     yields an empty export. Eviction also works one frame at a time rather than one group of pictures at a
        ///     time, so the frames preceding the oldest surviving keyframe are retained but unusable, and a longer IDR
        ///     interval leaves a larger share of the memory budget unusable.
        /// </remarks>
        public ValueTask<CapturedFrames> CaptureAsync(double? durationSeconds)
        {
            if (!TryTakeFrames(durationSeconds, out var storage, out var videoMetadata, out var video,
                    out var audioMetadata, out var audio))
                return new ValueTask<CapturedFrames>((CapturedFrames)null);

            return new ValueTask<CapturedFrames>(new RingStorageCapturedFrames(storage, videoMetadata, video,
                audioMetadata, audio, _onError));
        }

        private bool TryAdd(EncodedFrame frame, bool isVideo)
        {
            lock (_lock)
            {
                if (_disposed || _storage == null) return false;

                if (frame.Kind == UniencSampleKind.Metadata)
                {
                    // Ownership of the frame passes to the buffer.
                    (isVideo ? _videoMetadata : _audioMetadata).Add(frame);
                    return true;
                }

                var length = frame.Data.Length;
                if (length > _storage.Capacity)
                {
                    _onError?.Invoke(new InvalidOperationException(
                        $"An encoded frame of {length} bytes exceeds the memory budget of {_storage.Capacity} bytes and was dropped."));
                    return false;
                }

                while (_storage.Capacity - _storage.UsedBytes < length)
                {
                    var (oldest, _) = _samples.Dequeue();
                    _storage.Release(oldest.End);
                }

                var position = _storage.Write(frame.Data);
                _samples.Enqueue((new StoredEncodedFrame(position, length, frame.Timestamp, frame.Kind), isVideo));

                // MediaCodec (Android) may produce out-of-order frame with timestamp=0 at the end of stream.
                if (isVideo && (_videoQueueLatestTimestamp is not { } latest || frame.Timestamp >= latest))
                    // in-order frame
                    _videoQueueLatestTimestamp = frame.Timestamp;
            }

            // The payload has been copied into the storage.
            frame.Dispose();
            return true;
        }

        /// <summary>
        ///     Takes the frames for the duration out of the buffer, which is left empty. Returns false when nothing is
        ///     muxable, in which case the configuration records taken out are released here.
        /// </summary>
        private bool TryTakeFrames(double? durationSeconds, out EncodedFrameRingStorage storage,
            out EncodedFrame[] videoMetadata, out ArraySegment<StoredEncodedFrame> video,
            out EncodedFrame[] audioMetadata, out ArraySegment<StoredEncodedFrame> audio)
        {
            var videoSamples = new List<StoredEncodedFrame>();
            var audioSamples = new List<StoredEncodedFrame>();
            double latest;

            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(BoundedEncodedFrameBuffer));

                foreach (var (frame, isVideo) in _samples)
                    (isVideo ? videoSamples : audioSamples).Add(frame);
                _samples.Clear();

                videoMetadata = _videoMetadata.ToArray();
                audioMetadata = _audioMetadata.ToArray();
                _videoMetadata.Clear();
                _audioMetadata.Clear();

                storage = _storage;
                _storage = null;
                latest = _videoQueueLatestTimestamp ?? 0;
            }

            var videoArray = videoSamples.ToArray();
            var audioArray = audioSamples.ToArray();

            if (storage == null || !EncodedFrameSelector.TrySelect(
                    EncodedFrameSelector.ToDescriptors(videoArray),
                    EncodedFrameSelector.ToDescriptors(audioArray),
                    latest, durationSeconds, out var videoStart, out var audioStart))
            {
                // Nothing muxable: either no frame at all, or no keyframe to start from.
                // The metadata frames were taken out of their lists above, so they are released here.
                EncodedFrameSelector.DisposeAll(videoMetadata, _onError);
                EncodedFrameSelector.DisposeAll(audioMetadata, _onError);
                video = default;
                audio = default;
                return false;
            }

            video = new ArraySegment<StoredEncodedFrame>(videoArray, videoStart, videoArray.Length - videoStart);
            audio = new ArraySegment<StoredEncodedFrame>(audioArray, audioStart, audioArray.Length - audioStart);
            return true;
        }
    }

    /// <summary>
    ///     Location of a sample whose payload is held in an <see cref="EncodedFrameRingStorage" />.
    /// </summary>
    internal readonly struct StoredEncodedFrame
    {
        public readonly long Position;
        public readonly int Length;
        public readonly double Timestamp;
        public readonly UniencSampleKind Kind;

        public StoredEncodedFrame(long position, int length, double timestamp, UniencSampleKind kind)
        {
            Position = position;
            Length = length;
            Timestamp = timestamp;
            Kind = kind;
        }

        public long End => Position + Length;
    }

    /// <summary>
    ///     Byte ring of a fixed capacity that holds the payloads of encoded frames.
    /// </summary>
    /// <remarks>
    ///     The capacity is divided into chunks that are allocated when the ring first reaches them, so the memory grows
    ///     with the footage up to the capacity and is not rounded up for each frame, unlike arrays rented from
    ///     <see cref="ArrayPool{T}.Shared" />. Each chunk is smaller than the threshold of the large object heap. A
    ///     payload is written at the head and may span chunks and wrap around the end. Payloads are released from the
    ///     tail in the order they were written. Positions are logical offsets that increase monotonically. Not
    ///     thread-safe; the caller synchronizes writes and releases, and reads only after writing has ended.
    /// </remarks>
    internal sealed class EncodedFrameRingStorage
    {
        public const int DefaultChunkSize = 64 * 1024;

        private readonly int _chunkSize;
        private readonly byte[][] _chunks;
        private long _head;
        private long _tail;

        public EncodedFrameRingStorage(long capacity, int chunkSize = DefaultChunkSize)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));

            Capacity = capacity;
            _chunkSize = chunkSize;
            _chunks = new byte[(capacity + chunkSize - 1) / chunkSize][];
        }

        public long Capacity { get; }

        public long UsedBytes => _head - _tail;

        /// <summary>
        ///     Bytes of the chunks allocated so far.
        /// </summary>
        public long AllocatedBytes
        {
            get
            {
                long total = 0;
                foreach (var chunk in _chunks)
                    if (chunk != null)
                        total += chunk.Length;
                return total;
            }
        }

        /// <summary>
        ///     Copies the payload to the head and returns its position. The caller releases enough of the tail
        ///     beforehand.
        /// </summary>
        public long Write(ReadOnlySpan<byte> payload)
        {
            if (payload.Length > Capacity - UsedBytes)
                throw new InvalidOperationException("The storage does not have enough free space for the payload.");

            var position = _head;
            var length = payload.Length;
            var offset = position % Capacity;
            while (!payload.IsEmpty)
            {
                var segment = GetSegment(offset, payload.Length, true);
                payload[..segment.Length].CopyTo(segment);
                payload = payload[segment.Length..];
                offset = (offset + segment.Length) % Capacity;
            }

            _head = position + length;
            return position;
        }

        /// <summary>
        ///     Releases every payload that ends at or before the position.
        /// </summary>
        public void Release(long end)
        {
            if (end > _head) throw new ArgumentOutOfRangeException(nameof(end));
            if (end > _tail) _tail = end;
        }

        /// <summary>
        ///     Returns the payload at the position. A payload that spans chunks is copied into the scratch array, which is
        ///     rented from <see cref="ArrayPool{T}.Shared" /> or replaced by a larger one as needed; the caller returns it
        ///     to the pool when it is no longer needed.
        /// </summary>
        public ReadOnlySpan<byte> GetPayload(long position, int length, ref byte[] scratch)
        {
            if (position < _tail || position + length > _head)
                throw new ArgumentOutOfRangeException(nameof(position), "The payload has been released.");

            if (length == 0) return ReadOnlySpan<byte>.Empty;

            var offset = position % Capacity;
            var first = GetSegment(offset, length, false);
            if (first.Length == length) return first;

            if (scratch == null || scratch.Length < length)
            {
                if (scratch != null) ArrayPool<byte>.Shared.Return(scratch);
                scratch = ArrayPool<byte>.Shared.Rent(length);
            }

            var destination = scratch.AsSpan(0, length);
            var copied = 0;
            while (copied < length)
            {
                var segment = GetSegment(offset, length - copied, false);
                segment.CopyTo(destination[copied..]);
                copied += segment.Length;
                offset = (offset + segment.Length) % Capacity;
            }

            return destination;
        }

        private Span<byte> GetSegment(long offset, int maxLength, bool allocate)
        {
            var index = (int)(offset / _chunkSize);
            var chunk = _chunks[index];
            if (chunk == null)
            {
                if (!allocate) throw new InvalidOperationException("The chunk has not been written.");
                var chunkLength = (int)Math.Min(_chunkSize, Capacity - (long)index * _chunkSize);
                chunk = _chunks[index] = new byte[chunkLength];
            }

            var start = (int)(offset - (long)index * _chunkSize);
            return chunk.AsSpan(start, Math.Min(chunk.Length - start, maxLength));
        }
    }

    /// <summary>
    ///     Reads the samples of one stream from an <see cref="EncodedFrameRingStorage" />, preceded by the configuration
    ///     records of the stream and rebased so that the first sample starts at zero. Every sample read is a copy, so the
    ///     storage can be read any number of times.
    /// </summary>
    internal sealed class RingStorageFrameReader : IEncodedFrameReader
    {
        private readonly EncodedFrame[] _metadata;
        private readonly double _origin;
        private readonly bool _ownsMetadata;
        private ArraySegment<StoredEncodedFrame> _samples;
        private EncodedFrameRingStorage _storage;
        private int _position;
        private byte[] _scratch;

        /// <param name="ownsMetadata">
        ///     Whether the configuration records are handed over to the caller as they are read and released when the
        ///     reader is disposed. Otherwise they are copied, and the caller of the constructor keeps owning them.
        /// </param>
        public RingStorageFrameReader(EncodedFrameRingStorage storage, EncodedFrame[] metadata, bool ownsMetadata,
            ArraySegment<StoredEncodedFrame> samples)
        {
            _storage = storage;
            _metadata = metadata;
            _ownsMetadata = ownsMetadata;
            _samples = samples;
            _origin = samples.Count > 0 ? samples.Array[samples.Offset].Timestamp : 0;
        }

        public bool TryReadNext(out EncodedFrame frame)
        {
            if (_position < _metadata.Length)
            {
                var source = _metadata[_position++];
                frame = _ownsMetadata ? source : EncodedFrame.CreateWithCopy(source.Data, source.Timestamp, source.Kind);
                return true;
            }

            var index = _position - _metadata.Length;
            if (index < _samples.Count)
            {
                _position++;
                var sample = _samples.Array[_samples.Offset + index];
                frame = EncodedFrame.CreateWithCopy(_storage.GetPayload(sample.Position, sample.Length, ref _scratch),
                    sample.Timestamp - _origin, sample.Kind);
                return true;
            }

            frame = default;
            return false;
        }

        public void Dispose()
        {
            if (_ownsMetadata)
                for (var i = _position; i < _metadata.Length; i++)
                    _metadata[i].Dispose();

            _position = int.MaxValue;

            // The reader may stay reachable after it is disposed, for example from a completed muxing task that is still
            // referenced. Dropping the storage here keeps such a reference from holding the whole buffer.
            _storage = null;
            _samples = default;

            if (_scratch != null)
            {
                ArrayPool<byte>.Shared.Return(_scratch);
                _scratch = null;
            }
        }
    }
}
