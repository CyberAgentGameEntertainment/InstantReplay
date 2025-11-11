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
        private double _prevFrameTime;
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

            var deltaTime = time - _prevFrameTime;

            if (deltaTime <= 0) return false;

            _frameTimer += deltaTime;
            _prevFrameTime = time;

            if (_fixedFrameInterval is { } fixedFrameInterval)
            {
                if (_frameTimer < _fixedFrameInterval) return false;
                _frameTimer %= fixedFrameInterval;
            }

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
