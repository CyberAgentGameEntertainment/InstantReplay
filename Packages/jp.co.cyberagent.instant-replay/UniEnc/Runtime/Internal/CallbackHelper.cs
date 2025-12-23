using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using AOT;
using UniEnc.Native;

#if NET5_0
using System.Runtime.CompilerServices;
#endif

namespace UniEnc
{
    /// <summary>
    ///     Helper class for managing native callbacks with zero-allocation ValueTask support.
    /// </summary>
    internal static class CallbackHelper
    {
        public unsafe delegate void SimpleCallbackDelegate(void* userData, UniencErrorNative errorKind);

        private unsafe delegate void DataCallbackDelegate<in T>(T data, void* userData, UniencErrorNative error)
            where T : unmanaged;
        
#if !NET5_0
        private static readonly unsafe SimpleCallbackDelegate SSimpleCallbackDelegate = SimpleCallback;
        private static readonly unsafe DataCallbackDelegate<UniencSampleData> SSampleDataCallbackDelegate =
            SampleDataCallback;
#endif

        // ReSharper disable once RedundantUnsafeContext
        private static readonly unsafe IntPtr SimpleCallbackPtr =
#if NET5_0
            (IntPtr)(delegate* unmanaged[Cdecl]<void*, UniencErrorNative, void>)(&SimpleCallback);
#else
            Marshal.GetFunctionPointerForDelegate(SSimpleCallbackDelegate);
#endif


        // ReSharper disable once RedundantUnsafeContext
        private static readonly unsafe IntPtr DataCallbackPtr =
#if NET5_0
            (IntPtr)(delegate* unmanaged[Cdecl]<UniencSampleData, void*, UniencErrorNative, void>)(&SampleDataCallback);
#else
            Marshal.GetFunctionPointerForDelegate(SSampleDataCallbackDelegate);
#endif

        /// <summary>
        ///     Native callback for simple operations.
        /// </summary>
        [MonoPInvokeCallback(typeof(SimpleCallbackDelegate))]
#if NET5_0
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
#endif
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
        [MonoPInvokeCallback(typeof(DataCallbackDelegate<UniencSampleData>))]
#if NET5_0
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
#endif
        private static unsafe void SampleDataCallback(UniencSampleData sampleData, void* userData,
            UniencErrorNative error)
        {
            var handle = GCHandle.FromIntPtr((IntPtr)userData);
            var context = (DataCallbackContext<EncodedFrame>)handle.Target;
            handle.Free();

            if (error.kind == UniencErrorKind.Success)
            {
                if (sampleData.size > 0 && sampleData.data != null)
                {
                    var sourceSpan = new ReadOnlySpan<byte>(sampleData.data, (int)sampleData.size);
                    var frame = EncodedFrame.CreateWithCopy(sourceSpan, sampleData.timestamp, sampleData.kind);
                    context.SetResult(frame);
                }
                else
                {
                    var frame = EncodedFrame.CreateWithCopy(ReadOnlySpan<byte>.Empty, sampleData.timestamp,
                        sampleData.kind);
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
        internal static nuint GetSimpleCallbackPtr()
        {
            return (nuint)(nint)SimpleCallbackPtr;
        }

        /// <summary>
        ///     Gets the function pointer for data callbacks.
        /// </summary>
        internal static nuint GetDataCallbackPtr()
        {
            return (nuint)(nint)DataCallbackPtr;
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
        internal sealed class DataCallbackContext<T> : IValueTaskSource<T>
        {
            private static readonly ConcurrentQueue<DataCallbackContext<T>> Pool = new();

            private ManualResetValueTaskSourceCore<T> _core;

            public ValueTask<T> Task => new(this, _core.Version);

            public T GetResult(short token)
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

            public static DataCallbackContext<T> Rent()
            {
                if (Pool.TryDequeue(out var context))
                {
                    context._core.Reset();
                    return context;
                }

                return new DataCallbackContext<T>();
            }

            public void Return()
            {
                Pool.Enqueue(this);
            }

            public void SetResult(T result)
            {
                _core.SetResult(result);
            }

            public void SetException(Exception exception)
            {
                _core.SetException(exception);
            }
        }
    }
}
