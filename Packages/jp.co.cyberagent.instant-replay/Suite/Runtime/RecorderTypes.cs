// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System.Collections.Generic;

namespace InstantReplay.Suite
{
    /// <summary>
    ///     What an <see cref="InstantReplayRecorder" /> is doing.
    /// </summary>
    public enum RecorderState
    {
        /// <summary>Not recording. Buffering is disabled or the recorder has not been activated.</summary>
        Idle,

        /// <summary>Continuously keeping the latest footage so that it can be saved as a replay.</summary>
        Buffering,

        /// <summary>Writing a replay or finalizing an unbounded recording. Buffering resumes when it completes.</summary>
        Exporting,

        /// <summary>Recording to a file until stopped. Buffering is suspended meanwhile.</summary>
        RecordingUnbounded,

        /// <summary>
        ///     Buffering failed repeatedly and was stopped. Enabling buffering again retries. See
        ///     <see cref="InstantReplayRecorder.LastException" />.
        /// </summary>
        Faulted
    }

    /// <summary>
    ///     Snapshot of the recorder for display.
    /// </summary>
    public readonly struct RecorderStatistics
    {
        public RecorderStatistics(double bufferedDurationSeconds, double estimatedCapacitySeconds,
            long bufferUsedBytes, long bufferCapacityBytes, double unboundedElapsedSeconds)
        {
            BufferedDurationSeconds = bufferedDurationSeconds;
            EstimatedCapacitySeconds = estimatedCapacitySeconds;
            BufferUsedBytes = bufferUsedBytes;
            BufferCapacityBytes = bufferCapacityBytes;
            UnboundedElapsedSeconds = unboundedElapsedSeconds;
        }

        /// <summary>Seconds a replay saved now would cover. Zero when not buffering.</summary>
        public double BufferedDurationSeconds { get; }

        /// <summary>
        ///     Estimated number of seconds the buffer can hold. Derived from the actual bytes per second once enough has
        ///     been recorded, and from the bitrates of the active preset before that.
        /// </summary>
        public double EstimatedCapacitySeconds { get; }

        public long BufferUsedBytes { get; }
        public long BufferCapacityBytes { get; }

        /// <summary>Seconds recorded by the running unbounded recording, excluding pauses. Zero otherwise.</summary>
        public double UnboundedElapsedSeconds { get; }
    }

    /// <summary>
    ///     Parameters of <see cref="InstantReplayRecorder.SaveReplayAsync" />.
    /// </summary>
    public sealed class ReplaySaveRequest
    {
        /// <summary>Seconds to save, counted back from now. Null saves the whole buffer.</summary>
        public double? DurationSeconds { get; set; }

        /// <summary>Key-value pairs stored with the recording.</summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; }
    }

    /// <summary>
    ///     Range and thumbnail chosen for a <see cref="ReplayDraft" />, for example by the user in a trim view. Pass it to
    ///     <see cref="ReplayDraft.SaveAsync(ReplaySelection)" /> or <see cref="ReplayDraft.ExportAsync(ReplaySelection, string)" />.
    /// </summary>
    public sealed class ReplaySelection
    {
        public ReplaySelection(double startSeconds, double? endSeconds = null, byte[] thumbnailPng = null)
        {
            StartSeconds = startSeconds;
            EndSeconds = endSeconds;
            ThumbnailPng = thumbnailPng;
        }

        /// <summary>Start time in seconds from the beginning of the draft.</summary>
        public double StartSeconds { get; }

        /// <summary>End time in seconds from the beginning of the draft, exclusive. Null means the end of the draft.</summary>
        public double? EndSeconds { get; }

        /// <summary>PNG-encoded thumbnail, or null when none was chosen.</summary>
        public byte[] ThumbnailPng { get; }
    }

    /// <summary>
    ///     Parameters of <see cref="InstantReplayRecorder.StartUnbounded" />.
    /// </summary>
    public sealed class UnboundedRecordingRequest
    {
        /// <summary>Key-value pairs stored with the recording.</summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; }
    }

    /// <summary>
    ///     Parameters of <see cref="RecordingLibrary.ImportRecoveredAsync" />.
    /// </summary>
    public sealed class RecoveryImportRequest
    {
        /// <summary>Seconds to export, counted back from the end of the recovered footage. Null exports all of it.</summary>
        public double? DurationSeconds { get; set; }

        /// <summary>Key-value pairs stored with the recording.</summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; }
    }
}
