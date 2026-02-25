// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UniEnc;

namespace InstantReplay
{
    /// <summary>
    ///     Encoded frame buffer backed by disk storage.
    ///     Keeps only a lightweight in-memory index while frame data resides on disk.
    ///     Survives process crashes for later recovery via <see cref="DiskEncodedFrameBufferRecovery"/>.
    /// </summary>
    internal class DiskEncodedFrameBuffer : IEncodedFrameBuffer
    {
        /// <summary>
        ///     Magic number written at the end of an index entry to mark it as valid.
        /// </summary>
        private const int ValidMarker = unchecked((int)0xCAFE1234);

        /// <summary>
        ///     Size of a single index entry in bytes.
        /// </summary>
        internal const int IndexEntrySize = 8 + 4 + 8 + 1 + 4; // 25 bytes

        private readonly string _storagePath;
        private readonly long _maxDiskBytes;
        private readonly object _lock = new();

        private readonly FileStream _videoDataStream;
        private readonly FileStream _audioDataStream;
        private readonly FileStream _videoIndexStream;
        private readonly FileStream _audioIndexStream;

        private readonly List<FrameIndexEntry> _videoIndex = new();
        private readonly List<FrameIndexEntry> _audioIndex = new();
        private readonly List<EncodedFrame> _videoMetadata = new();
        private readonly List<EncodedFrame> _audioMetadata = new();

        private long _currentDiskUsage;
        private int _videoIndexStart;
        private int _audioIndexStart;
        private bool _disposed;
        private double? _videoLatestTimestamp;

        internal DiskEncodedFrameBuffer(
            string storagePath,
            long maxDiskBytes,
            VideoEncoderOptions videoOptions,
            AudioEncoderOptions audioOptions)
        {
            _storagePath = storagePath;
            _maxDiskBytes = maxDiskBytes;

            Directory.CreateDirectory(storagePath);

            // Write manifest for crash recovery
            var manifest = new DiskBufferManifest
            {
                Version = 1,
                VideoOptions = new DiskBufferManifest.VideoOptionsData
                {
                    Width = videoOptions.Width,
                    Height = videoOptions.Height,
                    FpsHint = videoOptions.FpsHint,
                    Bitrate = videoOptions.Bitrate
                },
                AudioOptions = new DiskBufferManifest.AudioOptionsData
                {
                    SampleRate = audioOptions.SampleRate,
                    Channels = audioOptions.Channels,
                    Bitrate = audioOptions.Bitrate
                }
            };
            File.WriteAllText(
                Path.Combine(storagePath, "manifest.json"),
                DiskBufferManifest.ToJson(manifest),
                Encoding.UTF8);

            _videoDataStream = new FileStream(
                Path.Combine(storagePath, "video.dat"),
                FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
            _audioDataStream = new FileStream(
                Path.Combine(storagePath, "audio.dat"),
                FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
            _videoIndexStream = new FileStream(
                Path.Combine(storagePath, "video.idx"),
                FileMode.Create, FileAccess.Write, FileShare.Read, 4096);
            _audioIndexStream = new FileStream(
                Path.Combine(storagePath, "audio.idx"),
                FileMode.Create, FileAccess.Write, FileShare.Read, 4096);
        }

        public bool TryAddVideoFrame(EncodedFrame frame)
        {
            if (_disposed) return false;

            // Copy data out of the span before entering lock
            var dataLength = frame.Data.Length;
            var data = new byte[dataLength];
            frame.Data.CopyTo(data);
            var timestamp = frame.Timestamp;
            var kind = frame.Kind;

            lock (_lock)
            {
                if (_disposed) return false;

                if (kind == UniencSampleKind.Metadata)
                {
                    _videoMetadata.Add(EncodedFrame.CreateWithCopy(data, timestamp, kind));
                    frame.Dispose();
                    return true;
                }

                EnsureDiskCapacity(dataLength);

                var offset = _videoDataStream.Position;
                _videoDataStream.Write(data, 0, dataLength);
                _videoDataStream.Flush();

                WriteIndexEntry(_videoIndexStream, offset, dataLength, timestamp, kind);

                _videoIndex.Add(new FrameIndexEntry(offset, dataLength, timestamp, kind));
                _currentDiskUsage += dataLength;

                if (_videoLatestTimestamp is not { } latest || timestamp >= latest)
                    _videoLatestTimestamp = timestamp;
            }

            frame.Dispose();
            return true;
        }

        public bool TryAddAudioFrame(EncodedFrame frame)
        {
            if (_disposed) return false;

            var dataLength = frame.Data.Length;
            var data = new byte[dataLength];
            frame.Data.CopyTo(data);
            var timestamp = frame.Timestamp;
            var kind = frame.Kind;

            lock (_lock)
            {
                if (_disposed) return false;

                if (kind == UniencSampleKind.Metadata)
                {
                    _audioMetadata.Add(EncodedFrame.CreateWithCopy(data, timestamp, kind));
                    frame.Dispose();
                    return true;
                }

                EnsureDiskCapacity(dataLength);

                var offset = _audioDataStream.Position;
                _audioDataStream.Write(data, 0, dataLength);
                _audioDataStream.Flush();

                WriteIndexEntry(_audioIndexStream, offset, dataLength, timestamp, kind);

                _audioIndex.Add(new FrameIndexEntry(offset, dataLength, timestamp, kind));
                _currentDiskUsage += dataLength;
            }

            frame.Dispose();
            return true;
        }

        public void GetFramesForDuration(double? durationSeconds,
            out ReadOnlyMemory<EncodedFrame> videoFrames,
            out ReadOnlyMemory<EncodedFrame> audioFrames)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DiskEncodedFrameBuffer));

