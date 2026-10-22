// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UniEnc;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Encoded footage taken out of a <see cref="RealtimeInstantReplaySession" /> that can be exported any number of
    ///     times, each time over a different range. Used to preview a replay and then save only part of it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An export can start only at a video key frame, because the frames before it are needed to decode the
    ///         frames that follow. <see cref="KeyFrameTimestamps" /> lists the possible start points, and the
    ///         granularity of trimming therefore equals the key frame interval of the encoder.
    ///     </para>
    ///     <para>
    ///         With the in-memory buffer the frames stay in memory until this object is disposed. With the disk buffer
    ///         only the location of every frame is held in memory, and the session directory is kept until this object is
    ///         disposed. Recovery does not offer the directory while it is held. If the application terminates before
    ///         disposal, the directory becomes recoverable like any other disk buffer.
    ///     </para>
    ///     <para>Members are thread-safe. Disposal waits for running exports to finish before releasing the frames.</para>
    /// </remarks>
    public sealed class CapturedReplay : IDisposable
    {
        private readonly AudioEncoderOptions _audioOptions;
        private readonly CapturedFrames _frames;
        private readonly double[] _keyFrameTimestamps;
        private readonly object _lock = new();
        private readonly VideoEncoderOptions _videoOptions;
        private int _activeExports;
        private bool _disposeRequested;
        private bool _released;

        internal CapturedReplay(CapturedFrames frames, in VideoEncoderOptions videoOptions,
            in AudioEncoderOptions audioOptions)
        {
            _frames = frames;
            _videoOptions = videoOptions;
            _audioOptions = audioOptions;

            var keyFrames = new List<double>();
            var video = frames.Video;
            for (var i = 0; i < video.Count; i++)
                if (video[i].Kind == UniencSampleKind.Key)
                    keyFrames.Add(video[i].Timestamp);
            _keyFrameTimestamps = keyFrames.ToArray();
            DurationSeconds = frames.GetDurationSeconds();
        }

        /// <summary>Seconds from the first to the latest video frame.</summary>
        public double DurationSeconds { get; }

        /// <summary>
        ///     Times of the video key frames in seconds from the start, in ascending order. The first is zero.
        /// </summary>
        public IReadOnlyList<double> KeyFrameTimestamps => _keyFrameTimestamps;

        /// <summary>Width of the video in pixels.</summary>
        public int Width => (int)_videoOptions.Width;

        /// <summary>Height of the video in pixels.</summary>
        public int Height => (int)_videoOptions.Height;

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposeRequested) return;
                _disposeRequested = true;
                if (_activeExports > 0) return;
                _released = true;
            }

            _frames.Dispose();
        }

        /// <summary>
        ///     Returns the time of the latest key frame at or before <paramref name="seconds" />, which is where an export
        ///     requested to start at that time actually starts.
        /// </summary>
        public double GetExportStart(double seconds)
        {
            var result = _keyFrameTimestamps.Length > 0 ? _keyFrameTimestamps[0] : 0;
            foreach (var timestamp in _keyFrameTimestamps)
            {
                if (timestamp > seconds + CapturedFrames.Epsilon) break;
                result = timestamp;
            }

            return result;
        }

        /// <summary>
        ///     Writes the footage between two times to an MP4 file. The start moves back to the key frame returned by
        ///     <see cref="GetExportStart" />.
        /// </summary>
        /// <param name="startSeconds">Start time in seconds from the beginning of the capture.</param>
        /// <param name="endSeconds">End time in seconds, exclusive. Null exports to the end.</param>
        /// <param name="outputPath">Output file path. A path in the temporary cache directory is generated when null.</param>
        /// <returns>Path to the exported file.</returns>
        /// <exception cref="ArgumentException">The range contains no video frame.</exception>
        /// <exception cref="ObjectDisposedException">The capture has been disposed.</exception>
        public async ValueTask<string> ExportAsync(double startSeconds = 0, double? endSeconds = null,
            string outputPath = null)
        {
            lock (_lock)
            {
                if (_disposeRequested) throw new ObjectDisposedException(nameof(CapturedReplay));
                _activeExports++;
            }

            try
            {
                _frames.ResolveRange(startSeconds, endSeconds, out var videoStart, out var videoEnd,
                    out var audioStart, out var audioEnd);

                if (string.IsNullOrEmpty(outputPath))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    outputPath = Path.Combine(Application.temporaryCachePath, $"InstantReplay_{timestamp}.mp4");
                }

                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                using var encodingSystem = new EncodingSystem(_videoOptions, _audioOptions);
                using var muxer = encodingSystem.CreateMuxer(outputPath);
                using var selection = _frames.CreateSelection(videoStart, videoEnd, audioStart, audioEnd);
                await EncodedFrameMuxer.MuxAsync(muxer, selection.VideoFrames, selection.AudioFrames)
                    .ConfigureAwait(EncodedFrameMuxer.ContinueOnCapturedContext);
                return outputPath;
            }
            finally
            {
                bool release;
                lock (_lock)
                {
                    _activeExports--;
                    release = _disposeRequested && _activeExports == 0 && !_released;
                    if (release) _released = true;
                }

                if (release) _frames.Dispose();
            }
        }
    }
}
