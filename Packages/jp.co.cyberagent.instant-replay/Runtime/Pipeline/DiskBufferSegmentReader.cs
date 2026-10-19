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
    ///     Location and description of one record persisted by <see cref="DiskBufferSegmentWriter" />.
    /// </summary>
    internal readonly struct DiskBufferScannedRecord
    {
        public readonly string FilePath;
        public readonly long PayloadOffset;
        public readonly int PayloadLength;
        public readonly double Timestamp;
        public readonly UniencSampleKind Kind;
        public readonly uint Crc32;

        public DiskBufferScannedRecord(string filePath, long payloadOffset, int payloadLength, double timestamp,
            UniencSampleKind kind, uint crc32)
        {
            FilePath = filePath;
            PayloadOffset = payloadOffset;
            PayloadLength = payloadLength;
            Timestamp = timestamp;
            Kind = kind;
            Crc32 = crc32;
        }
    }

    /// <summary>
    ///     Everything recoverable from one session directory.
    /// </summary>
    internal sealed class DiskBufferScanResult
    {
        public readonly List<DiskBufferScannedRecord> AudioMetadata = new();
        public readonly List<DiskBufferScannedRecord> AudioSamples = new();
        public readonly List<DiskBufferScannedRecord> VideoMetadata = new();
        public readonly List<DiskBufferScannedRecord> VideoSamples = new();

        /// <summary>
        ///     Timestamp of the most recent in-order video sample, which anchors the requested duration.
        /// </summary>
        public double LatestVideoTimestamp { get; internal set; }
    }

    /// <summary>
    ///     Reads back the files written by <see cref="DiskBufferSegmentWriter" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The same reader serves the export of a live session and the recovery of a session left behind by an
    ///         abnormal termination, so the recovery path is exercised by every export.
    ///     </para>
    ///     <para>
    ///         Records are appended and never rewritten in place, so a truncation caused by an abnormal termination can
    ///         only occur at the end of a file. Scanning stops at the first record that cannot be complete, and the
    ///         records accepted before that point are returned. This type has no dependency on UnityEngine.
    ///     </para>
    /// </remarks>
    internal static class DiskBufferSegmentReader
    {
        /// <summary>
        ///     Enumerates the segment files of a session directory in the order they were written.
        /// </summary>
        public static List<string> EnumerateSegmentFiles(string directory)
        {
            var result = new List<string>();
            if (!Directory.Exists(directory)) return result;

            var indexed = new List<KeyValuePair<int, string>>();
            foreach (var path in Directory.GetFiles(directory, DiskBufferFormat.SegmentFileSearchPattern))
            {
                var index = DiskBufferFormat.ParseSegmentIndex(Path.GetFileName(path));
                if (index < 0) continue;
                indexed.Add(new KeyValuePair<int, string>(index, path));
            }

            indexed.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var pair in indexed) result.Add(pair.Value);
            return result;
        }

        /// <summary>
        ///     Scans a session directory. Payload checksums are verified when a payload is read, not here, so that scanning
        ///     does not have to read the whole buffer.
        /// </summary>
        public static DiskBufferScanResult Scan(string directory, Action<Exception> onError = null)
        {
            var result = new DiskBufferScanResult();

            var metadataPath = Path.Combine(directory, DiskBufferFormat.MetadataFileName);
            if (File.Exists(metadataPath))
                ScanFile(metadataPath, result, true, onError);

            foreach (var segment in EnumerateSegmentFiles(directory))
                ScanFile(segment, result, false, onError);

            double? latest = null;
            foreach (var record in result.VideoSamples)
                // MediaCodec may produce an out-of-order frame with timestamp zero at the end of the stream, so the
                // maximum is taken rather than the last value.
                if (latest is not { } value || record.Timestamp > value)
                    latest = record.Timestamp;

            result.LatestVideoTimestamp = latest ?? 0;
            return result;
        }

        /// <summary>
        ///     Builds the muxable selection from a scanned session directory. Used both when a live session exports and
        ///     when a session left behind by an abnormal termination is recovered, so that the two produce the same result.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The selection reads payloads from the segment files one at a time while it is consumed, so the session
        ///         directory must not be modified or deleted until the selection has been disposed.
        ///     </para>
        ///     <para>
        ///         The first selected video sample is read and verified here, so that a buffer whose stream cannot start
        ///         yields an empty selection before an output file is created.
        ///     </para>
        ///     <para>
        ///         The codec configuration is prepended to both streams, which the muxer requires before any sample. On
        ///         Apple platforms no configuration record is produced, because the parameter sets travel inside every
        ///         sample; the empty case is therefore normal rather than an error.
        ///     </para>
        /// </remarks>
        public static EncodedFrameSelection BuildSelection(DiskBufferScanResult scan, double? durationSeconds,
            Action<Exception> onError = null)
        {
            if (!TrySelectRecords(scan, durationSeconds, out var videoRecords, out var audioRecords, onError))
                return default;

            return new EncodedFrameSelection(
                new DiskBufferRecordReader(scan.VideoMetadata, videoRecords, onError),
                new DiskBufferRecordReader(scan.AudioMetadata, audioRecords, onError));
        }

        /// <summary>
        ///     Selects the records that <see cref="BuildSelection" /> would mux, without opening any reader.
        /// </summary>
        public static bool TrySelectRecords(DiskBufferScanResult scan, double? durationSeconds,
            out List<DiskBufferScannedRecord> videoRecords, out List<DiskBufferScannedRecord> audioRecords,
            Action<Exception> onError = null)
        {
            videoRecords = null;
            audioRecords = null;
            if (scan == null) return false;

            var videoDescriptors = EncodedFrameSelector.ToDescriptors(scan.VideoSamples);
            var audioDescriptors = EncodedFrameSelector.ToDescriptors(scan.AudioSamples);

            if (!EncodedFrameSelector.TrySelect(videoDescriptors, audioDescriptors, scan.LatestVideoTimestamp,
                    durationSeconds, out var videoStart, out var audioStart))
                return false;

            videoRecords = scan.VideoSamples.GetRange(videoStart, scan.VideoSamples.Count - videoStart);
            audioRecords = audioStart >= 0 && scan.AudioSamples.Count > 0
                ? scan.AudioSamples.GetRange(audioStart, scan.AudioSamples.Count - audioStart)
                : new List<DiskBufferScannedRecord>();

            // A corrupt first sample would truncate the video stream to nothing.
            if (VerifyPayload(videoRecords[0], onError)) return true;

            videoRecords = null;
            audioRecords = null;
            return false;
        }

        /// <summary>
        ///     Reads the payload of a record into the buffer, growing it as needed, and verifies its checksum. Returns
        ///     false when the payload is incomplete or its checksum does not match.
        /// </summary>
        internal static bool TryReadPayload(FileStream stream, in DiskBufferScannedRecord record, ref byte[] buffer)
        {
            if (buffer.Length < record.PayloadLength) buffer = new byte[record.PayloadLength];

            stream.Position = record.PayloadOffset;
            if (!ReadExactly(stream, buffer, record.PayloadLength)) return false;
            return Crc32.Compute(buffer, 0, record.PayloadLength) == record.Crc32;
        }

        internal static FileStream OpenSegmentForRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        }

        private static bool VerifyPayload(in DiskBufferScannedRecord record, Action<Exception> onError)
        {
            try
            {
                using var stream = OpenSegmentForRead(record.FilePath);
                var buffer = Array.Empty<byte>();
                return TryReadPayload(stream, record, ref buffer);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                return false;
            }
        }

        private static void ScanFile(string path, DiskBufferScanResult result, bool metadataFile,
            Action<Exception> onError)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    64 * 1024);

                var header = new byte[DiskBufferFormat.FileHeaderSize];
                if (!ReadExactly(stream, header, header.Length)) return;
                if (!DiskBufferFormat.TryReadFileHeader(header, header.Length, out _)) return;

                var recordHeader = new byte[DiskBufferFormat.RecordHeaderSize];
                var length = stream.Length;

                while (true)
                {
                    var headerPosition = stream.Position;
                    if (length - headerPosition < DiskBufferFormat.RecordHeaderSize) break;
                    if (!ReadExactly(stream, recordHeader, recordHeader.Length)) break;

                    var payloadPosition = stream.Position;
                    if (!DiskBufferFormat.TryReadRecordHeader(recordHeader, 0, length - payloadPosition,
                            out var parsed))
                        break;

                    var record = new DiskBufferScannedRecord(path, payloadPosition, parsed.PayloadLength,
                        parsed.Timestamp, parsed.Kind, parsed.Crc32);

                    if (parsed.Kind == UniencSampleKind.Metadata)
                    {
                        // Codec configuration is expected only in the metadata file. A metadata record found inside a
                        // segment is still honoured so that a buffer written by a future layout is not silently dropped.
                        (parsed.Track == DiskBufferTrack.Video ? result.VideoMetadata : result.AudioMetadata)
                            .Add(record);
                    }
                    else if (metadataFile)
                    {
                        // A sample in the metadata file cannot be placed in the timeline; ignore it rather than guess.
                    }
                    else
                    {
                        (parsed.Track == DiskBufferTrack.Video ? result.VideoSamples : result.AudioSamples)
                            .Add(record);
                    }

                    stream.Position = payloadPosition + parsed.PayloadLength;
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n <= 0) return false;
                read += n;
            }

            return true;
        }
    }

    /// <summary>
    ///     Reads the frames of one stream back from the segment files, one payload at a time. The codec configuration
    ///     records are returned first, unchanged, followed by the samples with timestamps rebased so that the first sample
    ///     starts at zero.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only the scanned records are held in memory; each payload is read, verified, and copied into a frame when
    ///         it is requested. The files the records point to must therefore remain in place until the reader is
    ///         disposed. Each reader owns its own file handle, so the video and audio readers of one selection can be
    ///         consumed concurrently.
    ///     </para>
    ///     <para>
    ///         A sample that cannot be read or whose checksum does not match ends the stream there, because the remainder
    ///         cannot be decoded past a corrupt sample. A configuration record that fails in the same way ends the
    ///         configuration, and the samples are still returned.
    ///     </para>
    /// </remarks>
    internal sealed class DiskBufferRecordReader : IEncodedFrameReader
    {
        private readonly IReadOnlyList<DiskBufferScannedRecord> _metadata;
        private readonly Action<Exception> _onError;
        private readonly double _origin;
        private readonly IReadOnlyList<DiskBufferScannedRecord> _samples;

        private byte[] _buffer = Array.Empty<byte>();
        private bool _ended;
        private int _metadataPosition;
        private string _openPath;
        private int _samplePosition;
        private FileStream _stream;

        public DiskBufferRecordReader(IReadOnlyList<DiskBufferScannedRecord> metadata,
            IReadOnlyList<DiskBufferScannedRecord> samples, Action<Exception> onError = null)
        {
            _metadata = metadata ?? Array.Empty<DiskBufferScannedRecord>();
            _samples = samples ?? Array.Empty<DiskBufferScannedRecord>();
            _onError = onError;
            _origin = _samples.Count > 0 ? _samples[0].Timestamp : 0;
        }

        public bool TryReadNext(out EncodedFrame frame)
        {
            while (!_ended && _metadataPosition < _metadata.Count)
            {
                if (TryRead(_metadata[_metadataPosition++], 0, out frame)) return true;
                _metadataPosition = _metadata.Count;
            }

            if (!_ended && _samplePosition < _samples.Count)
            {
                if (TryRead(_samples[_samplePosition++], _origin, out frame)) return true;
                _ended = true;
            }

            frame = default;
            return false;
        }

        public void Dispose()
        {
            _ended = true;
            _stream?.Dispose();
            _stream = null;
            _openPath = null;
        }

        private bool TryRead(in DiskBufferScannedRecord record, double origin, out EncodedFrame frame)
        {
            frame = default;

            try
            {
                if (!string.Equals(_openPath, record.FilePath, StringComparison.Ordinal))
                {
                    _stream?.Dispose();
                    _stream = null;
                    _openPath = null;
                    _stream = DiskBufferSegmentReader.OpenSegmentForRead(record.FilePath);
                    _openPath = record.FilePath;
                }

                if (!DiskBufferSegmentReader.TryReadPayload(_stream, record, ref _buffer)) return false;
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
                return false;
            }

            frame = EncodedFrame.CreateWithCopy(_buffer.AsSpan(0, record.PayloadLength), record.Timestamp - origin,
                record.Kind);
            return true;
        }
    }
}
