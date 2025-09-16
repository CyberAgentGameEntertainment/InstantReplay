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
        private readonly object _lock = new();
        private readonly RealtimeRecorder _recorder;
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
            bool disposeAudioSampleProvider = true)
        {
            State = SessionState.Recording;
            _recorder = new RealtimeRecorder(
                options,
                frameProvider,
                disposeFrameProvider,
                audioSampleProvider,
                disposeAudioSampleProvider);

            // Start recording automatically
            _recorder.Resume();
        }

        public bool IsPaused => !_recorder.IsRecording;

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
                if (!_disposed)
                {
                    // Stop recording if still recording
                    if (State == SessionState.Recording)
                        _recorder?.Pause();

                    _recorder?.Dispose();
                    _disposed = true;
                }
            }
        }

        /// <summary>
        ///     Creates a new RealtimeInstantReplaySession with default options.
        ///     Recording starts automatically upon construction.
        /// </summary>
        public static RealtimeInstantReplaySession CreateDefault()
        {
            var options = new RealtimeEncodingOptions
            {
                VideoOptions = new VideoEncoderOptions
                {
                    Width = 1280,
                    Height = 720,
                    FpsHint = 30,
                    Bitrate = 2500000 // 2.5 Mbps
                },
                AudioOptions = new AudioEncoderOptions
                {
                    SampleRate = 44100,
                    Channels = 2,
                    Bitrate = 128000 // 128 kbps
                },
                MaxMemoryUsageBytes = 20 * 1024 * 1024, // 20 MiB
                FixedFrameRate = 30.0,
                VideoInputQueueSize = 5,
                AudioInputQueueSize = 60
            };

            return new RealtimeInstantReplaySession(options);
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
        public async Task<string> StopAndExportAsync(double? seconds = default, string outputPath = default)
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
                _recorder.Pause();
            }

            try
            {
                State = SessionState.Exporting;

                // Generate output path if not provided
                if (string.IsNullOrEmpty(outputPath))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var fileName = $"InstantReplay_{timestamp}.mp4";
                    outputPath = Path.Combine(Application.temporaryCachePath, fileName); // save to temporary cache path by default
                }

                var result = await _recorder.ExportLastSecondsAsync(outputPath, seconds);

                State = SessionState.Completed;
                return result;
            }
            catch (Exception)
            {
                State = SessionState.Invalid;
                throw;
            }
        }

        /// <summary>
        ///     Pauses the recording.
        /// </summary>
        public void Pause()
        {
            _recorder.Pause();
        }

        /// <summary>
        ///     Resumes the recording.
        /// </summary>
        public void Resume()
        {
            _recorder.Resume();
        }
    }
}
