using System;
using System.Buffers;
using System.Threading.Channels;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Handles realtime recording with frame queuing and backpressure control.
    /// </summary>
    public class RealtimeRecorder : IDisposable
    {
        private const double AllowedLag = 0.1;
        private readonly Channel<AudioFrameData> _audioChannel;
        private readonly Task _audioProcessingTask;
        private readonly IAudioSampleProvider _audioSampleProvider;
        private readonly ChannelWriter<AudioFrameData> _audioWriter;
        private readonly FramePreprocessor _framePreprocessor;
        private readonly IFrameProvider _frameProvider;
        private readonly object _lock = new();
        private readonly RealtimeEncodingOptions _options;
        private readonly double _targetFrameInterval;
        private readonly RealtimeTranscoder _transcoder;
        private readonly Channel<VideoFrameData> _videoChannel;
        private readonly Task _videoProcessingTask;
        private readonly ChannelWriter<VideoFrameData> _videoWriter;
        private long? _currentSamplePosition;
        private bool _disposed;
        private bool _isRecording;
        private double _lastFrameTime;

        /// <summary>
        ///     Creates a new RealtimeRecorder with the specified options.
        /// </summary>
        public RealtimeRecorder(RealtimeEncodingOptions options, IFrameProvider frameProvider = null,
            IAudioSampleProvider
                audioSampleProvider = null)
        {
            _options = options;
            _frameProvider = frameProvider ?? new ScreenshotFrameProvider();
            _audioSampleProvider = audioSampleProvider ?? new UnityAudioSampleProvider();
            _transcoder = new RealtimeTranscoder(options);

            // Initialize frame rate limiting
            _targetFrameInterval = 1.0 / options.TargetFrameRate;
            _lastFrameTime = 0;

            _framePreprocessor =
                FramePreprocessor.WithFixedSize((int)options.VideoOptions.Width, (int)options.VideoOptions.Height);

            // Create bounded channels with DropOldest policy for backpressure
            _videoChannel = Channel.CreateBounded<VideoFrameData>(
                new BoundedChannelOptions(options.VideoInputQueueSize)
                {
                    SingleReader = true,
                    SingleWriter = false
                }
            );
            _audioChannel = Channel.CreateBounded<AudioFrameData>(
                new BoundedChannelOptions(options.AudioInputQueueSize)
                {
                    SingleReader = true,
                    SingleWriter = false
                }
            );

            _videoWriter = _videoChannel.Writer;
            _audioWriter = _audioChannel.Writer;

            // Start background processing tasks
            _videoProcessingTask = ProcessVideoFramesAsync();
            _audioProcessingTask = ProcessAudioFramesAsync();

            _frameProvider.OnFrameProvided += OnFrameProvided;
            _audioSampleProvider.OnProvideAudioSamples += OnProvideAudioSamples;

            if (options.AudioOptions.SampleRate is not (48000 or 44100))
                Debug.LogWarning(
                    $"Encoding may fail if sample rate is neither 48000 nor 41000 (current: {_options.AudioOptions.SampleRate}.");
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
                    _isRecording = false;

                    // Signal cancellation and complete channels
                    _videoWriter.Complete();
                    _audioWriter.Complete();

                    _frameProvider.OnFrameProvided -= OnFrameProvided;
                    _audioSampleProvider.OnProvideAudioSamples -= OnProvideAudioSamples;

                    // Wait for background tasks to complete
                    try
                    {
                        Task.WhenAll(_videoProcessingTask, _audioProcessingTask).Wait(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }

                    // Dispose resources
                    _transcoder?.Dispose();

                    _disposed = true;
                }
            }
        }

        private void OnFrameProvided(IFrameProvider.Frame frame)
        {
            if (_disposed || !_isRecording)
                return;

            var texture = frame.Texture;
            var time = frame.Timestamp;
            var needFlipVertically = frame.NeedFlipVertically;

            if (ShouldLimitFrameRate(time))
                return;

            var renderTexture = _framePreprocessor.Process(texture, needFlipVertically);
            var nativeArrayData = RealtimeFrameReadback.ReadbackFrameAsync(renderTexture);

            var frameData = new VideoFrameData(nativeArrayData, renderTexture.width, renderTexture.height, time);

            // Try to write to channel (non-blocking)
            if (!_videoWriter.TryWrite(frameData))
                // Channel is full, frame will be dropped
                _ = DisposeFrame(frameData);

            return;

            static async ValueTask DisposeFrame(VideoFrameData frame)
            {
                (await frame.ReadbackTask).Dispose();
            }
        }

        private void OnProvideAudioSamples(ReadOnlySpan<float> samples, int channels, int sampleRate,
            double timestamp)
        {
            if (_disposed || !_isRecording || samples == null || samples.Length == 0)
                return;

            var numSamples = samples.Length / channels;
            var sampleRateInOption = _options.AudioOptions.SampleRate;
            var numChannelsInOption = _options.AudioOptions.Channels;

            var samplePosition = (long)Math.Round(timestamp * sampleRateInOption);
            var currentSamplePosition = _currentSamplePosition ??= samplePosition;
            var lag = samplePosition - currentSamplePosition;

            var numScaledSamples = sampleRateInOption == sampleRate
                ? numSamples
                : (long)Math.Round(numSamples * ((double)sampleRateInOption / sampleRate));
            long blankOrSkip;
            if (Math.Abs(lag) > AllowedLag * sampleRateInOption)
            {
                // if there is too much lag, skip input or insert blank
                blankOrSkip = lag;
            }
            else
            {
                // scale to position
                blankOrSkip = 0;
                numScaledSamples += lag;
            }

            var writeLength = (int)((numScaledSamples + blankOrSkip) * channels);
            var writeBufferArray = ArrayPool<short>.Shared.Rent(writeLength);
            var writeBuffer = writeBufferArray.AsSpan(0, writeLength);

            if (blankOrSkip > 0)
                writeBuffer[..(int)blankOrSkip].Clear();

            var skip = (int)Math.Max(0, -blankOrSkip);

            for (var i = skip; i < numScaledSamples; i++)
            for (var j = 0; j < numChannelsInOption; j++)
            {
                // NOTE: should we interpolate samples?
                var pos = sampleRate == sampleRateInOption
                    ? i
                    : (int)Math.Floor(i * ((double)numSamples / numScaledSamples));
                var sample = samples[pos * channels + j % channels];
                var scaledSample = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
                writeBuffer[i * (int)numChannelsInOption + j] = scaledSample;
            }

            var frameData = new AudioFrameData(writeBufferArray, writeBufferArray.AsMemory(0, writeLength), timestamp);

            if (!_audioWriter.TryWrite(frameData))
                // Channel is full, frame will be dropped
                frameData.Dispose();
            else
                _currentSamplePosition = currentSamplePosition + numScaledSamples + blankOrSkip;
        }


        /// <summary>
        ///     Starts recording.
        /// </summary>
        public void StartRecording()
        {
            lock (_lock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(RealtimeRecorder));

                if (_isRecording)
                    return;

                _isRecording = true;
            }
        }

        /// <summary>
        ///     Stops recording.
        /// </summary>
        public void StopRecording()
        {
            lock (_lock)
            {
                if (!_isRecording)
                    return;

                _isRecording = false;
            }
        }

        /// <summary>
        ///     Exports the last N seconds to a file.
        /// </summary>
        public async Task<string> ExportLastSecondsAsync(double seconds, string outputPath)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RealtimeRecorder));

            return await _transcoder.ExportLastSecondsAsync(seconds, outputPath);
        }

        /// <summary>
        ///     Determines if the frame rate should be limited at the given time.
        /// </summary>
        /// <param name="currentTime">Current timestamp in seconds</param>
        /// <returns>True if the frame should be dropped to maintain target frame rate</returns>
        private bool ShouldLimitFrameRate(double currentTime)
        {
            var timeSinceLastFrame = currentTime - _lastFrameTime;

            // Drop frame if we're running faster than target frame rate
            // Use 90% of target interval to provide some buffer
            if (timeSinceLastFrame < _targetFrameInterval * 0.9)
                return true; // Drop this frame

            _lastFrameTime = currentTime;
            return false; // Process this frame
        }

        private async Task ProcessVideoFramesAsync()
        {
            await foreach (var frameData in _videoChannel.Reader.ReadAllAsync().ConfigureAwait(false))
                try
                {
                    // Read back frame data from GPU
                    using var nativeArrayData = await frameData.ReadbackTask.ConfigureAwait(false);

                    // Push to transcoder
                    await _transcoder.PushVideoFrameAsync(
                        nativeArrayData,
                        (uint)frameData.Width,
                        (uint)frameData.Height,
                        frameData.Timestamp).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (ObjectDisposedException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
        }

        private async Task ProcessAudioFramesAsync()
        {
            await foreach (var audioData in _audioChannel.Reader.ReadAllAsync().ConfigureAwait(false))
                try
                {
                    // Push to transcoder
                    using (audioData)
                    {
                        await _transcoder.PushAudioSamplesAsync(
                            audioData.Data,
                            audioData.Timestamp).ConfigureAwait(false);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
        }
    }

    /// <summary>
    ///     Represents video frame data for processing.
    /// </summary>
    internal readonly struct VideoFrameData
    {
        public readonly ValueTask<NativeArray<byte>> ReadbackTask;
        public readonly int Width;
        public readonly int Height;
        public readonly double Timestamp;

        public VideoFrameData(ValueTask<NativeArray<byte>> readbackTask, int width, int height, double timestamp)
        {
            ReadbackTask = readbackTask;
            Width = width;
            Height = height;
            Timestamp = timestamp;
        }
    }

    /// <summary>
    ///     Represents audio frame data for processing.
    /// </summary>
    internal readonly struct AudioFrameData : IDisposable
    {
        private readonly short[] _array; // Rented from ArrayPool
        public readonly ReadOnlyMemory<short> Data;
        public readonly double Timestamp;

        public AudioFrameData(short[] rendArray, ReadOnlyMemory<short> data, double timestamp)
        {
            _array = rendArray;
            Data = data;
            Timestamp = timestamp;
        }

        public void Dispose()
        {
            ArrayPool<short>.Shared.Return(_array);
        }
    }
}
