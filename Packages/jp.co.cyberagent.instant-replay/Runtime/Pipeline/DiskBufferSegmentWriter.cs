// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using UniEnc;

namespace InstantReplay
{
    /// <summary>
    ///     Appends encoded frames to the segment files of one session directory, rotates segments at video key frames, and
    ///     deletes the oldest segments so that the session directory never exceeds its configured size.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The size limit is a hard bound rather than a target. Space for a record is reserved before it is written,
    ///         and the reservation deletes as many of the oldest closed segments as it needs. When the limit still cannot
    ///         be met — which requires the open segment alone to fill the budget — the record is dropped instead of being
    ///         written. The total size of the session directory therefore never exceeds the limit at any instant, not even
    ///         transiently between a write and the eviction that follows it.
    ///     </para>
    ///     <para>
    ///         The limit covers the whole session directory: the manifest, the codec configuration file, and every segment.
    ///         The manifest and the codec configuration file are not evictable, so they are accounted for and the remainder
    ///         is what segments may occupy.
    ///     </para>
    ///     <para>
    ///         Every member is called from the single writer thread of <see cref="DiskEncodedFrameBuffer" />, so no
    ///         synchronization is performed here. This type has no dependency on UnityEngine.
    ///     </para>
    /// </remarks>
    internal sealed class DiskBufferSegmentWriter : IDisposable
    {
        private readonly List<ClosedSegment> _closedSegments = new();
        private readonly string _directory;
        private readonly long _maxSegmentBytes;
        private readonly long _maxTotalBytes;
        private readonly List<byte[]> _metadataAudioPayloads = new();
        private readonly List<byte[]> _metadataVideoPayloads = new();
        private readonly Action<Exception> _onError;
        private readonly double _segmentDuration;
        private readonly DiskBufferSyncMode _syncMode;

        private bool _disposed;
        private long _manifestBytes;
        private long _metadataBytes;
        private FileStream _metadataStream;
        private long _openSegmentBytes;
        private double? _openSegmentFirstTimestamp;
        private int _openSegmentIndex = -1;
        private FileStream _openSegmentStream;
        private byte[] _scratch = new byte[DiskBufferFormat.RecordHeaderSize + 64 * 1024];
        private long _totalSegmentBytes;

        public DiskBufferSegmentWriter(string directory, long maxTotalBytes, double segmentDuration,
            long maxSegmentBytes, DiskBufferSyncMode syncMode, Action<Exception> onError = null)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _maxTotalBytes = maxTotalBytes;
            _segmentDuration = segmentDuration;
            _maxSegmentBytes = maxSegmentBytes;
            _syncMode = syncMode;
            _onError = onError;
        }

        /// <summary>
        ///     Number of records dropped because they did not fit within the configured size.
        /// </summary>
        public long DroppedRecordCount { get; private set; }

        /// <summary>
        ///     Current size of the session directory as tracked by this writer.
        /// </summary>
        public long TotalBytes => _manifestBytes + _metadataBytes + _totalSegmentBytes;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CloseOpenSegment();

            try
            {
                _metadataStream?.Dispose();
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }

