// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    /// <summary>
    ///     Pushes a selected set of encoded frames into a muxer and completes it.
    ///     Shared by <see cref="RealtimeInstantReplaySession" /> and <see cref="DiskEncodedFrameBufferRecovery" /> so that
    ///     both follow the same completion protocol.
    /// </summary>
    internal static class EncodedFrameMuxer
    {
        /// <summary>
        ///     Value to pass to <c>ConfigureAwait</c> when awaiting <see cref="MuxAsync" />, and any other task that may
        ///     complete on the main thread in the course of an export.
        /// </summary>
        /// <remarks>
        ///     A continuation that does not resume on the captured context is queued to the thread pool whenever the task
        ///     completes on a thread with a non-default <see cref="System.Threading.SynchronizationContext" />, such as the
        ///     main thread of Unity. Web has no threads, so the thread pool never runs it and the export never finishes.
        ///     On Web, the continuation therefore resumes on the captured context, which is the main thread.
        /// </remarks>
        public const bool ContinueOnCapturedContext =
#if UNITY_WEBGL && !UNITY_EDITOR
            true;
#else
            false;
#endif

        /// <summary>
        ///     Muxes the frames of the given readers, which are typically the two streams of an
        ///     <see cref="EncodedFrameSelection" />. A null reader is treated as an empty stream. Every frame read is
        ///     disposed, whether or not it was accepted by the muxer. After a failure, no further frame is read from that
        ///     stream; the readers are not disposed here, so the caller releases the unread frames by disposing them.
        /// </summary>
        public static async ValueTask MuxAsync(Muxer muxer, IEncodedFrameReader videoFrames,
            IEncodedFrameReader audioFrames)
        {
            async ValueTask MuxVideoAsync()
            {
                Exception exception = null;
                try
                {
                    // Frames are read one at a time, so that a reader backed by storage holds only the frame being
                    // pushed. The muxer applies backpressure, which bounds how far reading runs ahead of it.
                    while (videoFrames != null && videoFrames.TryReadNext(out var frame))
                        using (frame)
                        {
                            await muxer.PushVideoDataAsync(frame).ConfigureAwait(false);
                        }
                }
                catch (Exception ex)
                {
                    exception = ex;
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
                try
                {
                    while (audioFrames != null && audioFrames.TryReadNext(out var frame))
                        using (frame)
                        {
                            await muxer.PushAudioDataAsync(frame).ConfigureAwait(false);
                        }
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                // same as video
                await muxer.FinishAudioAsync().ConfigureAwait(false);

                if (exception != null)
                    throw exception;
            }

            // Always observe both tasks even if one of them fails, so that the muxer is not
            // disposed (by the caller) while the other task is still using it.
            var whenAll = Task.WhenAll(StartTrack(MuxVideoAsync), StartTrack(MuxAudioAsync));
            try
            {
                await whenAll.ConfigureAwait(ContinueOnCapturedContext);
            }
            catch (Exception) when (whenAll.Exception is { InnerExceptions: { Count: > 1 } } aggregate)
            {
                // Awaiting Task.WhenAll rethrows only the first exception;
                // rethrow the AggregateException so that all failures are propagated.
                throw aggregate;
            }

            await muxer.CompleteAsync().ConfigureAwait(false);
        }

        /// <summary>
        ///     Starts muxing one track. A push completes synchronously while the muxer has room for the frame, so a track
        ///     started on the calling thread pushes most of the selection there before it first yields. The caller is
        ///     typically the main thread of Unity, where this stalls the application for the duration of the export.
        ///     Each track is therefore started on the thread pool, except on Web, which has no threads.
        /// </summary>
        private static Task StartTrack(Func<ValueTask> track)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return track().AsTask();
#else
            return Task.Run(() => track().AsTask());
#endif
        }
    }
}
