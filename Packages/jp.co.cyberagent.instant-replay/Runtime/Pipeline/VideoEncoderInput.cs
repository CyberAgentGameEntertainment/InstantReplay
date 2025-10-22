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
        private readonly Task _transferTask;
        private readonly VideoEncoder _videoEncoder;

        internal VideoEncoderInput(VideoEncoder videoEncoder, IAsyncPipelineInput<EncodedFrame> next)
        {
            _videoEncoder = videoEncoder ?? throw new ArgumentNullException(nameof(videoEncoder));
            _transferTask = TransferAsync(next);
        }

        public async ValueTask PushAsync(LazyVideoFrameData value)
        {
            var frameData = await value.ReadbackTask;
            try
            {
                if (!frameData.IsValid)
                    throw new ArgumentException("Frame data is invalid", nameof(value));

                try
                {
                    await _videoEncoder.PushFrameAsync(ref frameData, (uint)value.Width, (uint)value.Height,
                        value.Timestamp);
                }
                catch (ObjectDisposedException)
                {
                    // ignore
                }
            }
            finally
            {
                // If frame data is moved out by encoder, this will be no-op
                frameData.Dispose();
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

        private async Task TransferAsync(IAsyncPipelineInput<EncodedFrame> next)
        {
            try
            {
                await TransferAsyncCore(next);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private async Task TransferAsyncCore(IAsyncPipelineInput<EncodedFrame> next)
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
    }
}
