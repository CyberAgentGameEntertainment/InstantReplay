// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace InstantReplay.Suite
{
    /// <summary>
    ///     Footage taken from the buffer by <see cref="InstantReplayRecorder.CaptureReplayAsync" /> that has not been saved
    ///     yet. It can be previewed and then saved to the library over a chosen range, any number of times.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A saved range starts at a video key frame, so the start chosen by the user moves back to the nearest key
    ///         frame at or before it. <see cref="KeyFrameTimestamps" /> lists the possible start points.
    ///     </para>
    ///     <para>
    ///         The draft holds the footage until it is disposed: in memory with the in-memory buffer, and in the session
    ///         directory with the disk buffer. The recorder resumes buffering as soon as the draft is created, so the
    ///         draft and the new buffer coexist and their storage adds up. Dispose the draft once the user has finished
    ///         with it.
    ///     </para>
    ///     <para>Every member must be called on the main thread.</para>
    /// </remarks>
    public sealed class ReplayDraft : IDisposable
    {
        private readonly CapturedReplay _capture;
        private readonly RecordingLibrary _library;
        private bool _disposed;
        private Task<string> _previewTask;

        internal ReplayDraft(RecordingLibrary library, CapturedReplay capture, DateTime capturedAtUtc,
            IReadOnlyDictionary<string, string> metadata)
        {
            _library = library;
            _capture = capture;
            CapturedAtUtc = capturedAtUtc;
            Metadata = metadata;
        }

        /// <summary>Time at which the footage was taken from the buffer, which is the end of the footage.</summary>
        public DateTime CapturedAtUtc { get; }

        /// <summary>Seconds from the first to the last video frame.</summary>
        public double DurationSeconds => _capture.DurationSeconds;

        /// <summary>Times of the video key frames in seconds from the start, in ascending order. The first is zero.</summary>
        public IReadOnlyList<double> KeyFrameTimestamps => _capture.KeyFrameTimestamps;

        /// <summary>Width of the video in pixels.</summary>
        public int Width => _capture.Width;

        /// <summary>Height of the video in pixels.</summary>
        public int Height => _capture.Height;

        /// <summary>Key-value pairs stored with every recording saved from this draft. May be null.</summary>
        public IReadOnlyDictionary<string, string> Metadata { get; }

        public bool IsDisposed => _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _capture.Dispose();

            var preview = _previewTask;
            _previewTask = null;
            if (preview != null)
                preview.ContinueWith(static t =>
                {
                    if (t.Status != TaskStatus.RanToCompletion) return;
                    try
                    {
                        if (File.Exists(t.Result)) File.Delete(t.Result);
                    }
                    catch (Exception ex)
                    {
                        ILogger.LogExceptionCore(ex);
                    }
                }, TaskScheduler.Default);
        }

        /// <summary>
        ///     Returns the time at which a range requested to start at <paramref name="seconds" /> actually starts.
        /// </summary>
        public double GetSaveStart(double seconds)
        {
            return _capture.GetExportStart(seconds);
        }

        /// <summary>
        ///     Writes the whole footage to a temporary file for preview and returns its path. The file is written once
        ///     and deleted when the draft is disposed.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The draft has been disposed.</exception>
        public ValueTask<string> GetPreviewPathAsync()
        {
            ThrowIfDisposed();
            if (_previewTask == null || _previewTask.IsFaulted || _previewTask.IsCanceled)
                _previewTask = _capture.ExportAsync().AsTask();
            return new ValueTask<string>(_previewTask);
        }

        /// <summary>
        ///     Saves the footage between two times to the library as a <see cref="RecordingSource.Replay" /> recording.
        /// </summary>
        /// <param name="startSeconds">Start time in seconds. It moves back to <see cref="GetSaveStart" />.</param>
        /// <param name="endSeconds">End time in seconds, exclusive. Null saves to the end.</param>
        /// <param name="thumbnailPng">PNG-encoded thumbnail stored with the recording, or null.</param>
        /// <exception cref="ArgumentException">The range contains no video frame.</exception>
        /// <exception cref="ObjectDisposedException">The draft has been disposed.</exception>
        public async ValueTask<RecordingEntry> SaveAsync(double startSeconds = 0, double? endSeconds = null,
            byte[] thumbnailPng = null)
        {
            ThrowIfDisposed();

            var start = GetSaveStart(startSeconds);
            var end = Math.Min(endSeconds ?? DurationSeconds, DurationSeconds);
            var path = _library.CreateOutputPath(RecordingSource.Replay);
            var completed = false;
            try
            {
                await _capture.ExportAsync(startSeconds, endSeconds, path);
                completed = true;
                return _library.Add(path, RecordingSource.Replay, CapturedAtUtc, Math.Max(0, end - start), Metadata,
                    thumbnailPng);
            }
            finally
            {
                if (!completed) _library.Abandon(path);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReplayDraft));
        }
    }
}
