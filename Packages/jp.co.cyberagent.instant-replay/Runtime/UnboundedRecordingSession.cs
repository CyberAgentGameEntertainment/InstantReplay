// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Single session of unbounded recorder.
    ///     <see cref="UnboundedRecordingSession" /> records video and audio into disk continuously until stopped explicitly.
    /// </summary>
    public class UnboundedRecordingSession : IDisposable
    {
        private readonly AudioSampleProviderSubscription _audioPipeline;
        private readonly object _lock = new();
        private readonly Muxer _muxer;
        private readonly TemporalController _temporalController = new();
        private readonly FrameProviderSubscription _videoPipeline;
        private bool _disposed;

        public UnboundedRecordingSession(
            string outputPath,
            RealtimeEncodingOptions options,
            IFrameProvider frameProvider = null,
            bool disposeFrameProvider = true,
            IAudioSampleProvider audioSampleProvider = null,
            bool disposeAudioSampleProvider = true)
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

            using var encodingSystem = new EncodingSystem(options.VideoOptions, options.AudioOptions);
            var videoEncoder = encodingSystem.CreateVideoEncoder();
            var audioEncoder = encodingSystem.CreateAudioEncoder();
            var muxer = _muxer = encodingSystem.CreateMuxer(outputPath);

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
                _videoPipeline = new FrameProviderSubscription(frameProvider, disposeFrameProvider,
                    new VideoTemporalAdjuster<IFrameProvider.Frame>(
                        _temporalController,
                        fixedFrameInterval,
                        options.VideoLagAdjustmentThreshold).AsInput(
                        new DirectFrameDataTransform().AsInput(
                            new DroppingChannelInput<LazyVideoFrameData>(
                                options.VideoInputQueueSize,
                                onLazyVideoFrameDataDropped,
                                new VideoEncoderInput(videoEncoder,
                                    new MuxerVideoInput(muxer)
                                )))));
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

                _videoPipeline = new FrameProviderSubscription(frameProvider, disposeFrameProvider,
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
                                        new MuxerVideoInput(muxer)))))));
            }

            var audioInputQueueSizeSeconds = options.AudioInputQueueSizeSeconds ?? 1.0;
            var audioInputQueueSizeSamples = (int)(options.AudioOptions.SampleRate * options.AudioOptions.Channels *
                                                   audioInputQueueSizeSeconds);

            _audioPipeline = new AudioSampleProviderSubscription(audioSampleProvider, disposeAudioSampleProvider,
                new AudioTemporalAdjuster(
                    _temporalController,
                    options.AudioOptions.SampleRate,
                    options.AudioOptions.Channels,
                    options.AudioLagAdjustmentThreshold).AsInput(
                    new PcmAudioFrameDroppingChannelInput(audioInputQueueSizeSamples,
                        new AudioEncoderInput(audioEncoder, options.AudioOptions.SampleRate,
                            new MuxerAudioInput(muxer)))));

            _temporalController.Resume();
        }

        public bool IsPaused => _temporalController.IsPaused;

        /// <summary>
        ///     Disposes the session and releases all resources.
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _videoPipeline?.Dispose();
                _audioPipeline?.Dispose();
                _muxer?.Dispose();
            }
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

        /// <summary>
        ///     Completes the recording and finalizes the output file.
        /// </summary>
        public async ValueTask CompleteAsync()
        {
            await Task.WhenAll(_videoPipeline.CompleteAsync().AsTask(), _audioPipeline.CompleteAsync().AsTask());
            await _muxer.CompleteAsync();
        }
    }
}
