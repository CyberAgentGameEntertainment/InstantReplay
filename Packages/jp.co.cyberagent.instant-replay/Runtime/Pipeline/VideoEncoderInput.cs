// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;
using UnityEngine;

namespace InstantReplay
{
    internal class VideoEncoderInput : IAsyncPipelineInput<LazyVideoFrameData>
    {
        private readonly VideoEncoder _videoEncoder;
        private readonly Task _transferTask;

        internal VideoEncoderInput(VideoEncoder videoEncoder, IAsyncPipelineInput<EncodedFrame> next)
        {
            _videoEncoder = videoEncoder ?? throw new ArgumentNullException(nameof(videoEncoder));
            _transferTask = TransferAsync(next);
        }

        private async Task TransferAsync(IAsyncPipelineInput<EncodedFrame> next)
        {
            try
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

                        try
                        {
                            await next.PushAsync(encodedFrame);
                        }
                        catch
                        {
                            encodedFrame.Dispose();
                            throw;
                        }

                    } while (true);
                }
                finally
                {
                    await next.CompleteAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public async ValueTask PushAsync(LazyVideoFrameData value)
        {
            using var frameData = await value.ReadbackTask;

            if (frameData.Length == 0)
                throw new ArgumentException("Frame data cannot be empty", nameof(value));

            try
            {
                await _videoEncoder.PushFrameAsync(frameData, (uint)value.Width, (uint)value.Height, value.Timestamp);
            }
            catch (ObjectDisposedException)
            {
                // ignore
            }
        }

        public ValueTask CompleteAsync(Exception exception = null)
        {
            _videoEncoder.CompleteInput();
            return new ValueTask(_transferTask);
        }

        public void Dispose()
        {
            _videoEncoder?.Dispose();
        }
    }
}