            int videoCount;
            int audioCount;
            FrameIndexEntry[] videoEntries;
            FrameIndexEntry[] audioEntries;
            EncodedFrame[] videoMeta;
            EncodedFrame[] audioMeta;

            lock (_lock)
            {
                videoCount = _videoIndex.Count - _videoIndexStart;
                audioCount = _audioIndex.Count - _audioIndexStart;

                videoEntries = new FrameIndexEntry[videoCount];
                for (var i = 0; i < videoCount; i++)
                    videoEntries[i] = _videoIndex[_videoIndexStart + i];

                audioEntries = new FrameIndexEntry[audioCount];
                for (var i = 0; i < audioCount; i++)
                    audioEntries[i] = _audioIndex[_audioIndexStart + i];

                videoMeta = _videoMetadata.ToArray();
                audioMeta = _audioMetadata.ToArray();
                _videoMetadata.Clear();
                _audioMetadata.Clear();
            }

            if (videoCount == 0)
            {
                videoFrames = default;
                audioFrames = default;
                return;
            }

            // Find keyframe closest to the requested start time
            var latest = _videoLatestTimestamp ?? 0;
            var argMinTimespan = -1;

            if (durationSeconds is { } durationSecondsValue)
            {
                var expectedStartTime = latest - durationSecondsValue;
                var minTimespan = double.MaxValue;
                for (var i = 0; i < videoCount; i++)
                {
                    if (videoEntries[i].Kind != UniencSampleKind.Key) continue;
                    var timespan = Math.Abs(videoEntries[i].Timestamp - expectedStartTime);
                    if (timespan >= minTimespan) continue;
                    minTimespan = timespan;
                    argMinTimespan = i;
                }
            }
            else
            {
                for (var i = 0; i < videoCount; i++)
                {
                    if (videoEntries[i].Kind != UniencSampleKind.Key) continue;
                    argMinTimespan = i;
                    break;
                }
            }

            if (argMinTimespan == -1)
            {
                videoFrames = default;
                audioFrames = default;
                return;
            }

