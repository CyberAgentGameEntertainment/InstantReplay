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
        ///     The codec configuration is prepended to both streams, which the muxer requires before any sample. On Apple
        ///     platforms no configuration record is produced, because the parameter sets travel inside every sample; the
        ///     empty case is therefore normal rather than an error.
        /// </remarks>
        public static EncodedFrameSelection BuildSelection(DiskBufferScanResult scan, double? durationSeconds,
            Action<Exception> onError = null)
        {
            if (scan == null) return default;

            var videoDescriptors = EncodedFrameSelector.ToDescriptors(scan.VideoSamples);
            var audioDescriptors = EncodedFrameSelector.ToDescriptors(scan.AudioSamples);

            if (!EncodedFrameSelector.TrySelect(videoDescriptors, audioDescriptors, scan.LatestVideoTimestamp,
                    durationSeconds, out var videoStart, out var audioStart))
                return default;

            var videoRecords = scan.VideoSamples.GetRange(videoStart, scan.VideoSamples.Count - videoStart);
            var audioRecords = audioStart >= 0 && scan.AudioSamples.Count > 0
                ? scan.AudioSamples.GetRange(audioStart, scan.AudioSamples.Count - audioStart)
                : new List<DiskBufferScannedRecord>();

            var videoFrames = Materialize(videoRecords, onError);
            var audioFrames = Materialize(audioRecords, onError);
            var videoMetadata = Materialize(scan.VideoMetadata, onError);
            var audioMetadata = Materialize(scan.AudioMetadata, onError);

            if (videoFrames.Length == 0)
            {
                // Nothing muxable; release everything that was materialized so no pooled array is leaked.
                EncodedFrameSelector.DisposeAll(videoFrames, onError);
                EncodedFrameSelector.DisposeAll(audioFrames, onError);
                EncodedFrameSelector.DisposeAll(videoMetadata, onError);
                EncodedFrameSelector.DisposeAll(audioMetadata, onError);
                return default;
            }

            EncodedFrameSelector.RebaseTimestamps(videoFrames);
            EncodedFrameSelector.RebaseTimestamps(audioFrames);

            return new EncodedFrameSelection(
                EncodedFrameSelector.PrependMetadata(videoFrames, videoMetadata),
                EncodedFrameSelector.PrependMetadata(audioFrames, audioMetadata));
        }

        /// <summary>
        ///     Reads the payloads of the given records and materializes them as frames. Records are grouped by file and
        ///     read in offset order. A record whose checksum does not match truncates the result there, because the
        ///     remainder of the stream cannot be decoded past a corrupt sample.
        /// </summary>
        public static EncodedFrame[] Materialize(IReadOnlyList<DiskBufferScannedRecord> records,
            Action<Exception> onError = null)
        {
            if (records == null || records.Count == 0) return Array.Empty<EncodedFrame>();

            var frames = new List<EncodedFrame>(records.Count);
            var buffer = Array.Empty<byte>();
            FileStream stream = null;
            var openPath = (string)null;

            try
            {
                foreach (var record in records)
                {
                    if (!string.Equals(openPath, record.FilePath, StringComparison.Ordinal))
                    {
                        stream?.Dispose();
                        stream = new FileStream(record.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                            64 * 1024);
                        openPath = record.FilePath;
                    }

                    if (buffer.Length < record.PayloadLength) buffer = new byte[record.PayloadLength];

                    stream.Position = record.PayloadOffset;
                    if (!ReadExactly(stream, buffer, record.PayloadLength)) break;
                    if (Crc32.Compute(buffer, 0, record.PayloadLength) != record.Crc32) break;

                    frames.Add(EncodedFrame.CreateWithCopy(buffer.AsSpan(0, record.PayloadLength), record.Timestamp,
                        record.Kind));
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
            finally
            {
                stream?.Dispose();
            }

            return frames.ToArray();
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
}
