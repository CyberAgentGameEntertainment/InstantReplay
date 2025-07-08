using System;
using System.Threading.Tasks;
using UniEnc.Internal;

namespace UniEnc
{
    /// <summary>
    ///     Encodes raw video frames to compressed format.
    /// </summary>
    public sealed class VideoEncoder : IDisposable
    {
        private readonly object _lock = new();
        private bool _disposed;
        private nint _inputHandle;
        private nint _outputHandle;

        internal VideoEncoder(nint inputHandle, nint outputHandle)
        {
            _inputHandle = inputHandle;
            _outputHandle = outputHandle;
        }

        /// <summary>
        ///     Releases all resources used by the video encoder.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     Pushes a raw video frame to the encoder.
        /// </summary>
        /// <param name="frameData">Raw frame data (e.g., RGBA or YUV)</param>
        /// <param name="width">Frame width in pixels</param>
        /// <param name="height">Frame height in pixels</param>
        /// <param name="timestamp">Frame timestamp in seconds</param>
        public ValueTask PushFrameAsync(byte[] frameData, uint width, uint height, double timestamp)
        {
            return PushFrameAsync(frameData.AsSpan(), width, height, timestamp);
        }

        /// <summary>
        ///     Pushes a raw video frame to the encoder.
        /// </summary>
        /// <param name="frameData">Raw frame data (e.g., RGBA or YUV)</param>
        /// <param name="width">Frame width in pixels</param>
        /// <param name="height">Frame height in pixels</param>
        /// <param name="timestamp">Frame timestamp in seconds</param>
        public ValueTask PushFrameAsync(ReadOnlySpan<byte> frameData, uint width, uint height, double timestamp)
        {
            ThrowIfDisposed();

            var context = CallbackHelper.SimpleCallbackContext.Rent();
            var contextHandle = CallbackHelper.CreateSendPtr(context);

            unsafe
            {
                fixed (byte* dataPtr = frameData)
                {
                    NativeMethods.unienc_video_encoder_push(
                        _inputHandle,
                        (nint)dataPtr,
                        (nuint)frameData.Length,
                        width,
                        height,
                        timestamp,
                        CallbackHelper.GetSimpleCallbackPtr(),
                        contextHandle);
                }
            }

            return context.Task;
        }

        /// <summary>
        ///     Pulls an encoded frame from the encoder.
        /// </summary>
        /// <returns>The encoded frame, or null if no frames are available</returns>
        public ValueTask<EncodedFrame> PullFrameAsync()
        {
            ThrowIfDisposed();

            var context = CallbackHelper.DataCallbackContext.Rent();
            var contextHandle = CallbackHelper.CreateSendPtr(context);

            unsafe
            {
                NativeMethods.unienc_video_encoder_pull(
                    _outputHandle,
                    CallbackHelper.GetDataCallbackPtr(),
                    contextHandle);
            }

            return context.Task;
        }

        private void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    if (_inputHandle != 0)
                    {
                        NativeMethods.unienc_free_video_encoder_input(_inputHandle);
                        _inputHandle = 0;
                    }

                    if (_outputHandle != 0)
                    {
                        NativeMethods.unienc_free_video_encoder_output(_outputHandle);
                        _outputHandle = 0;
                    }

                    _disposed = true;
                }
            }
        }

        ~VideoEncoder()
        {
            Dispose(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VideoEncoder));
        }
    }
}
