// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

namespace InstantReplay
{
    /// <summary>
    ///     Snapshot of the encoded frame buffer of a <see cref="RealtimeInstantReplaySession" />.
    /// </summary>
    public readonly struct RecordingBufferStatistics
    {
        /// <summary>
        ///     Duration in seconds that an export of the whole buffer would cover, measured from the earliest retained video
        ///     key frame to the latest video frame. Zero when the buffer holds no key frame yet.
        /// </summary>
        public double BufferedDurationSeconds { get; }

        /// <summary>
        ///     Bytes currently occupied by the buffer: memory for the in-memory buffer, or the size of the session
        ///     directory for the disk buffer.
        /// </summary>
        public long UsedBytes { get; }

        /// <summary>
        ///     Upper bound of <see cref="UsedBytes" />:
        ///     <see cref="RealtimeEncodingOptions.MaxMemoryUsageBytesForCompressedFrames" />
        ///     for the in-memory buffer, or <see cref="DiskBufferOptions.MaxDiskUsageBytes" /> for the disk buffer.
        /// </summary>
        public long CapacityBytes { get; }

        /// <summary>
        ///     Whether the buffer is kept on disk.
        /// </summary>
        public bool IsDiskBuffer { get; }

        public RecordingBufferStatistics(double bufferedDurationSeconds, long usedBytes, long capacityBytes,
            bool isDiskBuffer)
        {
            BufferedDurationSeconds = bufferedDurationSeconds;
            UsedBytes = usedBytes;
            CapacityBytes = capacityBytes;
            IsDiskBuffer = isDiskBuffer;
        }
    }
}
