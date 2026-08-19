// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;

namespace InstantReplay
{
    internal class VideoTemporalAdjuster<T> : IPipelineTransform<T, T> where T : struct, IDiscreteTemporalData
    {
        private const double DefaultAllowedLag = 0.1;
        private readonly double _allowedLag;
        private readonly double? _fixedFrameInterval;
        private readonly IRecordingTimeProvider _recordingTimeProvider;
        private bool _disposed;
        private double _frameTimer;
        private double? _prevFrameTime;
        private double? _prevOutputTime;
        private double? _videoTimeDifference;

        public VideoTemporalAdjuster(IRecordingTimeProvider recordingTimeProvider, double? fixedFrameInterval,
            double? allowedLag = null)
        {
            _recordingTimeProvider = recordingTimeProvider;
            _fixedFrameInterval = fixedFrameInterval;
            if (allowedLag is < 0)
                throw new ArgumentOutOfRangeException(nameof(allowedLag), "allowedLag must be non-negative.");
            _allowedLag = allowedLag ?? DefaultAllowedLag;
        }

        public bool WillAcceptWhenNextWont => false;

        public bool Transform(T input, out T output, bool willAcceptedByNextInput)
        {
            if (!willAcceptedByNextInput)
            {
                output = default;
                return false;
            }

            var realTime = _recordingTimeProvider.Now;

            output = default;

            if (_disposed || _recordingTimeProvider.IsPaused)
                return false;

            var time = input.Timestamp;

            if (_prevFrameTime is { } prevFrameTime)
            {
                var deltaTime = time - prevFrameTime;

                if (deltaTime <= 0) return false;

                _frameTimer += deltaTime;
            }

            if (_fixedFrameInterval is { } fixedFrameInterval)
            {
                if (_prevFrameTime.HasValue && _frameTimer < _fixedFrameInterval)
                {
                    // The elapsed time of this frame has already been accumulated into _frameTimer,
                    // so advance _prevFrameTime to avoid counting it again on the next frame.
                    _prevFrameTime = time;
                    return false;
                }

                _frameTimer %= fixedFrameInterval;
            }

            _prevFrameTime = time;

            // adjust timestamp
            if (!_videoTimeDifference.HasValue)
            {
                _videoTimeDifference = time - realTime;
                time = realTime;
            }
            else
            {
                var expectedTime = realTime + _videoTimeDifference.Value;
                var diff = time - expectedTime;
                if (Math.Abs(diff) >= _allowedLag)
                {
                    ILogger.LogWarningCore(
                        "Video timestamp adjusted. The timestamp IFrameProvider provided may not be realtime.");
                    _videoTimeDifference = time - realTime;
                    time = realTime;
                }
                else
                {
                    time -= _videoTimeDifference.Value;
                }
            }

            time -= _recordingTimeProvider.TotalPausedDuration;

            // Ensure the output timestamp is strictly monotonically increasing AND spaced by a minimum
            // interval. The timestamp rebase above (time = realTime) is based on a different clock than the
            // input timestamp, so under framerate jitter it can place a frame at (or just after) the
            // previously emitted one. Frames spaced sub-millisecond apart make the downstream muxer
            // (AVAssetWriter) fail asynchronously, so drop frames that do not advance by at least
            // minOutputInterval.
            var minOutputInterval = _fixedFrameInterval is { } ffi ? ffi * 0.5 : 0.0;
            if (_prevOutputTime is { } prevOutputTime && time <= prevOutputTime + minOutputInterval)
            {
                output = default;
                return false;
            }

            _prevOutputTime = time;
            output = input;
            output.Timestamp = time;
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
