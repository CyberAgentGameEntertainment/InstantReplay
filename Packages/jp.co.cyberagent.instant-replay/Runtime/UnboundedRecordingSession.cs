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
        private bool _isDisposed;

        public UnboundedRecordingSession(RealtimeEncodingOptions options,
            string outputPath,
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

            using var encodingSystem = new EncodingSystem(options.VideoOptions, options.AudioOptions);
            var videoEncoder = encodingSystem.CreateVideoEncoder();
            var audioEncoder = encodingSystem.CreateAudioEncoder();
            var muxer = _muxer = encodingSystem.CreateMuxer(outputPath);

            _videoPipeline = new FrameProviderSubscription(frameProvider, disposeFrameProvider,
                new VideoTemporalAdjuster<IFrameProvider.Frame>(_temporalController, fixedFrameInterval)
                    .AsInput(
                        new FramePreprocessorInput(
                            FramePreprocessor.WithFixedSize(
                                (int)options.VideoOptions.Width,
                                (int)options.VideoOptions.Height,
                                // RGBA to BGRA
                                new Matrix4x4(new Vector4(0, 0, 1, 0),
                                    new Vector4(0, 1, 0, 0),
                                    new Vector4(1, 0, 0, 0),
                                    new Vector4(0, 0, 0, 1)
                                )), true).AsInput(
                            new AsyncGPUReadbackTransform().AsInput(
                                new DroppingChannelInput<LazyVideoFrameData>(options.VideoInputQueueSize,
                                    async static dropped =>
                                    {
                                        try
                                        {
                                            Debug.LogWarning("Dropped video frame due to full queue.");
                                            using var _ = await dropped.ReadbackTask;
                                        }
                                        catch (Exception ex)
                                        {
                                            Debug.LogException(ex);
                                        }
                                    },
                                    new VideoEncoderInput(videoEncoder,
                                        new MuxerVideoInput(muxer))))))
            );

            _audioPipeline = new AudioSampleProviderSubscription(audioSampleProvider, disposeAudioSampleProvider,
                new AudioTemporalAdjuster(_temporalController, options.AudioOptions.SampleRate,
                        options.AudioOptions.Channels)
                    .AsInput(
                        new DroppingChannelInput<PcmAudioFrame>(options.AudioInputQueueSize,
                            static dropped =>
                            {
                                Debug.LogWarning("Dropped audio frame due to full queue.");
                                dropped.Dispose();
                            },
                            new AudioEncoderInput(audioEncoder, options.AudioOptions.SampleRate,
                                new MuxerAudioInput(muxer)))));

            _temporalController.Resume();
        }

        public bool IsPaused => _temporalController.IsPaused;

        /// <summary>
        ///     Gets the current state of the session.
        /// </summary>
        public SessionState State { get; private set; }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
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
