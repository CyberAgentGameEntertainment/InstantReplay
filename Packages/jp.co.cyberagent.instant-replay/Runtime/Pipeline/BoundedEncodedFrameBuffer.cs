// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    /// <summary>
    ///     Circular buffer for encoded frames with memory bounds.
    /// </summary>
    internal class BoundedEncodedFrameBuffer : IEncodedFrameBuffer
    {
        [ThreadStatic] private static List<EncodedFrame> _tempFrames;
        private readonly List<EncodedFrame> _audioMetadata = new();
        private readonly Queue<EncodedFrame> _audioQueue;
        private readonly long _maxMemoryBytes;

        private readonly List<EncodedFrame> _videoMetadata = new();
        private readonly Queue<EncodedFrame> _videoQueue;
        private long _currentMemoryUsage;
        private bool _disposed;
        private double? _videoQueueLatestTimestamp;

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
                foreach (var frame in _videoQueue)
                    frame.Dispose();

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
                if (frame.Kind == UniencSampleKind.Metadata)
                {
                    _videoMetadata.Add(frame);
                }
                else
                {
                    // MediaCodec (Android) may produce out-of-order frame with timestamp=0 at the end of stream.
                    if (_videoQueueLatestTimestamp is not { } videoQueueLatestTimestamp ||
                        frame.Timestamp >= videoQueueLatestTimestamp)
                        // in-order frame
                        _videoQueueLatestTimestamp = frame.Timestamp;
                    _videoQueue.Enqueue(frame);
                }
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
                if (frame.Kind == UniencSampleKind.Metadata)
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
        public ValueTask<EncodedFrameSelection> GetFramesForDurationAsync(double? durationSeconds)
        {
            GetFramesForDuration(durationSeconds, out var videoFrames, out var audioFrames);
            return new ValueTask<EncodedFrameSelection>(new EncodedFrameSelection(videoFrames, audioFrames));
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
                var latest = _videoQueueLatestTimestamp ?? 0;

                if (!EncodedFrameSelector.TrySelect(
                        EncodedFrameSelector.ToDescriptors(unprocessedVideoFrames.Span),
                        EncodedFrameSelector.ToDescriptors(unprocessedAudioFrames.Span),
                        latest, durationSeconds, out var argMinTimespan, out var argMinAudioTimespan))
                {
                    // Nothing muxable: either no frame at all, or no keyframe to start from.
                    // The metadata frames were taken out of their lists above, so they are released here.
                    EncodedFrameSelector.DisposeAll(videoMetadata.Span, ILogger.LogExceptionCore);
                    EncodedFrameSelector.DisposeAll(audioMetadata.Span, ILogger.LogExceptionCore);
                    videoFrames = default;
                    audioFrames = default;
                    return;
                }

                // split

                var videoFramesSpan = unprocessedVideoFrames[argMinTimespan..];
                var audioFramesSpan = unprocessedAudioFrames[argMinAudioTimespan..];
                unprocessedVideoFrames = unprocessedVideoFrames[..argMinTimespan];
                unprocessedAudioFrames = unprocessedAudioFrames[..argMinAudioTimespan];

                // adjust timestamps
                EncodedFrameSelector.RebaseTimestamps(videoFramesSpan.Span);
                EncodedFrameSelector.RebaseTimestamps(audioFramesSpan.Span);

                // concat metadata
                videoFramesSpan = EncodedFrameSelector.PrependMetadata(videoFramesSpan, videoMetadata);
                audioFramesSpan = EncodedFrameSelector.PrependMetadata(audioFramesSpan, audioMetadata);

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
                        ILogger.LogExceptionCore(ex);
                    }

                foreach (var frame in unprocessedAudioFrames.Span)
                    try
                    {
                        frame.Dispose();
                    }
                    catch (Exception ex)
                    {
                        ILogger.LogExceptionCore(ex);
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
                    ILogger.LogExceptionCore(ex);
                }

            framesToDispose.Clear();
        }
    }
}
