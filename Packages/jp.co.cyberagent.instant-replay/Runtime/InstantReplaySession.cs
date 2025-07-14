// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Single session of InstantReplay for recording and transcoding.
    /// </summary>
    public class InstantReplaySession : IDisposable
    {
        private static string _instantReplayDirectory;
        private readonly PipeReader _audioRecorderReader;

        private readonly CancellationTokenSource _cts = new();
        private readonly string _directory;
        private readonly Guid _id = Guid.NewGuid();
        private readonly TaskCompletionSource<RecorderResult> _recorderCompletion = new();
        private AudioRecorder _audioRecorder;
        private Recorder _recorder;

        /// <summary>
        /// </summary>
        /// <param name="numFrames">Number of preserved frames.</param>
        /// <param name="fixedFrameRate">Frame rate. If omitted, all produces frames are transcoded.</param>
        /// <param name="frameProvider">
        ///     Custom frame provider. If omitted,
        ///     <see cref="UnityEngine.Rendering.RenderPipelineManager.endContextRendering" />
        ///     and
        ///     <see cref="UnityEngine.ScreenCapture.CaptureScreenshotIntoRenderTexture" />
        ///     will be used.
        /// </param>
        /// <param name="disposeFrameProvider">Whether this session disposes <paramref name="frameProvider" />. </param>
        /// <param name="audioSampleProvider"></param>
        /// <param name="disposeAudioSampleProvider"></param>
        /// <param name="maxWidth">Max width of the video.</param>
        /// <param name="maxHeight">Max height of the video.</param>
        public InstantReplaySession(int numFrames,
            double? fixedFrameRate = 30,
            IFrameProvider frameProvider = default,
            bool disposeFrameProvider = true,
            IAudioSampleProvider audioSampleProvider = default,
            bool disposeAudioSampleProvider = true,
            int? maxWidth = default,
            int? maxHeight = default)
        {
            State = SessionState.Recording;

            _instantReplayDirectory ??= Path.Combine(Application.temporaryCachePath,
                "jp.co.cyberagent.instant-replay");
            var directory = _directory = Path.Combine(_instantReplayDirectory, _id.ToString("N"));

            Directory.CreateDirectory(directory);

            if (audioSampleProvider == null)
            {
                audioSampleProvider = new UnityAudioSampleProvider();
                disposeAudioSampleProvider = true;
            }

            _audioRecorder =
                new AudioRecorder(audioSampleProvider, disposeAudioSampleProvider, out _audioRecorderReader);

            if (frameProvider == null)
            {
                frameProvider = new ScreenshotFrameProvider();
                disposeFrameProvider = true;
            }

            _recorder = new Recorder(
                numFrames,
                fixedFrameRate,
                directory,
                frameProvider,
                disposeFrameProvider,
                FramePreprocessor.WithMaxSize(maxWidth, maxHeight),
                keepSeconds => { _audioRecorder?.DiscardSamples(keepSeconds); },
                result => { _recorderCompletion.TrySetResult(result); });
        }

        public SessionState State { get; private set; }

        public int NumBusySlots => _recorder?.NumBusySlots ?? 0;

        public void Dispose()
        {
            _cts.Cancel();

            try
            {
                StopAndTranscodeAsync().GetAwaiter().UnsafeOnCompleted(() => { });
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        ///     Stop recording and transcode frames. This method can be called only once.
        /// </summary>
        /// <param name="progress">Progress of the transcoding.</param>
        /// <param name="ct"></param>
        /// <returns>
        ///     Output mp4 file name, or null if there are no captured data. Containing directory will be deleted after the
        ///     disposal, so you need to move or copy the file.
        /// </returns>
        /// <exception cref="InvalidOperationException"></exception>
        public ValueTask<string> StopAndTranscodeAsync(IProgress<float> progress = default,
            CancellationToken ct = default)
        {
            return StopAndTranscodeAsync(null, progress, ct);
        }

        /// <summary>
        ///     Stop recording and transcode frames. This method can be called only once.
        /// </summary>
        /// <param name="maxDuration">Max duration of output video in seconds. Older frames will be discarded.</param>
        /// <param name="progress">Progress of the transcoding.</param>
        /// <param name="ct"></param>
        /// <returns>
        ///     Output mp4 file name, or null if there are no captured data. Containing directory will be deleted after the
        ///     disposal, so you need to move or copy the file.
        /// </returns>
        /// <exception cref="InvalidOperationException"></exception>
        public async ValueTask<string> StopAndTranscodeAsync(double? maxDuration, IProgress<float> progress = default,
            CancellationToken ct = default)
        {
            if (State != SessionState.Recording) throw new InvalidOperationException();
            try
            {
                State = SessionState.WaitForRecordingComplete;
                _recorder!.Dispose();
                _recorder = default;

                var numChannels = _audioRecorder.NumChannels ?? 2;
                var sampleRate = _audioRecorder.SampleRate ?? 48000;
                _audioRecorder!.Dispose();
                _audioRecorder = default;

                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
                ct = linkedCts.Token;

                progress?.Report(0f);

                var result = await _recorderCompletion.Task.ConfigureAwait(false);

                var reader = _audioRecorderReader;

                ct.ThrowIfCancellationRequested();

                progress?.Report(0.01f);

                State = SessionState.Transcoding;

                string outputFilename;
                if (result.Frames.Length == 0)
                {
                    outputFilename = null;
                }
                else
                {
                    ReadOnlyMemory<RecorderFrame> frames = result.Frames;

                    var duration = frames.Length >= 2
                        ? frames.Span[^1].Time - frames.Span[0].Time
                        : 1f / 30f /* single frame with 30FPS */;

                    if (duration > maxDuration && frames.Length > 1)
                    {
                        var endTime = frames.Span[^1].Time;
                        var minStartTime = endTime - maxDuration.Value;

                        // binary search for the first frame that is after the minStartTime
                        var startIndex = 0;
                        var endIndex = frames.Length - 1;
                        while (startIndex < endIndex)
                        {
                            var midIndex = (startIndex + endIndex) / 2;
                            if (frames.Span[midIndex].Time < minStartTime)
                                startIndex = midIndex + 1;
                            else
                                endIndex = midIndex;
                        }

                        if (startIndex > 0)
                        {
                            frames = frames.Slice(startIndex);
                            duration = frames.Length >= 2
                                ? frames.Span[^1].Time - frames.Span[0].Time
                                : 1f / 30f /* single frame with 30FPS */;

                            Debug.Log($"{frames.Length} frames, {duration} seconds");
                        }
                    }

                    if (duration < 0)
                        throw new InvalidOperationException("Negative duration");

                    var tempOutputFilename = Path.Combine(_directory, "output.mp4");

                    var transcoder = TranscoderProvider.Provide(result.Width, result.Height, sampleRate,
                        numChannels, tempOutputFilename);
                    await using (transcoder.ConfigureAwait(false))
                    {
                        var encodeAudioSamplesTask = Task.Run(async () =>
                        {
                            ReadResult res;
                            do
                            {
                                res = await reader.ReadAsync(ct);
                                reader.AdvanceTo(res.Buffer.Start, res.Buffer.End);
                            } while (!res.IsCompleted);

                            // insert blank or trim start
                            var buffer = res.Buffer;
                            var providedSamples = buffer.Length / numChannels / sizeof(short);
                            var expectedSamples = (long)Math.Round(duration * sampleRate);
                            var blankOrTrim = (expectedSamples - providedSamples) * numChannels * sizeof(short);

                            if (blankOrTrim > 0)
                            {
                                // blank
                                var blank = ArrayPool<byte>.Shared.Rent((int)Math.Min(4096, blankOrTrim));
                                try
                                {
                                    Array.Clear(blank, 0, blank.Length);
                                    for (var remain = blankOrTrim; remain > 0;)
                                    {
                                        var length = (int)Math.Min(blank.Length, remain);
                                        remain -= length;
                                        await transcoder.PushAudioSamplesAsync(blank.AsMemory(0, length), ct);
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(blank);
                                }
                            }
                            else if (blankOrTrim < 0)
                            {
                                // trim
                                buffer = buffer.Slice(-blankOrTrim);
                            }

                            foreach (var segment in buffer)
                                await transcoder.PushAudioSamplesAsync(segment, ct);

                            await reader.CompleteAsync();
                        }, default);

                        var startTime = frames.Span[0].Time;

                        for (var i = 0; i < frames.Length; i++)
                        {
                            var frame = frames.Span[i];
                            await transcoder.PushFrameAsync(frame.Path, frame.Time - startTime, ct)
                                .ConfigureAwait(false);
                            progress?.Report(0.01f + (float)i / frames.Length * 0.89f);
                        }

                        await encodeAudioSamplesTask;

                        await transcoder.CompleteAsync().ConfigureAwait(false);
                        progress?.Report(1f);
                    }

                    // move
                    outputFilename = Path.Combine(_instantReplayDirectory, $"{_id:N}.mp4");
                    File.Move(tempOutputFilename, outputFilename);
                }

                State = SessionState.Completed;

                return outputFilename;
            }
            catch (OperationCanceledException)
            {
                State = SessionState.Invalid;
                throw;
            }
            finally
            {
                Directory.Delete(_directory, true);
            }
        }
    }

    public enum SessionState
    {
        /// <summary>
        ///     Failed or canceled.
        /// </summary>
        Invalid,

        /// <summary>
        ///     Recording frames.
        /// </summary>
        Recording,

        /// <summary>
        ///     After recording frames, waiting for the completion of the recording.
        /// </summary>
        WaitForRecordingComplete,

        /// <summary>
        ///     Transcoding frames.
        /// </summary>
        Transcoding,

        /// <summary>
        ///     Exporting encoded frames (realtime mode).
        /// </summary>
        Exporting = Transcoding,

        /// <summary>
        ///     Completed.
        /// </summary>
        Completed
    }
}
