// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using System.Threading.Tasks;
using UniEnc;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Single session of realtime InstantReplay for recording and exporting.
    ///     This is a disposable, one-time use session that automatically starts recording
    ///     on construction and allows a single export operation.
    /// </summary>
    public class RealtimeInstantReplaySession : IDisposable
    {
        private readonly AudioSampleProviderSubscription _audioPipeline;
        private readonly BoundedEncodedFrameBuffer _buffer;
        private readonly EncodingSystem _encodingSystem;
        private readonly object _lock = new();
        private readonly TemporalController _temporalController = new();
        private readonly FrameProviderSubscription _videoPipeline;
        private bool _disposed;

        /// <summary>
        ///     Creates a new RealtimeInstantReplaySession with the specified options.
        ///     Recording starts automatically upon construction.
        /// </summary>
        public RealtimeInstantReplaySession(
            in RealtimeEncodingOptions options,
            IFrameProvider frameProvider = null,
            bool disposeFrameProvider = true,
            IAudioSampleProvider audioSampleProvider = null,
            bool disposeAudioSampleProvider = true,
            Action<Exception> onException = null)
        {
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

            double? fixedFrameInterval = null;
            if (options.FixedFrameRate is { } fixedFrameRate)
            {
                if (fixedFrameRate <= 0)
                    throw new ArgumentOutOfRangeException(nameof(fixedFrameRate),
                        "Fixed frame rate must be greater than zero.");
                fixedFrameInterval = 1.0 / fixedFrameRate;
            }

            var uncompressedLimit = options.MaxNumberOfRawFrameBuffers switch
            {
                <= 0 => throw new ArgumentOutOfRangeException(nameof(options.MaxNumberOfRawFrameBuffers),
                    "MaxNumberOfRawFrameBuffer must be positive if specified."),
                { } value => options.VideoOptions.Width * options.VideoOptions.Height * 4 * value, // 32bpp
                null => 0
            };

            var encodingSystem = _encodingSystem = new EncodingSystem(options.VideoOptions, options.AudioOptions);
            var videoEncoder = encodingSystem.CreateVideoEncoder();
            var audioEncoder = encodingSystem.CreateAudioEncoder();
            var buffer = _buffer = new BoundedEncodedFrameBuffer(options.MaxMemoryUsageBytesForCompressedFrames);

            // ReSharper disable once ConvertToLocalFunction
            Action<LazyVideoFrameData> onLazyVideoFrameDataDropped = async static dropped =>
            {
                try
                {
                    ILogger.LogWarningCore("Dropped video frame due to full queue.");
                    using var _ = await dropped.ReadbackTask;
                }
                catch (Exception ex)
                {
                    ILogger.LogExceptionCore(ex);
                }
            };

            if (!options.ForceReadback && encodingSystem.IsBlitSupported())
            {
                _videoPipeline = new FrameProviderSubscription(frameProvider, disposeFrameProvider, onException,
                    new VideoTemporalAdjuster<IFrameProvider.Frame>(
                        _temporalController,
                        fixedFrameInterval,
                        options.VideoLagAdjustmentThreshold).AsInput(
                        new DirectFrameDataTransform().AsInput(
                            new DroppingChannelInput<LazyVideoFrameData>(
                                options.VideoInputQueueSize,
                                onLazyVideoFrameDataDropped,
                                new VideoEncoderInput(videoEncoder,
                                    new BoundedEncodedDataBufferVideoInput(buffer).AsAsync())))));
            }
            else
            {
                var preprocessor = FramePreprocessor.WithFixedSize(
                    (int)options.VideoOptions.Width,
                    (int)options.VideoOptions.Height,
                    // RGBA to BGRA
                    new Matrix4x4(new Vector4(0, 0, 1, 0),
                        new Vector4(0, 1, 0, 0),
                        new Vector4(1, 0, 0, 0),
                        new Vector4(0, 0, 0, 1)
                    ));

                _videoPipeline = new FrameProviderSubscription(frameProvider, disposeFrameProvider, onException,
                    new VideoTemporalAdjuster<IFrameProvider.Frame>(
                        _temporalController,
                        fixedFrameInterval,
                        options.VideoLagAdjustmentThreshold).AsInput(
                        new FramePreprocessorInput(preprocessor, true).AsInput(
                            new AsyncGPUReadbackTransform(new SharedBufferPool((nuint)uncompressedLimit)).AsInput(
                                new DroppingChannelInput<LazyVideoFrameData>(
                                    options.VideoInputQueueSize,
                                    onLazyVideoFrameDataDropped,
                                    new VideoEncoderInput(videoEncoder,
                                        new BoundedEncodedDataBufferVideoInput(buffer).AsAsync()))))));
            }

            var audioInputQueueSizeSeconds = options.AudioInputQueueSizeSeconds ?? 1.0;
            var audioInputQueueSizeSamples = (int)(options.AudioOptions.SampleRate * options.AudioOptions.Channels *
                                                   audioInputQueueSizeSeconds);

            _audioPipeline = new AudioSampleProviderSubscription(audioSampleProvider, disposeAudioSampleProvider,
                onException,
                new AudioTemporalAdjuster(
                    _temporalController,
                    options.AudioOptions.SampleRate,
                    options.AudioOptions.Channels,
                    options.AudioLagAdjustmentThreshold).AsInput(
                    new PcmAudioFrameDroppingChannelInput(audioInputQueueSizeSamples,
                        new AudioEncoderInput(audioEncoder,
                            new BoundedEncodedDataBufferAudioInput(buffer).AsAsync()))));

            _temporalController.Resume();
            State = SessionState.Recording;
        }

        public bool IsPaused => _temporalController.IsPaused;

        /// <summary>
        ///     Gets the current state of the session.
        /// </summary>
        public SessionState State { get; private set; }

        /// <summary>
        ///     Disposes the session and releases all resources.
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _videoPipeline.Dispose();
                _audioPipeline.Dispose();
                _encodingSystem.Dispose();
                _buffer.Dispose();
            }
        }

        /// <summary>
        ///     Creates a new RealtimeInstantReplaySession with default options.
        ///     Recording starts automatically upon construction.
        /// </summary>
        public static RealtimeInstantReplaySession CreateDefault()
        {
            return new RealtimeInstantReplaySession(RealtimeEncodingOptions.Default);
        }

        /// <summary>
        ///     Stop recording and export the last N seconds of recording to a file.
        ///     This method can be called only once.
        /// </summary>
        /// <param name="seconds">Duration in seconds to export</param>
        /// <param name="outputPath">Output file path. If null, a default path will be generated.</param>
        /// <returns>Path to the exported video file</returns>
        /// <exception cref="InvalidOperationException">Thrown if called when not in Recording state</exception>
        /// <exception cref="ArgumentException">Thrown if duration is not positive</exception>
        public async ValueTask<string> StopAndExportAsync(double? seconds = default, string outputPath = default)
        {
            if (State != SessionState.Recording)
                throw new InvalidOperationException(
                    $"Cannot export when state is {State}. Export can only be called once.");

            if (seconds <= 0)
                throw new ArgumentException("Duration must be positive", nameof(seconds));

            lock (_lock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(RealtimeInstantReplaySession));

                if (State != SessionState.Recording)
                    throw new InvalidOperationException(
                        $"Cannot export when state is {State}. Export can only be called once.");

                State = SessionState.WaitForRecordingComplete;
                _temporalController.Pause();
            }

            try
            {
                await Task.WhenAll(_videoPipeline.CompleteAsync().AsTask(), _audioPipeline.CompleteAsync().AsTask());

                State = SessionState.Exporting;

                // Generate output path if not provided
                if (string.IsNullOrEmpty(outputPath))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var fileName = $"InstantReplay_{timestamp}.mp4";
                    outputPath =
                        Path.Combine(Application.temporaryCachePath,
                            fileName); // save to temporary cache path by default
                }

                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                // Create a temporary muxer for this export
                using var muxer = _encodingSystem.CreateMuxer(outputPath);

                // Get frames for the requested duration
                _buffer.GetFramesForDuration(seconds, out var videoFrames, out var audioFrames);

                // Mux the segment
                await MuxSegmentAsync(muxer, videoFrames, audioFrames);

                State = SessionState.Completed;
                return outputPath;
            }
            catch (Exception)
            {
                State = SessionState.Invalid;
                throw;
            }
        }


        private async ValueTask MuxSegmentAsync(Muxer muxer, ReadOnlyMemory<EncodedFrame> videoFrames,
            ReadOnlyMemory<EncodedFrame> audioFrames)
        {
            async ValueTask MuxVideoAsync()
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
                                await muxer.PushVideoDataAsync(frame).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }
                }

                // Errors in the input to the muxer do not propagate as exceptions in PushVideoDataAsync;
                // instead, a channel closed error occurs when attempting to input the next frame.
                // Since the actual muxer error is returned in FinishVideoAsync,
                // we should call FinishVideoAsync even if PushVideoDataAsync fails.
                await muxer.FinishVideoAsync().ConfigureAwait(false);

                if (exception != null)
                    throw exception;
            }

            async ValueTask MuxAudioAsync()
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
                                await muxer.PushAudioDataAsync(frame).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }
                }

                // same as video
                await muxer.FinishAudioAsync().ConfigureAwait(false);

                if (exception != null)
                    throw exception;
            }

            // Always observe both tasks even if one of them fails, so that the muxer is not
            // disposed (by the caller) while the other task is still using it.
            var video = MuxVideoAsync().AsTask();
            var audio = MuxAudioAsync().AsTask();

            var whenAll = Task.WhenAll(video, audio);
            try
            {
                await whenAll.ConfigureAwait(false);
            }
            catch (Exception) when (whenAll.Exception is { } aggregate && aggregate.InnerExceptions.Count > 1)
            {
                // Awaiting Task.WhenAll rethrows only the first exception;
                // rethrow the AggregateException so that all failures are propagated.
                throw aggregate;
            }

            await muxer.CompleteAsync();
        }

        /// <summary>
        ///     Pauses the recording.
        /// </summary>
        public void Pause()
        {
            _temporalController.Pause();
        }

        /// <summary>
        ///     Resumes the recording.
        /// </summary>
        public void Resume()
        {
            _temporalController.Resume();
        }
    }
}
