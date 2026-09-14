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
        ///     Muxes the given frames. Every frame is disposed, whether or not it was accepted by the muxer.
        /// </summary>
        public static async ValueTask MuxAsync(Muxer muxer, ReadOnlyMemory<EncodedFrame> videoFrames,
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
            var whenAll = Task.WhenAll(MuxVideoAsync().AsTask(), MuxAudioAsync().AsTask());
            try
            {
                await whenAll.ConfigureAwait(false);
            }
            catch (Exception) when (whenAll.Exception is { InnerExceptions: { Count: > 1 } } aggregate)
            {
                // Awaiting Task.WhenAll rethrows only the first exception;
                // rethrow the AggregateException so that all failures are propagated.
                throw aggregate;
            }

            await muxer.CompleteAsync().ConfigureAwait(false);
        }
    }
}
