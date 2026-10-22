// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace InstantReplay.Suite
{
    /// <summary>
    ///     A recording started by <see cref="InstantReplayRecorder.StartUnbounded" /> that continues until it is stopped.
    /// </summary>
    public sealed class UnboundedRecording
    {
        private readonly InstantReplayRecorder _recorder;
        private readonly Stopwatch _stopwatch = new();
        private Exception _failure;
        private Task<RecordingEntry> _stopTask;

        internal UnboundedRecording(InstantReplayRecorder recorder, string outputPath, DateTime startedAtUtc,
            IReadOnlyDictionary<string, string> metadata, bool paused)
        {
            _recorder = recorder;
            OutputPath = outputPath;
            StartedAtUtc = startedAtUtc;
            Metadata = metadata;
            if (!paused) _stopwatch.Start();
        }

        /// <summary>Absolute path of the file being written.</summary>
        public string OutputPath { get; }

        public DateTime StartedAtUtc { get; }

        /// <summary>Seconds recorded so far, excluding the time the recorder was paused.</summary>
        public double ElapsedSeconds => _stopwatch.Elapsed.TotalSeconds;

        /// <summary>Whether the recording is still running, that is, neither stopped nor failed.</summary>
        public bool IsRecording => _stopTask == null && _failure == null;

        internal IReadOnlyDictionary<string, string> Metadata { get; }

        /// <summary>
        ///     Stops the recording and finalizes the file. Calling it again returns the same result. Buffering resumes
        ///     afterwards if it is enabled. Call it on the main thread.
        /// </summary>
        public ValueTask<RecordingEntry> StopAsync()
        {
            _stopTask ??= _failure != null
                ? Task.FromException<RecordingEntry>(_failure)
                : _recorder.StopUnboundedAsync(this);
            return new ValueTask<RecordingEntry>(_stopTask);
        }

        internal void SetPaused(bool paused)
        {
            if (!IsRecording) return;
            if (paused) _stopwatch.Stop();
            else _stopwatch.Start();
        }

        internal void MarkStopped()
        {
            _stopwatch.Stop();
        }

        internal void Fail(Exception exception)
        {
            _stopwatch.Stop();
            _failure ??= exception;
        }
    }
}
