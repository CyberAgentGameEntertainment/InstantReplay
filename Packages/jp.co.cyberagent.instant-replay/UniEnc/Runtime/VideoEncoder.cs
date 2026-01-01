using System;
using System.Threading.Tasks;
using UniEnc.Native;

namespace UniEnc
{
    /// <summary>
    ///     Encodes raw video frames to compressed format.
    /// </summary>
    public sealed class VideoEncoder : IDisposable
    {
        private readonly object _lock = new();
        private InputHandle _inputHandle;
        private OutputHandle _outputHandle;

        internal VideoEncoder(nint inputHandle, nint outputHandle)
        {
            _inputHandle = new InputHandle(inputHandle);
            _outputHandle = new OutputHandle(outputHandle);
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
        /// <param name="frameData">Raw frame data (BGRA)</param>
        /// <param name="width">Frame width in pixels</param>
        /// <param name="height">Frame height in pixels</param>
        /// <param name="timestamp">Frame timestamp in seconds</param>
        public ValueTask PushFrameAsync<T>(in SharedBuffer<T> frameData, uint width, uint height, double timestamp)
            where T : struct, IDisposable
        {
            lock (_lock)
            {
                _ = _inputHandle ?? throw new ObjectDisposedException(nameof(_inputHandle));

                var context = CallbackHelper.SimpleCallbackContext.Rent();

                try
                {
                    var contextHandle = CallbackHelper.CreateSendPtr(context);

                    unsafe
                    {
                        using var runtime = RuntimeWrapper.GetScope();

                        NativeMethods.unienc_video_encoder_push_shared_buffer(
                            runtime.Runtime,
                            _inputHandle.DangerousGetHandle(),
                            frameData.MoveOut(),
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
        }

        public ValueTask UnsafePushUnityFrameAsync(nint sourceTexturePtr, uint width, uint height,
            uint unityGraphicsFormat,
            bool isGammaWorkflow, double timestamp, nuint onIssueGraphicsEventPtr)
        {
            lock (_lock)
            {
                _ = _inputHandle ?? throw new ObjectDisposedException(nameof(_inputHandle));

                var context = CallbackHelper.SimpleCallbackContext.Rent();

                try
                {
                    var contextHandle = CallbackHelper.CreateSendPtr(context);

                    unsafe
                    {
                        using var runtime = RuntimeWrapper.GetScope();

                        NativeMethods.unienc_video_encoder_push_blit_source(
                            runtime.Runtime,
                            _inputHandle.DangerousGetHandle(),
                            (void*)sourceTexturePtr,
                            width,
                            height,
                            unityGraphicsFormat,
                            false,
                            isGammaWorkflow,
                            timestamp,
                            onIssueGraphicsEventPtr,
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
        }

        public static unsafe void UnsafeReleaseGraphicsEventContext(nint context)
        {
            NativeMethods.unienc_free_graphics_event_context((void*)context);
        }

        /// <summary>
        ///     Pulls an encoded frame from the encoder.
        /// </summary>
        /// <returns>The encoded frame, or null if no frames are available</returns>
        public ValueTask<EncodedFrame> PullFrameAsync()
        {
            lock (_lock)
            {
                _ = _outputHandle ?? throw new ObjectDisposedException(nameof(_outputHandle));

                var context = CallbackHelper.DataCallbackContext<EncodedFrame>.Rent();
                var contextHandle = CallbackHelper.CreateSendPtr(context);

                unsafe
                {
                    using var runtime = RuntimeWrapper.GetScope();

                    NativeMethods.unienc_video_encoder_pull(
                        runtime.Runtime,
                        _outputHandle.DangerousGetHandle(),
                        CallbackHelper.GetDataCallbackPtr(),
                        contextHandle);
                }

                return context.Task;
            }
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
                var input = _inputHandle;
                _inputHandle = null;
                input?.Dispose();
            }
        }

        private void Dispose(bool disposing)
        {
            lock (_lock)
            {
                var input = _inputHandle;
                _inputHandle = null;
                input?.Dispose();

                var output = _outputHandle;
                _outputHandle = null;
                output?.Dispose();
            }
        }

        ~VideoEncoder()
        {
            Dispose(false);
        }

        private class InputHandle : GeneralHandle
        {
            private readonly Utils.SafeHandleScope _runtimeScope = RuntimeWrapper.GetReferenceScope();
            public InputHandle(IntPtr handle) : base(handle)
            {
            }

            protected override unsafe bool ReleaseHandle()
            {
                using var _ = _runtimeScope;
                using var scope = RuntimeWrapper.GetScope();
                NativeMethods.unienc_free_video_encoder_input(scope.Runtime, (nint)handle);
                return true;
            }
        }

        private class OutputHandle : GeneralHandle
        {
            private readonly Utils.SafeHandleScope _runtimeScope = RuntimeWrapper.GetReferenceScope();
            public OutputHandle(IntPtr handle) : base(handle)
            {
            }

            protected override unsafe bool ReleaseHandle()
            {
                using var _ = _runtimeScope;
                using var scope = RuntimeWrapper.GetScope();
                NativeMethods.unienc_free_video_encoder_output(scope.Runtime,(nint)handle);
                return true;
            }
        }
    }
}
