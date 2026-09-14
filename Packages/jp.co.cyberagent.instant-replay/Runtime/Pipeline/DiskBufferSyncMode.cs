// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

namespace InstantReplay
{
    /// <summary>
    ///     Determines how aggressively the disk buffer flushes written data to the storage device.
    /// </summary>
    /// <remarks>
    ///     The distinction that matters is between two failure modes. A process crash — a native fault, an out-of-memory
    ///     kill, or an abort — does not lose data that has already reached the operating system through a write, because
    ///     the kernel owns the page cache and flushes it independently of the process. Power loss and a kernel panic lose
    ///     everything that has not been flushed to the storage device.
    ///     Recovering the footage that precedes a crash, which is the purpose of the disk buffer, targets the first mode.
    ///     Defending against it costs no additional device writes, so the default guards against it without shortening the
    ///     lifespan of flash memory. Guarding against the second mode requires a device flush per record, which multiplies
    ///     the number of erase cycles the storage device performs and is therefore not the default.
    /// </remarks>
    public enum DiskBufferSyncMode
    {
        /// <summary>
        ///     Default. Written data is handed to the operating system after every batch drained from the write queue, and
        ///     flushed to the storage device when a segment is closed, when codec configuration is written, and when the
        ///     manifest is written. Recorded frames survive a process crash. Power loss or a kernel panic loses at most the
        ///     records written since the current segment was opened.
        /// </summary>
        OperatingSystem = 0,

        /// <summary>
        ///     Every record is flushed to the storage device as it is written. Frames survive power loss and a kernel panic,
        ///     at the cost of one device flush per frame. This markedly increases wear on flash memory and is intended for
        ///     diagnosing storage-layer problems rather than for routine use.
        /// </summary>
        EveryRecord = 1
    }
}
