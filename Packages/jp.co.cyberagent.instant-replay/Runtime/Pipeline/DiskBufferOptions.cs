// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Configuration of the opt-in disk buffer of <see cref="RealtimeInstantReplaySession" />.
    ///     Enabling it lowers memory pressure and makes the footage leading up to a crash recoverable, at the cost of
    ///     continuous writes to storage.
    /// </summary>
    /// <remarks>
    ///     Because recording continuously to storage shortens the lifespan of flash memory, the disk buffer is disabled by
    ///     default and is intended primarily for development and quality-assurance builds.
    /// </remarks>
    public struct DiskBufferOptions
    {
        /// <summary>
        ///     Directory that holds the session directories. When null or empty,
        ///     <c>Application.temporaryCachePath/InstantReplay/DiskBuffer</c> is used.
        /// </summary>
        public string Directory { get; set; }

        /// <summary>
        ///     Hard upper bound of the size of one session directory, covering the manifest, the codec configuration file,
        ///     and every segment file.
        /// </summary>
        /// <remarks>
        ///     This is a bound, not a target. Space is reserved before each record is written, and the reservation deletes
        ///     as many of the oldest segments as it needs, so the directory never exceeds this size at any instant. When the
        ///     bound cannot be met even after every evictable segment has been deleted, records are dropped rather than
        ///     written. Setting a value smaller than a few times <see cref="MaxSegmentBytes" /> therefore degrades the
        ///     recording rather than the retention.
        /// </remarks>
        public long MaxDiskUsageBytes { get; set; }

        /// <summary>
        ///     Target duration of one segment file in seconds. A segment is closed at the first video key frame after the
        ///     target is reached, so that every segment begins at a key frame and discarding one never leaves a partial
        ///     group of pictures behind.
        /// </summary>
        public double SegmentDuration { get; set; }

        /// <summary>
        ///     Upper bound of the size of one segment file. Reaching it closes the segment at the next video key frame even
        ///     when <see cref="SegmentDuration" /> has not elapsed.
        /// </summary>
        public long MaxSegmentBytes { get; set; }

        /// <summary>
        ///     Upper bound of the total payload size waiting in the write queue. Frames arriving while the queue is full are
        ///     dropped rather than blocking the encoder.
        /// </summary>
        public long MaxPendingWriteBytes { get; set; }

        /// <summary>
        ///     Whether the session directory is kept when the session is disposed normally. When false, which is the
        ///     default, the directory is deleted, so that every directory left behind denotes an abnormal termination.
        /// </summary>
        public bool RetainOnDispose { get; set; }

        /// <summary>
        ///     Flush policy. See <see cref="DiskBufferSyncMode" /> for the trade-off between durability and wear on flash
        ///     memory.
        /// </summary>
        public DiskBufferSyncMode SyncMode { get; set; }

        public static ref readonly DiskBufferOptions Default => ref DefaultValue;

        private static readonly DiskBufferOptions DefaultValue =
            new()
            {
                Directory = null,
                MaxDiskUsageBytes = 256L * 1024 * 1024, // 256 MiB
                SegmentDuration = 5.0,
                MaxSegmentBytes = 8L * 1024 * 1024, // 8 MiB
                MaxPendingWriteBytes = 4L * 1024 * 1024, // 4 MiB
                RetainOnDispose = false,
                SyncMode = DiskBufferSyncMode.OperatingSystem
            };

        /// <summary>
        ///     Smallest bound that still leaves room for the manifest, the codec configuration, and a few segments.
        /// </summary>
        public const long MinimumDiskUsageBytes = 4L * 1024 * 1024;

        /// <summary>
        ///     Returns the configured root directory, or the default one when none was specified.
        /// </summary>
        public string ResolveDirectory()
        {
            return string.IsNullOrEmpty(Directory) ? GetDefaultDirectory() : Directory;
        }

        /// <summary>
        ///     Directory used when <see cref="Directory" /> is not specified. It is writable on every supported platform,
        ///     is excluded from backup on iOS, and is not visible to the user.
        /// </summary>
        public static string GetDefaultDirectory()
        {
            return Path.Combine(Application.temporaryCachePath, "InstantReplay", "DiskBuffer");
        }

        internal void Validate()
        {
            if (MaxDiskUsageBytes < MinimumDiskUsageBytes)
                throw new ArgumentOutOfRangeException(nameof(MaxDiskUsageBytes),
                    $"MaxDiskUsageBytes must be at least {MinimumDiskUsageBytes} bytes.");

            if (SegmentDuration <= 0)
                throw new ArgumentOutOfRangeException(nameof(SegmentDuration),
                    "SegmentDuration must be greater than zero.");

            if (MaxSegmentBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxSegmentBytes),
                    "MaxSegmentBytes must be greater than zero.");

            if (MaxSegmentBytes > MaxDiskUsageBytes)
                throw new ArgumentOutOfRangeException(nameof(MaxSegmentBytes),
                    "MaxSegmentBytes must not exceed MaxDiskUsageBytes.");

            if (MaxPendingWriteBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxPendingWriteBytes),
                    "MaxPendingWriteBytes must be greater than zero.");
        }
    }
}
