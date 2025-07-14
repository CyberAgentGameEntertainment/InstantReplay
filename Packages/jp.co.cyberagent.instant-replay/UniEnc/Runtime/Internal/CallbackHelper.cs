using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using AOT;

namespace UniEnc.Internal
{
    /// <summary>
    ///     Helper class for managing native callbacks with zero-allocation ValueTask support.
    /// </summary>
    internal static class CallbackHelper
    {
        private static readonly unsafe SimpleCallbackDelegate SSimpleCallbackDelegate = SimpleCallback;
        private static readonly unsafe DataCallbackDelegate SDataCallbackDelegate = DataCallback;

        private static readonly IntPtr SimpleCallbackPtr =
            Marshal.GetFunctionPointerForDelegate(SSimpleCallbackDelegate);

        private static readonly IntPtr DataCallbackPtr = Marshal.GetFunctionPointerForDelegate(SDataCallbackDelegate);

        /// <summary>
        ///     Native callback for simple operations.
        /// </summary>
        [MonoPInvokeCallback(typeof(SimpleCallbackDelegate))]
        private static unsafe void SimpleCallback(void* userData, UniencErrorNative error)
        {
            var handle = GCHandle.FromIntPtr((IntPtr)userData);
            var context = (SimpleCallbackContext)handle.Target;
            handle.Free();

            if (error.kind == UniencErrorKind.Success)
            {
                context.SetResult();
            }
            else
            {
                string errorMessage = null;
                if (error.message != null)
                    errorMessage = Marshal.PtrToStringUTF8((IntPtr)error.message);

                context.SetException(new UniEncException(error.kind, errorMessage ?? "Operation failed"));
            }
        }

        /// <summary>
        ///     Native callback for data operations.
        /// </summary>
        [MonoPInvokeCallback(typeof(DataCallbackDelegate))]
        private static unsafe void DataCallback(void* userData, byte* data, nuint size, double timestamp,
            bool isKeyFrame, UniencErrorNative error)
        {
            var handle = GCHandle.FromIntPtr((IntPtr)userData);
            var context = (DataCallbackContext)handle.Target;
            handle.Free();

            if (error.kind == UniencErrorKind.Success)
            {
                if (size > 0 && data != null)
                {
                    var sourceSpan = new ReadOnlySpan<byte>(data, (int)size);
                    var frame = EncodedFrame.CreateWithCopy(sourceSpan, timestamp, isKeyFrame);
                    context.SetResult(frame);
                }
                else
                {
                    var frame = EncodedFrame.CreateWithCopy(ReadOnlySpan<byte>.Empty, timestamp, isKeyFrame);
                    context.SetResult(frame);
                }
            }
            else
            {
                string errorMessage = null;
                if (error.message != null)
                    errorMessage = Marshal.PtrToStringUTF8((IntPtr)error.message);

                context.SetException(new UniEncException(error.kind, errorMessage ?? "Operation failed"));
            }
        }

        /// <summary>
        ///     Creates a GCHandle for the context and returns it as SendPtr.
        /// </summary>
        internal static SendPtr CreateSendPtr<T>(T context) where T : class
        {
            var handle = GCHandle.Alloc(context);
            return GCHandle.ToIntPtr(handle);
        }

        /// <summary>
        ///     Gets the function pointer for simple callbacks.
        /// </summary>
        internal static unsafe delegate* unmanaged[Cdecl]<void*, UniencErrorNative, void> GetSimpleCallbackPtr()
        {
            return (delegate* unmanaged[Cdecl]<void*, UniencErrorNative, void>)SimpleCallbackPtr;
        }

        /// <summary>
        ///     Gets the function pointer for data callbacks.
        /// </summary>
        internal static unsafe delegate* unmanaged[Cdecl]<void*, byte*, nuint, double, bool, UniencErrorNative, void>
            GetDataCallbackPtr()
        {
            return (delegate* unmanaged[Cdecl]<void*, byte*, nuint, double, bool, UniencErrorNative, void>)
                DataCallbackPtr;
        }

        /// <summary>
        ///     Reusable context for simple callbacks that only return an error.
        /// </summary>
        internal sealed class SimpleCallbackContext : IValueTaskSource
        {
            private static readonly ConcurrentQueue<SimpleCallbackContext> Pool = new();

            private ManualResetValueTaskSourceCore<object> _core;
            private GCHandle? _state;

            private SimpleCallbackContext()
            {
            }

            public ValueTask Task => new(this, _core.Version);

            public void GetResult(short token)
            {
                _core.GetResult(token);
                Return();
            }

            public ValueTaskSourceStatus GetStatus(short token)
            {
                return _core.GetStatus(token);
            }

            public void OnCompleted(Action<object> continuation, object state, short token,
                ValueTaskSourceOnCompletedFlags flags)
            {
                _core.OnCompleted(continuation, state, token, flags);
            }

            public static SimpleCallbackContext Rent(GCHandle? state = default)
            {
                if (Pool.TryDequeue(out var context))
                {
                    context._core.Reset();
                    context._state = state;
                    return context;
                }

                return new SimpleCallbackContext
                {
                    _state = state
                };
            }

            public void Return()
            {
                Pool.Enqueue(this);
            }

            public void SetResult()
            {
                if (_state.HasValue)
                {
                    _state.Value.Free();
                    _state = null;
                }

                _core.SetResult(null);
            }

            public void SetException(Exception exception)
            {
                if (_state.HasValue)
                {
                    _state.Value.Free();
                    _state = null;
                }

                _core.SetException(exception);
            }
        }

        /// <summary>
        ///     Reusable context for callbacks that return data.
        /// </summary>
        internal sealed class DataCallbackContext : IValueTaskSource<EncodedFrame>
        {
            private static readonly ConcurrentQueue<DataCallbackContext> Pool = new();

            private ManualResetValueTaskSourceCore<EncodedFrame> _core;

            public ValueTask<EncodedFrame> Task => new(this, _core.Version);

            public EncodedFrame GetResult(short token)
            {
                var result = _core.GetResult(token);
                Return();
                return result;
            }

            public ValueTaskSourceStatus GetStatus(short token)
            {
                return _core.GetStatus(token);
            }

            public void OnCompleted(Action<object> continuation, object state, short token,
                ValueTaskSourceOnCompletedFlags flags)
            {
                _core.OnCompleted(continuation, state, token, flags);
            }

            public static DataCallbackContext Rent()
            {
                if (Pool.TryDequeue(out var context))
                {
                    context._core.Reset();
                    return context;
                }

                return new DataCallbackContext();
            }

            public void Return()
            {
                Pool.Enqueue(this);
            }

            public void SetResult(EncodedFrame result)
            {
                _core.SetResult(result);
            }

            public void SetException(Exception exception)
            {
                _core.SetException(exception);
            }
        }

        private unsafe delegate void SimpleCallbackDelegate(void* userData, UniencErrorNative errorKind);

        private unsafe delegate void DataCallbackDelegate(void* userData, byte* data, nuint size, double timestamp,
            bool isKeyFrame, UniencErrorNative error);
    }
}
