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
    ///     The payloads of the samples of each stream are copied into an <see cref="EncodedFrameTrackStorage" />. The
    ///     storages of both streams take their chunks from one <see cref="EncodedFrameChunkPool" /> whose size is the
    ///     memory budget, so the memory held for the footage never exceeds the budget, and the budget is shared between
    ///     the streams in whatever ratio their bitrates have. When a stream needs a chunk and none is left, the oldest
    ///     samples are evicted in timestamp order: the oldest sample of the two streams goes first. The codec
    ///     configuration records are small and are kept outside the storages for the lifetime of the buffer.
    /// </remarks>
    internal class BoundedEncodedFrameBuffer : IEncodedFrameBuffer
    {
        private readonly List<EncodedFrame> _audioMetadata = new();
        private readonly Queue<StoredEncodedFrame> _audioSamples = new();
        private readonly object _lock = new();
        private readonly long _maxMemoryBytes;
        private readonly Action<Exception> _onError;
        private readonly List<EncodedFrame> _videoMetadata = new();
        private readonly Queue<StoredEncodedFrame> _videoSamples = new();

        // Null once the frames have been taken out for an export or a capture.
        private EncodedFrameTrackStorage _audioStorage;
        private bool _disposed;
        private EncodedFrameChunkPool _pool;
        private double? _videoQueueLatestTimestamp;
        private EncodedFrameTrackStorage _videoStorage;

        public BoundedEncodedFrameBuffer(long maxMemoryBytes, Action<Exception> onError = null,
            int chunkSize = EncodedFrameChunkPool.DefaultChunkSize)
        {
            if (maxMemoryBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxMemoryBytes),
                    "The memory budget for compressed frames must be positive.");

            _maxMemoryBytes = maxMemoryBytes;
            _onError = onError;
            _pool = EncodedFrameChunkPool.ForBudget(maxMemoryBytes, chunkSize);
            _videoStorage = new EncodedFrameTrackStorage(_pool);
            _audioStorage = new EncodedFrameTrackStorage(_pool);
        }

        /// <summary>
        ///     Bytes of the chunks allocated so far, or zero once the frames have been taken out.
        /// </summary>
        internal long AllocatedBytes
        {
            get
            {
                lock (_lock)
                {
                    return _pool?.AllocatedBytes ?? 0;
                }
            }
        }

        /// <summary>
        ///     Timestamps of the oldest sample of each stream, or null for a stream that holds no sample.
        /// </summary>
        internal (double? Video, double? Audio) OldestTimestamps
        {
            get
            {
                lock (_lock)
                {
                    return (_videoSamples.TryPeek(out var video) ? video.Timestamp : null,
                        _audioSamples.TryPeek(out var audio) ? audio.Timestamp : null);
                }
            }
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
                _videoSamples.Clear();
                _audioSamples.Clear();
                _pool = null;
                _videoStorage = null;
                _audioStorage = null;
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
                    foreach (var frame in _videoSamples)
                        if (frame.Kind == UniencSampleKind.Key)
                        {
                            bufferedDuration = Math.Max(0, latest - frame.Timestamp);
                            break;
                        }

                usedBytes = (_videoStorage?.UsedBytes ?? 0) + (_audioStorage?.UsedBytes ?? 0);
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
            if (!TryTakeFrames(durationSeconds, out var videoStorage, out var videoMetadata, out var video,
                    out var audioStorage, out var audioMetadata, out var audio))
                return default;

            // The readers take over the configuration records, and the payloads stay in the storages, which are no
            // longer written to.
            return new ValueTask<EncodedFrameSelection>(new EncodedFrameSelection(
                new TrackStorageFrameReader(videoStorage, videoMetadata, true, video),
                new TrackStorageFrameReader(audioStorage, audioMetadata, true, audio)));
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
            if (!TryTakeFrames(durationSeconds, out var videoStorage, out var videoMetadata, out var video,
                    out var audioStorage, out var audioMetadata, out var audio))
                return new ValueTask<CapturedFrames>((CapturedFrames)null);

            return new ValueTask<CapturedFrames>(new TrackStorageCapturedFrames(videoStorage, videoMetadata, video,
                audioStorage, audioMetadata, audio, _onError));
        }

        private bool TryAdd(EncodedFrame frame, bool isVideo)
        {
            lock (_lock)
            {
                if (_disposed || _pool == null) return false;

                if (frame.Kind == UniencSampleKind.Metadata)
                {
                    // Ownership of the frame passes to the buffer.
                    (isVideo ? _videoMetadata : _audioMetadata).Add(frame);
                    return true;
                }

                var length = frame.Data.Length;
                if (length > _pool.Capacity)
                {
                    _onError?.Invoke(new InvalidOperationException(
                        $"An encoded frame of {length} bytes exceeds the memory budget of {_pool.Capacity} bytes and was dropped."));
                    return false;
                }

                var storage = isVideo ? _videoStorage : _audioStorage;
                while (_pool.AvailableChunks < storage.GetChunksToAllocate(length))
                    EvictOldest();

                var position = storage.Write(frame.Data);
                (isVideo ? _videoSamples : _audioSamples).Enqueue(
                    new StoredEncodedFrame(position, length, frame.Timestamp, frame.Kind));

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
        ///     Evicts the sample with the older timestamp of the oldest samples of the two streams.
        /// </summary>
        private void EvictOldest()
        {
            bool fromVideo;
            if (_videoSamples.TryPeek(out var video) && _audioSamples.TryPeek(out var audio))
                fromVideo = video.Timestamp <= audio.Timestamp;
            else if (_videoSamples.Count > 0)
                fromVideo = true;
            else if (_audioSamples.Count > 0)
                fromVideo = false;
            else
                // Unreachable: an empty pair of storages holds no chunk, and the frame fits in the pool.
                throw new InvalidOperationException("No sample is left to evict.");

            if (fromVideo)
                _videoStorage.Release(_videoSamples.Dequeue().End);
            else
                _audioStorage.Release(_audioSamples.Dequeue().End);
        }

        /// <summary>
        ///     Takes the frames for the duration out of the buffer, which is left empty. Returns false when nothing is
        ///     muxable, in which case the configuration records taken out are released here.
        /// </summary>
        private bool TryTakeFrames(double? durationSeconds, out EncodedFrameTrackStorage videoStorage,
            out EncodedFrame[] videoMetadata, out ArraySegment<StoredEncodedFrame> video,
            out EncodedFrameTrackStorage audioStorage, out EncodedFrame[] audioMetadata,
            out ArraySegment<StoredEncodedFrame> audio)
        {
            StoredEncodedFrame[] videoArray;
            StoredEncodedFrame[] audioArray;
            double latest;

            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(BoundedEncodedFrameBuffer));

                videoArray = _videoSamples.ToArray();
                audioArray = _audioSamples.ToArray();
                _videoSamples.Clear();
                _audioSamples.Clear();

                videoMetadata = _videoMetadata.ToArray();
                audioMetadata = _audioMetadata.ToArray();
                _videoMetadata.Clear();
                _audioMetadata.Clear();

                videoStorage = _videoStorage;
                audioStorage = _audioStorage;
                _pool = null;
                _videoStorage = null;
                _audioStorage = null;
                latest = _videoQueueLatestTimestamp ?? 0;
            }

            if (videoStorage == null || !EncodedFrameSelector.TrySelect(
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
    ///     Location of a sample whose payload is held in an <see cref="EncodedFrameTrackStorage" />.
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
    ///     Fixed number of equally sized chunks shared by the <see cref="EncodedFrameTrackStorage" /> of each stream.
    /// </summary>
    /// <remarks>
    ///     Chunks are allocated when they are first rented and are reused after they are returned, so the memory grows
    ///     with the footage up to the capacity and is not rounded up for each frame, unlike arrays rented from
    ///     <see cref="ArrayPool{T}.Shared" />. Each chunk is smaller than the threshold of the large object heap. Not
    ///     thread-safe; the caller synchronizes access.
    /// </remarks>
    internal sealed class EncodedFrameChunkPool
    {
        public const int DefaultChunkSize = 64 * 1024;

        // A small budget is still divided into this many chunks at least. Each stream holds a partly filled chunk at
        // its head and at its tail, so a pool of a few chunks would make the streams evict each other entirely.
        private const int MinChunks = 16;

        private readonly Stack<byte[]> _free = new();
        private int _allocatedChunks;
        private int _rentedChunks;

        public EncodedFrameChunkPool(int chunkSize, int maxChunks)
        {
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            if (maxChunks <= 0) throw new ArgumentOutOfRangeException(nameof(maxChunks));

            ChunkSize = chunkSize;
            MaxChunks = maxChunks;
        }

        public int ChunkSize { get; }

        public int MaxChunks { get; }

        public long Capacity => (long)ChunkSize * MaxChunks;

        public int AvailableChunks => MaxChunks - _rentedChunks;

        /// <summary>
        ///     Bytes of the chunks allocated so far.
        /// </summary>
        public long AllocatedBytes => (long)ChunkSize * _allocatedChunks;

        /// <summary>
        ///     Creates a pool whose capacity is as close to the budget as possible without exceeding it. The chunks are
        ///     made smaller when the budget would hold too few of them.
        /// </summary>
        public static EncodedFrameChunkPool ForBudget(long budget, int chunkSize = DefaultChunkSize)
        {
            if (budget <= 0) throw new ArgumentOutOfRangeException(nameof(budget));
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));

            chunkSize = (int)Math.Max(1, Math.Min(chunkSize, budget / MinChunks));
            return new EncodedFrameChunkPool(chunkSize, (int)Math.Min(budget / chunkSize, int.MaxValue));
        }

        public byte[] Rent()
        {
            if (_rentedChunks >= MaxChunks) throw new InvalidOperationException("No chunk is left in the pool.");

            _rentedChunks++;
            if (_free.Count > 0) return _free.Pop();

            _allocatedChunks++;
            return new byte[ChunkSize];
        }

        public void Return(byte[] chunk)
        {
            if (chunk == null || chunk.Length != ChunkSize) throw new ArgumentException("Not a chunk of this pool.");

            _rentedChunks--;
            _free.Push(chunk);
        }
    }

    /// <summary>
    ///     Queue of chunks that holds the payloads of the encoded frames of one stream.
    /// </summary>
    /// <remarks>
    ///     A payload is written at the head and may span chunks. Payloads are released from the tail in the order they
    ///     were written, and each chunk is returned to the pool once every payload in it has been released. Positions are
    ///     logical offsets that increase monotonically. Not thread-safe; the caller synchronizes writes and releases, and
    ///     reads only after writing has ended.
    /// </remarks>
    internal sealed class EncodedFrameTrackStorage
    {
        private readonly int _chunkSize;

        // The chunks held, indexed by their number modulo the length. The pool never lends more chunks than that.
        private readonly byte[][] _chunks;
        private readonly EncodedFrameChunkPool _pool;

        // Number of the oldest chunk held.
        private long _firstChunk;
        private long _head;
        private long _tail;

        public EncodedFrameTrackStorage(EncodedFrameChunkPool pool)
        {
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _chunkSize = pool.ChunkSize;
            _chunks = new byte[pool.MaxChunks][];
        }

        public long UsedBytes => _head - _tail;

        /// <summary>
        ///     Returns the number of chunks to rent to write a payload of the length.
        /// </summary>
        public int GetChunksToAllocate(int length)
        {
            return (int)(ChunkCountUpTo(_head + length) - ChunkCountUpTo(_head));
        }

        /// <summary>
        ///     Copies the payload to the head and returns its position. The caller makes sure beforehand that the pool has
        ///     enough chunks left.
        /// </summary>
        public long Write(ReadOnlySpan<byte> payload)
        {
            if (GetChunksToAllocate(payload.Length) > _pool.AvailableChunks)
                throw new InvalidOperationException("The pool does not have enough chunks for the payload.");

            var position = _head;
            while (!payload.IsEmpty)
            {
                var number = _head / _chunkSize;
                var offset = (int)(_head - number * _chunkSize);
                var index = (int)(number % _chunks.Length);
                if (offset == 0) _chunks[index] = _pool.Rent();

                var length = Math.Min(_chunkSize - offset, payload.Length);
                payload[..length].CopyTo(_chunks[index].AsSpan(offset, length));
                payload = payload[length..];
                _head += length;
            }

            return position;
        }

        /// <summary>
        ///     Releases every payload that ends at or before the position.
        /// </summary>
        public void Release(long end)
        {
            if (end > _head) throw new ArgumentOutOfRangeException(nameof(end));
            if (end <= _tail) return;

            _tail = end;
            if (_tail == _head)
            {
                // Empty. The partly written chunk at the head is returned too, and the next payload starts a new chunk.
                _head = _tail = ChunkCountUpTo(_head) * _chunkSize;
                ReturnChunksBefore(_head / _chunkSize);
            }
            else
            {
                ReturnChunksBefore(_tail / _chunkSize);
            }
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

            var first = GetSegment(position, length);
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
                var segment = GetSegment(position + copied, length - copied);
                segment.CopyTo(destination[copied..]);
                copied += segment.Length;
            }

            return destination;
        }

        private ReadOnlySpan<byte> GetSegment(long position, int maxLength)
        {
            var number = position / _chunkSize;
            var offset = (int)(position - number * _chunkSize);
            return _chunks[(int)(number % _chunks.Length)].AsSpan(offset, Math.Min(_chunkSize - offset, maxLength));
        }

        /// <summary>
        ///     Number of chunks from the start of the logical space needed to hold the bytes before the position.
        /// </summary>
        private long ChunkCountUpTo(long position)
        {
            return (position + _chunkSize - 1) / _chunkSize;
        }

        private void ReturnChunksBefore(long number)
        {
            for (; _firstChunk < number; _firstChunk++)
            {
                var index = (int)(_firstChunk % _chunks.Length);
                _pool.Return(_chunks[index]);
                _chunks[index] = null;
            }
        }
    }

    /// <summary>
    ///     Reads the samples of one stream from an <see cref="EncodedFrameTrackStorage" />, preceded by the configuration
    ///     records of the stream and rebased so that the first sample starts at zero. Every sample read is a copy, so the
    ///     storage can be read any number of times.
    /// </summary>
    internal sealed class TrackStorageFrameReader : IEncodedFrameReader
    {
        private readonly EncodedFrame[] _metadata;
        private readonly double _origin;
        private readonly bool _ownsMetadata;
        private int _position;
        private ArraySegment<StoredEncodedFrame> _samples;
        private byte[] _scratch;
        private EncodedFrameTrackStorage _storage;

        /// <param name="ownsMetadata">
        ///     Whether the configuration records are handed over to the caller as they are read and released when the
        ///     reader is disposed. Otherwise they are copied, and the caller of the constructor keeps owning them.
        /// </param>
        public TrackStorageFrameReader(EncodedFrameTrackStorage storage, EncodedFrame[] metadata, bool ownsMetadata,
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
