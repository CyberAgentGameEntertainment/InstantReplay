using System;
using System.Threading.Tasks;
using UniEnc.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

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
        ///     Gets whether the input has been completed (no more frames can be pushed).
        /// </summary>
        internal bool IsInputCompleted => _inputHandle == 0;

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
        /// <param name="frameData">Raw frame data (BGRA)</param>
        /// <param name="width">Frame width in pixels</param>
        /// <param name="height">Frame height in pixels</param>
        /// <param name="timestamp">Frame timestamp in seconds</param>
        public ValueTask PushFrameAsync(NativeArray<byte> frameData, uint width, uint height, double timestamp)
        {
            ThrowIfDisposed();

            if (_inputHandle == 0) return default;

            var context = CallbackHelper.SimpleCallbackContext.Rent();

            try
            {
                var contextHandle = CallbackHelper.CreateSendPtr(context);

                unsafe
                {
                    var runtime = RuntimeWrapper.Instance;

                    NativeMethods.unienc_video_encoder_push(
                        runtime.Runtime,
                        _inputHandle,
                        (nint)frameData.GetUnsafeReadOnlyPtr(),
                        (nuint)frameData.Length,
                        width,
                        height,
                        timestamp,
                        CallbackHelper.GetSimpleCallbackPtr(),
                        contextHandle);
                }

                return context.Task;
            }
            catch
            {
                context.Return();
                throw;
            }
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
                var runtime = RuntimeWrapper.Instance;

                NativeMethods.unienc_video_encoder_pull(
                    runtime.Runtime,
                    _outputHandle,
                    CallbackHelper.GetDataCallbackPtr(),
                    contextHandle);
            }

            return context.Task;
        }

        /// <summary>
        ///     Completes the encoding by disposing the input handle.
        ///     This signals that no more frames will be pushed.
        ///     The output handle remains valid to pull remaining encoded frames.
        /// </summary>
        public void CompleteInput()
        {
            lock (_lock)
            {
                if (_inputHandle != 0)
                {
                    unsafe
                    {
                        var runtime = RuntimeWrapper.Instance;

                        NativeMethods.unienc_free_video_encoder_input(runtime.Runtime, _inputHandle);
                    }

                    _inputHandle = 0;
                }
            }
        }

        private void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    if (_inputHandle != 0)
                    {
                        unsafe
                        {
                            var runtime = RuntimeWrapper.Instance;

                            NativeMethods.unienc_free_video_encoder_input(runtime.Runtime, _inputHandle);
                        }

                        _inputHandle = 0;
                    }

                    if (_outputHandle != 0)
                    {
                        unsafe
                        {
                            var runtime = RuntimeWrapper.Instance;

                            NativeMethods.unienc_free_video_encoder_output(runtime.Runtime, _outputHandle);
                        }

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
