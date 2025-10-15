// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;

namespace InstantReplay
{
    internal class VideoTemporalAdjuster<T> : IPipelineTransform<T, T> where T : struct, IDiscreteTemporalData
    {
        private const double AllowedLag = 0.1;
        private readonly double? _fixedFrameInterval;
        private readonly IRecordingTimeProvider _recordingTimeProvider;
        private bool _disposed;
        private double _frameTimer;
        private double _prevFrameTime;
        private double? _videoTimeDifference;

        public VideoTemporalAdjuster(IRecordingTimeProvider recordingTimeProvider, double? fixedFrameInterval)
        {
            _recordingTimeProvider = recordingTimeProvider;
            _fixedFrameInterval = fixedFrameInterval;
        }

        public bool Transform(T input, out T output)
        {
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
                if (Math.Abs(diff) >= AllowedLag)
                {
                    Debug.LogWarning(
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
