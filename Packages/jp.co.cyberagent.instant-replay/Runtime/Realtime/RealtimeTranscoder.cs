using System;
using System.IO;
using System.Threading.Tasks;
using UniEnc;
using Unity.Collections;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Realtime transcoder that encodes frames as they arrive and maintains a circular buffer.
    /// </summary>
    public class RealtimeTranscoder : IDisposable
    {
        private readonly Task _audioTransferTask;
        private readonly object _lock = new();
        private readonly RealtimeEncodingOptions _options;
        private readonly Task _videoTransferTask;
        private AudioEncoder _audioEncoder;
        private bool _disposed;
        private EncodingSystem _encodingSystem;
        private BoundedEncodedFrameBuffer _frameBuffer;
        private VideoEncoder _videoEncoder;

        /// <summary>
        ///     Creates a new RealtimeTranscoder with the specified options.
        /// </summary>
        public RealtimeTranscoder(RealtimeEncodingOptions options)
        {
            _options = options;

            InitializeEncodingSystem();
            _videoTransferTask = TransferVideoSamplesAsync();
            _audioTransferTask = TransferAudioSamplesAsync();
        }

        /// <summary>
        ///     Disposes all resources.
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _frameBuffer?.Dispose();
                    _videoEncoder?.Dispose();
                    _audioEncoder?.Dispose();
                    _encodingSystem?.Dispose();
                    _disposed = true;
                }
            }
        }

        /// <summary>
        ///     Pushes a video frame for encoding.
        /// </summary>
        public async ValueTask PushVideoFrameAsync(NativeArray<byte> frameData, uint width, uint height,
            double timestamp)
        {
            ThrowIfDisposed();

            if (frameData.Length == 0)
                throw new ArgumentException("Frame data cannot be empty", nameof(frameData));

            await _videoEncoder.PushFrameAsync(frameData, width, height, timestamp).ConfigureAwait(false);
        }

        /// <summary>
        ///     Pushes audio samples for encoding.
        /// </summary>
        public async ValueTask PushAudioSamplesAsync(ReadOnlyMemory<short> audioData, double timestamp)
        {
            ThrowIfDisposed();

            if (audioData.IsEmpty)
                throw new ArgumentException("Audio data cannot be empty", nameof(audioData));

            // Push samples to audio encoder
            await _audioEncoder.PushSamplesAsync(audioData, (ulong)(timestamp * _options.AudioOptions.SampleRate))
                .ConfigureAwait(false);
        }

        private async Task TransferVideoSamplesAsync()
        {
            try
            {
                do
                {
                    // Try to pull encoded frame
                    var encodedFrame = await _videoEncoder.PullFrameAsync().ConfigureAwait(false);

                    if (encodedFrame.Data.IsEmpty)
                        // end
                        return;

                    // Add to circular buffer
                    if (!_frameBuffer.TryAddVideoFrame(encodedFrame))
                        encodedFrame.Dispose();
                } while (true);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private async Task TransferAudioSamplesAsync()
        {
            try
            {
                do
                {
                    // Try to pull encoded frame
                    var encodedFrame = await _audioEncoder.PullFrameAsync().ConfigureAwait(false);

                    if (encodedFrame.Data.IsEmpty)
                        // end
                        return;

                    // Add to circular buffer
                    if (!_frameBuffer.TryAddAudioFrame(encodedFrame))
                        encodedFrame.Dispose();
                } while (true);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        /// <summary>
        ///     Exports the last N seconds to a file.
        /// </summary>
        public async Task<string> ExportLastSecondsAsync(double seconds, string outputPath)
        {
            ThrowIfDisposed();

            if (seconds <= 0)
                throw new ArgumentException("Duration must be positive", nameof(seconds));

            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentException("Output path cannot be empty", nameof(outputPath));

            // Ensure output directory exists
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            // Flush all encoders and wait for all frames to be encoded
            await FlushEncodersAsync().ConfigureAwait(false);

            // Create a temporary muxer for this export
            using var muxer = _encodingSystem.CreateMuxer(outputPath);

            // Get frames for the requested duration
            _frameBuffer.GetFramesForDuration(seconds, out var videoFrames, out var audioFrames);

            // Mux the segment
            await MuxSegmentAsync(muxer, videoFrames, audioFrames).ConfigureAwait(false);

            return outputPath;
        }

        /// <summary>
        ///     Flushes all encoders by completing input and waiting for all frames to be encoded.
        /// </summary>
        private async Task FlushEncodersAsync()
        {
            // Complete input to signal end of stream
            _videoEncoder?.CompleteInput();
            _audioEncoder?.CompleteInput();

            // Wait for transfer tasks to complete
            // They will exit when encoders return empty frames
            if (_videoTransferTask != null)
            {
                await _videoTransferTask.ConfigureAwait(false);
            }

            if (_audioTransferTask != null)
            {
                await _audioTransferTask.ConfigureAwait(false);
            }
        }

        private void InitializeEncodingSystem()
        {
            lock (_lock)
            {
                if (_disposed) return;

                _encodingSystem = new EncodingSystem(_options.VideoOptions, _options.AudioOptions);
                _videoEncoder = _encodingSystem.CreateVideoEncoder();
                _audioEncoder = _encodingSystem.CreateAudioEncoder();

                _frameBuffer = new BoundedEncodedFrameBuffer(_options.MaxMemoryUsageBytes);
            }
        }

        private async Task MuxSegmentAsync(Muxer muxer, ReadOnlyMemory<EncodedFrame> videoFrames,
            ReadOnlyMemory<EncodedFrame> audioFrames)
        {
            // Process video and audio independently
            var videoTask = Task.Run(async () =>
            {
                Exception exception = null;
                for (var i = 0; i < videoFrames.Span.Length; i++)
                {
                    var frame = videoFrames.Span[i];
                    try
                    {
                        using (frame)
                        {
                            if (exception == null)
                                await muxer.PushVideoDataAsync(frame.Data);
                        }
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }
                }

                if (exception != null)
                    throw exception;

                await muxer.FinishVideoAsync();
            });

            var audioTask = Task.Run(async () =>
            {
                Exception exception = null;
                for (var i = 0; i < audioFrames.Span.Length; i++)
                {
                    var frame = audioFrames.Span[i];
                    try
                    {
                        using (frame)
                        {
                            if (exception == null)
                                await muxer.PushAudioDataAsync(frame.Data);
                        }
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }
                }

                if (exception != null)
                    throw exception;

                await muxer.FinishAudioAsync();
            });

            await Task.WhenAll(videoTask, audioTask).ConfigureAwait(false);

            await muxer.CompleteAsync().ConfigureAwait(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RealtimeTranscoder));
        }
    }
}