            _metadataStream = null;
        }

        /// <summary>
        ///     Records the size of the manifest so that it is accounted for against the size limit. The manifest is written
        ///     by the caller before any frame is accepted.
        /// </summary>
        public void SetManifestBytes(long bytes)
        {
            _manifestBytes = bytes;
        }

        /// <summary>
        ///     Writes one frame. Codec configuration goes to the unevictable metadata file; every other frame goes to the
        ///     current segment. Returns false when the frame was dropped because it did not fit.
        /// </summary>
        public bool Write(DiskBufferTrack track, in EncodedFrame frame)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DiskBufferSegmentWriter));

            return frame.Kind == UniencSampleKind.Metadata
                ? WriteMetadata(track, frame)
                : WriteSample(track, frame);
        }

        /// <summary>
        ///     Hands everything written so far to the operating system, which is what makes it survive a process crash.
        /// </summary>
        public void FlushToOperatingSystem()
        {
            if (_disposed) return;

            try
            {
                _openSegmentStream?.Flush();
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }
        }

        private bool WriteMetadata(DiskBufferTrack track, in EncodedFrame frame)
        {
            var payload = frame.Data;

            // Codec configuration is emitted once per stream on every supported platform. Storing only distinct payloads
            // keeps a platform that reissues it from eroding the space reserved for segments.
            var known = track == DiskBufferTrack.Video ? _metadataVideoPayloads : _metadataAudioPayloads;
            foreach (var existing in known)
                if (PayloadEquals(existing, payload))
                    return true;

            var required = DiskBufferFormat.RecordHeaderSize + payload.Length;
            var fileHeader = _metadataStream == null ? DiskBufferFormat.FileHeaderSize : 0;

            // Losing the codec configuration makes the whole session unrecoverable, so a failure to store it is
            // reported rather than counted as an ordinary dropped record.
            if (_metadataBytes + fileHeader + required > DiskBufferFormat.MaxMetadataBytes)
            {
                _onError?.Invoke(new InvalidOperationException(
                    "The disk buffer could not store codec configuration: the metadata file is full. " +
                    "The session will not be recoverable."));
                return false;
            }

            if (!TryReserve(fileHeader + required))
            {
                _onError?.Invoke(new InvalidOperationException(
                    "The disk buffer could not store codec configuration within MaxDiskUsageBytes. " +
                    "The session will not be recoverable; raise MaxDiskUsageBytes."));
                return false;
            }

            if (_metadataStream == null)
                try
                {
                    _metadataStream = CreateFile(Path.Combine(_directory, DiskBufferFormat.MetadataFileName),
                        DiskBufferFormat.MetadataFileIndex);
                    _metadataBytes += fileHeader;
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);
                    _metadataStream = null;
                    return false;
                }

            WriteRecord(_metadataStream, track, frame.Kind, frame.Timestamp, payload);
            _metadataBytes += required;

            var copy = new byte[payload.Length];
            payload.CopyTo(copy);
            known.Add(copy);

            // Nothing can be muxed without the codec configuration, so it always reaches the storage device immediately.
            try
            {
                _metadataStream.Flush(true);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }

            return true;
        }

        private bool WriteSample(DiskBufferTrack track, in EncodedFrame frame)
        {
            if (ShouldRotate(track, frame)) RotateSegment();

            if (_openSegmentStream == null && !OpenSegment()) return Drop();

            var payload = frame.Data;
            var required = DiskBufferFormat.RecordHeaderSize + payload.Length;

            if (!TryReserve(required)) return Drop();

            WriteRecord(_openSegmentStream, track, frame.Kind, frame.Timestamp, payload);

            _openSegmentBytes += required;
            _totalSegmentBytes += required;
            _openSegmentFirstTimestamp ??= frame.Timestamp;

            if (_syncMode == DiskBufferSyncMode.EveryRecord)
                try
                {
                    _openSegmentStream.Flush(true);
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);
                }

            return true;
        }

        private bool Drop()
        {
            DroppedRecordCount++;
            return false;
        }

        private bool ShouldRotate(DiskBufferTrack track, in EncodedFrame frame)
        {
            if (_openSegmentStream == null) return false;

            // A segment always starts at a video key frame, so that discarding an older segment never leaves a partial
            // group of pictures at the head of the buffer.
            if (track != DiskBufferTrack.Video || frame.Kind != UniencSampleKind.Key) return false;

            if (_openSegmentBytes >= _maxSegmentBytes) return true;

            return _openSegmentFirstTimestamp is { } first && frame.Timestamp - first >= _segmentDuration;
        }

        private void RotateSegment()
        {
            // Closing the open segment first makes it evictable, so that the reservation for the new segment can reclaim
            // its space. Without this the buffer would deadlock once the open segment alone filled the budget.
            CloseOpenSegment();
            OpenSegment();
        }

        private bool OpenSegment()
        {
            if (!TryReserve(DiskBufferFormat.FileHeaderSize)) return false;

            var index = _openSegmentIndex + 1;
            var path = Path.Combine(_directory, DiskBufferFormat.GetSegmentFileName(index));

            try
            {
                _openSegmentStream = CreateFile(path, unchecked((uint)index));
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
                _openSegmentStream = null;
                return false;
            }

            _openSegmentIndex = index;
            _openSegmentBytes = DiskBufferFormat.FileHeaderSize;
            _totalSegmentBytes += DiskBufferFormat.FileHeaderSize;
            _openSegmentFirstTimestamp = null;
            return true;
        }

        private void CloseOpenSegment()
        {
            if (_openSegmentStream == null) return;

            var path = _openSegmentStream.Name;

            try
            {
                // A closed segment is guaranteed to have reached the storage device, which bounds the loss on power loss
                // to the records of a single segment.
                _openSegmentStream.Flush(true);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }

            try
            {
                _openSegmentStream.Dispose();
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }

            _openSegmentStream = null;
            _closedSegments.Add(new ClosedSegment(path, _openSegmentBytes));
            _openSegmentBytes = 0;
            _openSegmentFirstTimestamp = null;
        }

        /// <summary>
        ///     Makes room for the given number of bytes by deleting the oldest closed segments. Returns false when the
        ///     limit cannot be met, in which case the caller must not write.
        /// </summary>
        private bool TryReserve(long additionalBytes)
        {
            while (TotalBytes + additionalBytes > _maxTotalBytes)
            {
                if (_closedSegments.Count == 0) return false;

                var oldest = _closedSegments[0];
                _closedSegments.RemoveAt(0);
                _totalSegmentBytes -= oldest.SizeBytes;

                try
                {
                    File.Delete(oldest.Path);
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);
                }
            }

            return true;
        }

        private void WriteRecord(FileStream stream, DiskBufferTrack track, UniencSampleKind kind, double timestamp,
            ReadOnlySpan<byte> payload)
        {
            var total = DiskBufferFormat.RecordHeaderSize + payload.Length;

            // Grown once and reused for the lifetime of the writer, so that no allocation occurs per frame.
            if (_scratch.Length < total) _scratch = new byte[total];

            DiskBufferFormat.WriteRecordHeader(_scratch, 0, payload.Length, track, kind, timestamp,
                Crc32.Compute(payload));
            payload.CopyTo(_scratch.AsSpan(DiskBufferFormat.RecordHeaderSize));

            stream.Write(_scratch, 0, total);
        }

        private static bool PayloadEquals(byte[] existing, ReadOnlySpan<byte> payload)
        {
            if (existing.Length != payload.Length) return false;
            for (var i = 0; i < existing.Length; i++)
                if (existing[i] != payload[i])
                    return false;
            return true;
        }

        private static FileStream CreateFile(string path, uint index)
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);

            var header = new byte[DiskBufferFormat.FileHeaderSize];
            DiskBufferFormat.WriteFileHeader(header, index);
            stream.Write(header, 0, header.Length);

            return stream;
        }

        private readonly struct ClosedSegment
        {
            public readonly string Path;
            public readonly long SizeBytes;

            public ClosedSegment(string path, long sizeBytes)
            {
                Path = path;
                SizeBytes = sizeBytes;
            }
        }
    }
}
