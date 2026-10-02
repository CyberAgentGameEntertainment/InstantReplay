// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UniEnc;

namespace InstantReplay
{
    internal enum DiskBufferTrack : byte
    {
        Video = 0,
        Audio = 1
    }

    /// <summary>
    ///     Layout of the files written by the disk buffer. See <c>docs/disk-buffered-recording.md</c> for the rationale.
    ///     This type intentionally has no dependency on UnityEngine so that the storage layer can be exercised outside the
    ///     Unity Editor.
    /// </summary>
    internal static class DiskBufferFormat
    {
        /// <summary>
        ///     Bumped whenever the layout below changes. A session written with a different version is not read back.
        /// </summary>
        public const int FormatVersion = 2;

        public const int FileHeaderSize = 16;
        public const int RecordHeaderSize = 20;

        /// <summary>
        ///     Segment index stored in the header of the metadata file, which is not part of the segment sequence.
        /// </summary>
        public const uint MetadataFileIndex = 0xFFFFFFFF;

        /// <summary>
        ///     Rejects implausible payload lengths while scanning, so that a torn record is not mistaken for a huge one.
        /// </summary>
        public const int MaxPayloadLength = 64 * 1024 * 1024;

        /// <summary>
        ///     Upper bound of the metadata file. Codec configuration is emitted once per stream on every supported
        ///     platform, so this is only a guard against a platform that reissues it without bound; exceeding it would
        ///     otherwise erode the space reserved for segments.
        /// </summary>
        public const long MaxMetadataBytes = 1024 * 1024;

        public const string ManifestFileName = "manifest.json";
        public const string MetadataFileName = "metadata.irb";
        public const string SegmentFileExtension = ".irb";
        public const string SegmentFilePrefix = "seg-";
        public const string SegmentFileSearchPattern = SegmentFilePrefix + "*" + SegmentFileExtension;

        // "IRSG"
        private const uint Magic = 0x47535249;

        public static string GetSegmentFileName(int index)
        {
            return $"{SegmentFilePrefix}{index:D8}{SegmentFileExtension}";
        }

        /// <summary>
        ///     Extracts the segment index from a segment file name, or returns -1 when the name is not one.
        /// </summary>
        public static int ParseSegmentIndex(string fileName)
        {
            if (fileName == null) return -1;
            if (!fileName.StartsWith(SegmentFilePrefix, StringComparison.Ordinal)) return -1;
            if (!fileName.EndsWith(SegmentFileExtension, StringComparison.Ordinal)) return -1;

            var digits = fileName.Substring(SegmentFilePrefix.Length,
                fileName.Length - SegmentFilePrefix.Length - SegmentFileExtension.Length);

            return int.TryParse(digits, out var index) && index >= 0 ? index : -1;
        }

        public static void WriteFileHeader(byte[] destination, uint index)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (destination.Length < FileHeaderSize)
                throw new ArgumentException("Destination is too small.", nameof(destination));

            WriteUInt32(destination, 0, Magic);
            WriteUInt32(destination, 4, unchecked((uint)FormatVersion));
            WriteUInt32(destination, 8, index);
            WriteUInt32(destination, 12, 0);
        }

        public static bool TryReadFileHeader(byte[] source, int length, out uint index)
        {
            index = 0;
            if (source == null || length < FileHeaderSize) return false;
            if (ReadUInt32(source, 0) != Magic) return false;
            if (ReadUInt32(source, 4) != unchecked((uint)FormatVersion)) return false;
            index = ReadUInt32(source, 8);
            return true;
        }

        public static void WriteRecordHeader(byte[] destination, int offset, int payloadLength, DiskBufferTrack track,
            UniencSampleKind kind, double timestamp, uint crc32)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (destination.Length - offset < RecordHeaderSize)
                throw new ArgumentException("Destination is too small.", nameof(destination));

            WriteUInt32(destination, offset, unchecked((uint)payloadLength));
            destination[offset + 4] = (byte)track;
            destination[offset + 5] = (byte)kind;
            destination[offset + 6] = 0;
            destination[offset + 7] = 0;
            WriteUInt64(destination, offset + 8, unchecked((ulong)BitConverter.DoubleToInt64Bits(timestamp)));
            WriteUInt32(destination, offset + 16, crc32);
        }

        /// <summary>
        ///     Parses a record header. Returns false when the header cannot belong to a complete record, which marks the end
        ///     of the usable part of a file truncated by an abnormal termination.
        /// </summary>
        public static bool TryReadRecordHeader(byte[] source, int offset, long remainingAfterHeader,
            out DiskBufferRecordHeader header)
        {
            header = default;
            if (source == null || source.Length - offset < RecordHeaderSize) return false;

            var payloadLength = unchecked((int)ReadUInt32(source, offset));
            if (payloadLength < 0 || payloadLength > MaxPayloadLength) return false;
            if (payloadLength > remainingAfterHeader) return false;

            var track = source[offset + 4];
            var kind = source[offset + 5];
            if (track > (byte)DiskBufferTrack.Audio) return false;
            if (kind > (byte)UniencSampleKind.Metadata) return false;

            var timestamp = BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64(source, offset + 8)));
            if (double.IsNaN(timestamp) || double.IsInfinity(timestamp)) return false;

            header = new DiskBufferRecordHeader(payloadLength, (DiskBufferTrack)track, (UniencSampleKind)kind,
                timestamp, ReadUInt32(source, offset + 16));
            return true;
        }

        private static void WriteUInt32(byte[] destination, int offset, uint value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteUInt64(byte[] destination, int offset, ulong value)
        {
            WriteUInt32(destination, offset, (uint)value);
            WriteUInt32(destination, offset + 4, (uint)(value >> 32));
        }

        private static uint ReadUInt32(byte[] source, int offset)
        {
            return source[offset]
                   | ((uint)source[offset + 1] << 8)
                   | ((uint)source[offset + 2] << 16)
                   | ((uint)source[offset + 3] << 24);
        }

        private static ulong ReadUInt64(byte[] source, int offset)
        {
            return ReadUInt32(source, offset) | ((ulong)ReadUInt32(source, offset + 4) << 32);
        }
    }

    internal readonly struct DiskBufferRecordHeader
    {
        public readonly int PayloadLength;
        public readonly DiskBufferTrack Track;
        public readonly UniencSampleKind Kind;
        public readonly double Timestamp;
        public readonly uint Crc32;

        public DiskBufferRecordHeader(int payloadLength, DiskBufferTrack track, UniencSampleKind kind, double timestamp,
            uint crc32)
        {
            PayloadLength = payloadLength;
            Track = track;
            Kind = kind;
            Timestamp = timestamp;
            Crc32 = crc32;
        }
    }

    /// <summary>
    ///     CRC-32 using the IEEE 802.3 polynomial, used to detect a record torn by an abnormal termination.
    ///     System.IO.Hashing is not available on the runtimes this package targets.
    /// </summary>
    internal static class Crc32
    {
        private const uint Polynomial = 0xEDB88320;
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (var i = 0u; i < 256u; i++)
            {
                var value = i;
                for (var bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
                table[i] = value;
            }

            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
                crc = (crc >> 8) ^ Table[(byte)(crc ^ b)];
            return crc ^ 0xFFFFFFFFu;
        }

        public static uint Compute(byte[] data, int offset, int length)
        {
            var crc = 0xFFFFFFFFu;
            for (var i = 0; i < length; i++)
                crc = (crc >> 8) ^ Table[(byte)(crc ^ data[offset + i])];
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
