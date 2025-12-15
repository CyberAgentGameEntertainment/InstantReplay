// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    internal class VideoEncoderInput : IAsyncPipelineInput<LazyVideoFrameData>
    {
        private readonly Task _transferTask;
        private readonly VideoEncoder _videoEncoder;
        private readonly IAsyncPipelineInput<EncodedFrame> _next;

        internal VideoEncoderInput(VideoEncoder videoEncoder, IAsyncPipelineInput<EncodedFrame> next)
        {
            _videoEncoder = videoEncoder ?? throw new ArgumentNullException(nameof(videoEncoder));
            _next = next;
            _transferTask = TransferAsync(next);
        }

        public async ValueTask PushAsync(LazyVideoFrameData value)
        {
            switch (value.Kind)
            {
                case LazyVideoFrameData.DataKind.SharedBuffer:
                {
                    var frameData = await value.ReadbackTask;
                    try
                    {
                        if (!frameData.IsValid)
                            throw new ArgumentException("Frame data is invalid", nameof(value));

                        try
                        {
                            await _videoEncoder.PushFrameAsync(frameData, (uint)value.Width, (uint)value.Height,
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

                    break;
                }
                case LazyVideoFrameData.DataKind.BlitSource:
                {
                    if (!value.BlitSource)
                        throw new ArgumentException("Frame data is invalid", nameof(value));

                    try
                    {
                        await _videoEncoder.PushFrameAsync(value.NativeBlitSourceHandle, (uint)value.Width,
                            (uint)value.Height, value.BlitSourceFormat, value.IsGammaWorkflow, value.Timestamp);
                    }
                    catch (ObjectDisposedException)
                    {
                        // ignore
                    }

                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
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
            _next?.Dispose();
        }

        private async Task TransferAsync(IAsyncPipelineInput<EncodedFrame> next)
        {
            try
            {
                await TransferAsyncCore(next);
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
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
