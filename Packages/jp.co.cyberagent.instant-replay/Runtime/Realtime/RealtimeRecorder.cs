using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace InstantReplay
{
    /// <summary>
    ///     Handles realtime recording with frame queuing and backpressure control.
    /// </summary>
    public class RealtimeRecorder : IDisposable
    {
        private const double AllowedLag = 0.1;
        private readonly Channel<AudioFrameData> _audioChannel;
        private readonly IAudioSampleProvider _audioSampleProvider;
        private readonly ChannelWriter<AudioFrameData> _audioWriter;
        private readonly bool _disposeAudioSampleProvider;
        private readonly bool _disposeFrameProvider;
        private readonly double? _fixedFrameInterval;
        private readonly FramePreprocessor _framePreprocessor;
        private readonly IFrameProvider _frameProvider;
        private readonly object _lock = new();
        private readonly RealtimeEncodingOptions _options;
        private readonly RealtimeTranscoder _transcoder;
        private readonly Channel<VideoFrameData> _videoChannel;
        private readonly ChannelWriter<VideoFrameData> _videoWriter;
        private double? _audioTimeDifference;
        private long? _currentSamplePosition;
        private bool _disposed;
        private double _frameTimer;
        private bool _isRecording;
        private double _prevFrameTime;
        private double? _videoTimeDifference;

        /// <summary>
        ///     Creates a new RealtimeRecorder with the specified options.
        /// </summary>
        public RealtimeRecorder(
            RealtimeEncodingOptions options,
            IFrameProvider frameProvider = null,
            bool disposeFrameProvider = true,
            IAudioSampleProvider audioSampleProvider = null,
            bool disposeAudioSampleProvider = true)
        {
            _options = options;

            if (frameProvider == null)
            {
                frameProvider = new ScreenshotFrameProvider();
                disposeFrameProvider = true;
            }

            if (audioSampleProvider == null)
            {
                audioSampleProvider = new UnityAudioSampleProvider();
                disposeAudioSampleProvider = true;
            }

            _frameProvider = frameProvider;
            _audioSampleProvider = audioSampleProvider;

            _disposeFrameProvider = disposeFrameProvider;
            _disposeAudioSampleProvider = disposeAudioSampleProvider;

            _transcoder = new RealtimeTranscoder(options);

            // Initialize frame rate limiting
            if (options.FixedFrameRate is { } fixedFrameRate)
            {
                if (fixedFrameRate <= 0)
                    throw new ArgumentOutOfRangeException(nameof(fixedFrameRate), "Fixed frame rate must be greater than zero.");
                _fixedFrameInterval = 1.0 / fixedFrameRate;
            }

            // Debug.Log($"Video Size: {options.VideoOptions.Width}x{options.VideoOptions.Height}");

            _framePreprocessor =
                FramePreprocessor.WithFixedSize((int)options.VideoOptions.Width, (int)options.VideoOptions.Height,
                    // RGBA to BGRA
                    new Matrix4x4(new Vector4(0, 0, 1, 0),
                        new Vector4(0, 1, 0, 0),
                        new Vector4(1, 0, 0, 0),
                        new Vector4(0, 0, 0, 1)
                    ),
                    true);

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
            _ = ProcessVideoFramesAsync();
            _ = ProcessAudioFramesAsync();

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

                    // Dispose resources
                    _transcoder?.Dispose();
                    _framePreprocessor?.Dispose();

                    if (_disposeFrameProvider)
                        _frameProvider?.Dispose();

                    if (_disposeAudioSampleProvider)
                        _audioSampleProvider?.Dispose();

                    _disposed = true;
                }
            }
        }

        private void OnFrameProvided(IFrameProvider.Frame frame)
        {
            var realTime = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

            if (_disposed || !_isRecording)
                return;

            var texture = frame.Texture;
            var time = frame.Timestamp;
            var needFlipVertically = frame.NeedFlipVertically;

            var deltaTime = time - _prevFrameTime;

            if (deltaTime <= 0) return;

            _frameTimer += deltaTime;
            _prevFrameTime = time;

            if (_fixedFrameInterval is { } fixedFrameInterval)
            {
                if (_frameTimer < _fixedFrameInterval) return;
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

            var renderTexture = _framePreprocessor.Process(texture, needFlipVertically);
            var nativeArrayData = RealtimeFrameReadback.ReadbackFrameAsync(renderTexture);

            var frameData = new VideoFrameData(nativeArrayData, renderTexture.width, renderTexture.height, time);

            // Try to write to channel (non-blocking)
            if (!_videoWriter.TryWrite(frameData))
            {
                // Channel is full, frame will be dropped
                _ = DisposeFrame(frameData);
                Debug.LogWarning("Video frame dropped due to full channel.");
            }

            return;

            static async ValueTask DisposeFrame(VideoFrameData frame)
            {
                (await frame.ReadbackTask).Dispose();
            }
        }

        private void OnProvideAudioSamples(ReadOnlySpan<float> samples, int channels, int sampleRate,
            double timestamp)
        {
            var realTime = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

            if (_disposed || !_isRecording || samples == null || samples.Length == 0)
                return;

            // adjust timestamp
            if (!_audioTimeDifference.HasValue)
            {
                _audioTimeDifference = timestamp - realTime;
                timestamp = realTime;
            }
            else
            {
                var expectedTime = realTime + _audioTimeDifference.Value;
                var diff = timestamp - expectedTime;
                if (Math.Abs(diff) >= AllowedLag)
                {
                    Debug.LogWarning(
                        "Audio timestamp adjusted. The timestamp IAudioSampleProvider provided may not be realtime.");
                    _audioTimeDifference = timestamp - realTime;
                    timestamp = realTime;
                }
                else
                {
                    timestamp -= _audioTimeDifference.Value;
                }
            }

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

            if (writeLength == 0)
                return;

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
            {
                // Channel is full, frame will be dropped
                frameData.Dispose();
                Debug.LogWarning("Audio frame dropped due to full channel.");
            }

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
        public async Task<string> ExportLastSecondsAsync(string outputPath, double? maxSeconds = null)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RealtimeRecorder));

            return await _transcoder.ExportLastSecondsAsync(maxSeconds, outputPath);
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
