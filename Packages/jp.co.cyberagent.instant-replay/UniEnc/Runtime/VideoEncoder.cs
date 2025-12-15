using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AOT;
using UniEnc.Native;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

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
        public ValueTask PushFrameAsync(in SharedBuffer frameData, uint width, uint height, double timestamp)
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

        public ValueTask PushFrameAsync(Texture source, bool isGammaWorkflow, double timestamp)
        {
            return PushFrameAsync(source.GetNativeTexturePtr(), (uint)source.width, (uint)source.height,
                source.graphicsFormat, isGammaWorkflow, timestamp);
        }

        public ValueTask PushFrameAsync(nint sourceTexturePtr, uint width, uint height, GraphicsFormat format,
            bool isGammaWorkflow, double timestamp)
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
                            (uint)format,
                            false,
                            isGammaWorkflow,
                            timestamp,
                            (nuint)OnIssueGraphicsEventPtr,
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
            public InputHandle(IntPtr handle) : base(handle)
            {
            }

            protected override bool ReleaseHandle()
            {
                NativeMethods.unienc_free_video_encoder_input((nint)handle);
                return true;
            }
        }

        private class OutputHandle : GeneralHandle
        {
            public OutputHandle(IntPtr handle) : base(handle)
            {
            }

            protected override bool ReleaseHandle()
            {
                NativeMethods.unienc_free_video_encoder_output((nint)handle);
                return true;
            }
        }

        #region Graphics Event

        private static Action<nint, int, nint> _onIssueGraphicsEvent;
        private static nint? _onIssueGraphicsEventPtr;

        private static nint OnIssueGraphicsEventPtr =>
            _onIssueGraphicsEventPtr ??=
                Marshal.GetFunctionPointerForDelegate(_onIssueGraphicsEvent ??= OnIssueGraphicsEvent);

        private static CommandBuffer _sharedCommandBuffer;

        [MonoPInvokeCallback(typeof(Action<nint, int, nint>))]
        private static void OnIssueGraphicsEvent(nint eventFuncPtr, int eventId, nint context)
        {
            try
            {
                if (!PlayerLoopEntryPoint.IsMainThread)
                {
                    // not on main thread
                    if (!GraphicsEventArguments.Pool.TryDequeue(out var dequeued))
                        dequeued = new GraphicsEventArguments();

                    dequeued.EventFuncPtr = eventFuncPtr;
                    dequeued.EventId = eventId;
                    dequeued.Context = context;

                    PlayerLoopEntryPoint.MainThreadContext.Post(static ctx =>
                    {
                        if (ctx is not GraphicsEventArguments args) return;
                        OnIssueGraphicsEvent(args.EventFuncPtr, args.EventId, args.Context);
                        GraphicsEventArguments.Pool.Enqueue(args);
                    }, dequeued);
                }
                else
                {
                    _sharedCommandBuffer ??= new CommandBuffer();
                    _sharedCommandBuffer.Clear();
                    _sharedCommandBuffer.IssuePluginEventAndData(eventFuncPtr, eventId, context);
                    Graphics.ExecuteCommandBuffer(_sharedCommandBuffer);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);

                // free
                unsafe
                {
                    NativeMethods.unienc_free_graphics_event_context((void*)context);
                }
            }
        }

        private class GraphicsEventArguments
        {
            public static readonly ConcurrentQueue<GraphicsEventArguments> Pool = new();
            public nint Context;
            public nint EventFuncPtr;
            public int EventId;
        }

        #endregion
    }
}
