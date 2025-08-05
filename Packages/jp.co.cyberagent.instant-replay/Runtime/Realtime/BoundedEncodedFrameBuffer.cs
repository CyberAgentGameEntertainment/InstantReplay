using System;
using System.Collections.Generic;
using System.Threading;
using UniEnc;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Circular buffer for encoded frames with memory bounds.
    /// </summary>
    public class BoundedEncodedFrameBuffer : IDisposable
    {
        [ThreadStatic] private static List<EncodedFrame> _tempFrames;
        private readonly List<EncodedFrame> _audioMetadata = new();
        private readonly Queue<EncodedFrame> _audioQueue;
        private readonly long _maxMemoryBytes;

        private readonly List<EncodedFrame> _videoMetadata = new();
        private readonly Queue<EncodedFrame> _videoQueue;
        private long _currentMemoryUsage;
        private bool _disposed;

        public BoundedEncodedFrameBuffer(long maxMemoryBytes)
        {
            _maxMemoryBytes = maxMemoryBytes;
            _videoQueue = new Queue<EncodedFrame>();
            _audioQueue = new Queue<EncodedFrame>();
            _currentMemoryUsage = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_videoQueue)
            lock (_audioQueue)
            {
                if (_videoQueue != null)
                    foreach (var frame in _videoQueue)
                        frame.Dispose();

                if (_audioQueue != null)
                    foreach (var frame in _audioQueue)
                        frame.Dispose();

                _videoQueue.Clear();
                _audioQueue.Clear();
            }
        }

        /// <summary>
        ///     Adds a video frame to the buffer.
        /// </summary>
        public bool TryAddVideoFrame(EncodedFrame frame)
        {
            if (_disposed) return false;

            var frameSize = frame.Data.Length;
            EnsureMemoryCapacity(frameSize);

            lock (_videoQueue)
            {
                if (frame.Kind == DataKind.Metadata)
                    _videoMetadata.Add(frame);
                else
                    _videoQueue.Enqueue(frame);
            }

            Interlocked.Add(ref _currentMemoryUsage, frameSize);
            return true;
        }

        /// <summary>
        ///     Adds an audio frame to the buffer.
        /// </summary>
        public bool TryAddAudioFrame(EncodedFrame frame)
        {
            if (_disposed) return false;

            var frameSize = frame.Data.Length;
            EnsureMemoryCapacity(frameSize);

            lock (_audioQueue)
            {
                if (frame.Kind == DataKind.Metadata)
                    _audioMetadata.Add(frame);
                else
                    _audioQueue.Enqueue(frame);
            }

            Interlocked.Add(ref _currentMemoryUsage, frameSize);
            return true;
        }

        /// <summary>
        ///     Gets frames for the specified duration, adjusted to start from a keyframe.
        /// </summary>
        public void GetFramesForDuration(double? durationSeconds, out ReadOnlyMemory<EncodedFrame> videoFrames,
            out ReadOnlyMemory<EncodedFrame> audioFrames)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BoundedEncodedFrameBuffer));

            Memory<EncodedFrame> unprocessedVideoFrames;
            Memory<EncodedFrame> unprocessedAudioFrames;
            Memory<EncodedFrame> videoMetadata;
            Memory<EncodedFrame> audioMetadata;
            lock (_videoQueue)
            lock (_audioQueue)
            {
                unprocessedVideoFrames = _videoQueue.ToArray();
                unprocessedAudioFrames = _audioQueue.ToArray();
                _videoQueue.Clear();
                _audioQueue.Clear();

                videoMetadata = _videoMetadata.ToArray();
                audioMetadata = _audioMetadata.ToArray();
                _videoMetadata.Clear();
                _audioMetadata.Clear();
            }

            try
            {
                if (unprocessedVideoFrames.Length == 0)
                {
                    videoFrames = default;
                    audioFrames = default;
                    return;
                }

                // find keyframe
                var argMinTimespan = -1;
                var latest = unprocessedVideoFrames.Span[^1];
                if (durationSeconds is { } durationSecondsValue)
                {
                    // TODO: binary search
                    var expectedStartTime = latest.Timestamp - durationSecondsValue;
                    var minTimespan = double.MaxValue;
                    for (var i = 0; i < unprocessedVideoFrames.Length; i++)
                    {
                        if (unprocessedVideoFrames.Span[i].Kind != DataKind.Key) continue;
                        var timespan = Math.Abs(unprocessedVideoFrames.Span[i].Timestamp - expectedStartTime);
                        if (timespan >= minTimespan) continue;
                        minTimespan = timespan;
                        argMinTimespan = i;
                    }
                }
                else
                {
                    for (var i = 0; i < unprocessedVideoFrames.Length; i++)
                    {
                        if (unprocessedVideoFrames.Span[i].Kind != DataKind.Key) continue;
                        argMinTimespan = i;
                        break;
                    }
                }

                if (argMinTimespan == -1)
                {
                    // No keyframe found, return empty arrays
                    videoFrames = default;
                    audioFrames = default;
                    return;
                }

                // find audio start index
                int argMinAudioTimespan;
                if (unprocessedAudioFrames.Length == 0)
                {
                    argMinAudioTimespan = 0;
                }
                else
                {
                    var actualDuration = latest.Timestamp - unprocessedVideoFrames.Span[argMinTimespan].Timestamp;
                    var expectedAudioStartTime = unprocessedAudioFrames.Span[^1].Timestamp - actualDuration;

                    var minAudioTimespan = double.MaxValue;
                    argMinAudioTimespan = -1;
                    for (var i = 0; i < unprocessedAudioFrames.Length; i++)
                    {
                        var timespan = Math.Abs(unprocessedAudioFrames.Span[i].Timestamp - expectedAudioStartTime);
                        if (timespan >= minAudioTimespan) continue;
                        minAudioTimespan = timespan;
                        argMinAudioTimespan = i;
                    }
                }

                // split

                var videoFramesSpan = unprocessedVideoFrames[argMinTimespan..];
                var audioFramesSpan = unprocessedAudioFrames[argMinAudioTimespan..];
                unprocessedVideoFrames = unprocessedVideoFrames[..argMinTimespan];
                unprocessedAudioFrames = unprocessedAudioFrames[..argMinAudioTimespan];

                // adjust timestamps
                var videoStartTime = videoFramesSpan.Span[0].Timestamp;
                for (var i = 0; i < videoFramesSpan.Length; i++)
                {
                    ref var frame = ref videoFramesSpan.Span[i];
                    frame = frame.WithTimestamp(frame.Timestamp - videoStartTime);
                }

                var audioStartTime = audioFramesSpan.Span[0].Timestamp;
                for (var i = 0; i < audioFramesSpan.Length; i++)
                {
                    ref var frame = ref audioFramesSpan.Span[i];
                    frame = frame.WithTimestamp(frame.Timestamp - audioStartTime);
                }

                // concat metadata
                if (videoMetadata.Length > 0)
                {
                    var newVideoFrames = new EncodedFrame[videoFramesSpan.Length + videoMetadata.Length];
                    videoMetadata.Span.CopyTo(newVideoFrames);
                    videoFramesSpan.Span.CopyTo(newVideoFrames.AsSpan(videoMetadata.Length));
                    videoFramesSpan = newVideoFrames.AsMemory();
                }

                if (audioMetadata.Length > 0)
                {
                    var newAudioFrames = new EncodedFrame[audioFramesSpan.Length + audioMetadata.Length];
                    audioMetadata.Span.CopyTo(newAudioFrames);
                    audioFramesSpan.Span.CopyTo(newAudioFrames.AsSpan(audioMetadata.Length));
                    audioFramesSpan = newAudioFrames.AsMemory();
                }

                videoFrames = videoFramesSpan;
                audioFrames = audioFramesSpan;
            }
            finally
            {
                foreach (var frame in unprocessedVideoFrames.Span)
                    try
                    {
                        frame.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }

                foreach (var frame in unprocessedAudioFrames.Span)
                    try
                    {
                        frame.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
            }
        }

        private void EnsureMemoryCapacity(int requiredBytes)
        {
            if (_currentMemoryUsage + requiredBytes <= _maxMemoryBytes)
                return;

            var framesToDispose = _tempFrames ??= new List<EncodedFrame>();
            framesToDispose.Clear();

            lock (_videoQueue)
            lock (_audioQueue)
            {
                var needToBeFreed = _currentMemoryUsage + requiredBytes - _maxMemoryBytes;
                if (needToBeFreed <= 0) return;

                var freed = 0;
                while (freed < needToBeFreed)
                    if (_videoQueue.TryPeek(out var videoFrame) &&
                        _audioQueue.TryPeek(out var audioFrame))
                    {
                        if (videoFrame.Timestamp <= audioFrame.Timestamp)
                        {
                            framesToDispose.Add(_videoQueue.Dequeue());
                            freed += videoFrame.Data.Length;
                        }
                        else
                        {
                            framesToDispose.Add(_audioQueue.Dequeue());
                            freed += audioFrame.Data.Length;
                        }
                    }
                    else if (_videoQueue.TryDequeue(out var vFrame))
                    {
                        framesToDispose.Add(vFrame);
                        freed += vFrame.Data.Length;
                    }
                    else if (_audioQueue.TryDequeue(out var aFrame))
                    {
                        framesToDispose.Add(aFrame);
                        freed += aFrame.Data.Length;
                    }
                    else
                    {
                        break;
                    }

                Interlocked.Add(ref _currentMemoryUsage, -freed);
            }

            foreach (var frame in framesToDispose)
                try
                {
                    frame.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }

            framesToDispose.Clear();
        }
    }
}