            // Find audio start index
            int argMinAudioTimespan;
            if (audioCount == 0)
            {
                argMinAudioTimespan = 0;
            }
            else
            {
                var actualDuration = latest - videoEntries[argMinTimespan].Timestamp;
                var expectedAudioStartTime = audioEntries[audioCount - 1].Timestamp - actualDuration;

                var minAudioTimespan = double.MaxValue;
                argMinAudioTimespan = -1;
                for (var i = 0; i < audioCount; i++)
                {
                    var timespan = Math.Abs(audioEntries[i].Timestamp - expectedAudioStartTime);
                    if (timespan >= minAudioTimespan) continue;
                    minAudioTimespan = timespan;
                    argMinAudioTimespan = i;
                }
            }

            // Read selected frames from disk, sorted by file offset for sequential I/O
            var selectedVideoEntries = new FrameIndexEntry[videoCount - argMinTimespan];
            Array.Copy(videoEntries, argMinTimespan, selectedVideoEntries, 0, selectedVideoEntries.Length);

            var selectedAudioEntries = audioCount > 0 && argMinAudioTimespan >= 0
                ? new FrameIndexEntry[audioCount - argMinAudioTimespan]
                : Array.Empty<FrameIndexEntry>();
            if (selectedAudioEntries.Length > 0)
                Array.Copy(audioEntries, argMinAudioTimespan, selectedAudioEntries, 0, selectedAudioEntries.Length);

            var videoFrameArray = ReadFramesFromDisk(
                Path.Combine(_storagePath, "video.dat"), selectedVideoEntries);
            var audioFrameArray = ReadFramesFromDisk(
                Path.Combine(_storagePath, "audio.dat"), selectedAudioEntries);

            // Adjust timestamps
            if (videoFrameArray.Length > 0)
            {
                var videoStartTime = videoFrameArray[0].Timestamp;
                for (var i = 0; i < videoFrameArray.Length; i++)
                    videoFrameArray[i] = videoFrameArray[i].WithTimestamp(
                        videoFrameArray[i].Timestamp - videoStartTime);
            }

            if (audioFrameArray.Length > 0)
            {
                var audioStartTime = audioFrameArray[0].Timestamp;
                for (var i = 0; i < audioFrameArray.Length; i++)
                    audioFrameArray[i] = audioFrameArray[i].WithTimestamp(
                        audioFrameArray[i].Timestamp - audioStartTime);
            }

            // Concat metadata
            if (videoMeta.Length > 0)
            {
                var combined = new EncodedFrame[videoMeta.Length + videoFrameArray.Length];
                Array.Copy(videoMeta, 0, combined, 0, videoMeta.Length);
                Array.Copy(videoFrameArray, 0, combined, videoMeta.Length, videoFrameArray.Length);
                videoFrameArray = combined;
            }

            if (audioMeta.Length > 0)
            {
                var combined = new EncodedFrame[audioMeta.Length + audioFrameArray.Length];
                Array.Copy(audioMeta, 0, combined, 0, audioMeta.Length);
                Array.Copy(audioFrameArray, 0, combined, audioMeta.Length, audioFrameArray.Length);
                audioFrameArray = combined;
            }

            videoFrames = videoFrameArray;
            audioFrames = audioFrameArray;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                _videoDataStream.Dispose();
                _audioDataStream.Dispose();
                _videoIndexStream.Dispose();
                _audioIndexStream.Dispose();

                foreach (var frame in _videoMetadata)
                    frame.Dispose();
                foreach (var frame in _audioMetadata)
                    frame.Dispose();
            }
        }

        /// <summary>
        ///     Deletes all storage files. Call after a successful export when data is no longer needed.
        /// </summary>
        public void CleanupStorage()
        {
            try
            {
                if (Directory.Exists(_storagePath))
                    Directory.Delete(_storagePath, true);
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }
        }

        private void EnsureDiskCapacity(int requiredBytes)
        {
            if (_currentDiskUsage + requiredBytes <= _maxDiskBytes)
                return;

            var needToBeFreed = _currentDiskUsage + requiredBytes - _maxDiskBytes;
            long freed = 0;

            while (freed < needToBeFreed)
            {
                var hasVideo = _videoIndexStart < _videoIndex.Count;
                var hasAudio = _audioIndexStart < _audioIndex.Count;

                if (hasVideo && hasAudio)
                {
                    var videoEntry = _videoIndex[_videoIndexStart];
                    var audioEntry = _audioIndex[_audioIndexStart];

                    if (videoEntry.Timestamp <= audioEntry.Timestamp)
                    {
                        freed += videoEntry.DataLength;
                        _videoIndexStart++;
                    }
                    else
                    {
                        freed += audioEntry.DataLength;
                        _audioIndexStart++;
                    }
                }
                else if (hasVideo)
                {
                    freed += _videoIndex[_videoIndexStart].DataLength;
                    _videoIndexStart++;
                }
                else if (hasAudio)
                {
                    freed += _audioIndex[_audioIndexStart].DataLength;
                    _audioIndexStart++;
                }
                else
                {
                    break;
                }
            }

            _currentDiskUsage -= freed;
        }

        private static void WriteIndexEntry(FileStream indexStream, long offset, int length, double timestamp,
            UniencSampleKind kind)
        {
            Span<byte> buffer = stackalloc byte[IndexEntrySize];

            BitConverter.TryWriteBytes(buffer, offset);
            BitConverter.TryWriteBytes(buffer.Slice(8), length);
            BitConverter.TryWriteBytes(buffer.Slice(12), timestamp);
            buffer[20] = (byte)kind;
            // Write without valid marker first
            BitConverter.TryWriteBytes(buffer.Slice(21), 0);

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            indexStream.Write(buffer);
#else
            var bytes = new byte[IndexEntrySize];
            buffer.CopyTo(bytes);
            indexStream.Write(bytes, 0, bytes.Length);
#endif
            indexStream.Flush();

            // Now write the valid marker
            var markerPos = indexStream.Position - 4;
            indexStream.Position = markerPos;

            Span<byte> markerBuffer = stackalloc byte[4];
            BitConverter.TryWriteBytes(markerBuffer, ValidMarker);
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            indexStream.Write(markerBuffer);
#else
            var markerBytes = new byte[4];
            markerBuffer.CopyTo(markerBytes);
            indexStream.Write(markerBytes, 0, 4);
#endif
            indexStream.Flush();
        }

        private static EncodedFrame[] ReadFramesFromDisk(string datFilePath, FrameIndexEntry[] entries)
        {
            if (entries.Length == 0)
                return Array.Empty<EncodedFrame>();

            // Sort by offset for sequential I/O, keeping original indices
            var sortedIndices = new int[entries.Length];
            for (var i = 0; i < entries.Length; i++)
                sortedIndices[i] = i;
            Array.Sort(sortedIndices, (a, b) => entries[a].DataOffset.CompareTo(entries[b].DataOffset));

            var frames = new EncodedFrame[entries.Length];

            using var stream = new FileStream(datFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);

            foreach (var idx in sortedIndices)
            {
                var entry = entries[idx];
                var data = new byte[entry.DataLength];

                stream.Position = entry.DataOffset;
                var totalRead = 0;
                while (totalRead < entry.DataLength)
                {
                    var read = stream.Read(data, totalRead, entry.DataLength - totalRead);
                    if (read == 0) throw new EndOfStreamException("Unexpected end of data file.");
                    totalRead += read;
                }

                frames[idx] = EncodedFrame.CreateWithCopy(data, entry.Timestamp, entry.Kind);
            }

            return frames;
        }

        /// <summary>
        ///     A single entry in the in-memory frame index.
        /// </summary>
        internal readonly struct FrameIndexEntry
        {
            public readonly long DataOffset;
            public readonly int DataLength;
            public readonly double Timestamp;
            public readonly UniencSampleKind Kind;

            public FrameIndexEntry(long dataOffset, int dataLength, double timestamp, UniencSampleKind kind)
            {
                DataOffset = dataOffset;
                DataLength = dataLength;
                Timestamp = timestamp;
                Kind = kind;
            }
        }
    }
}
