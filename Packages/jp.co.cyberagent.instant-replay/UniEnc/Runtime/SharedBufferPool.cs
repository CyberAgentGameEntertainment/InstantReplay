// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AOT;
using UniEnc.Native;
using Mutex = UniEnc.Native.Mutex;

namespace UniEnc
{
    public readonly struct SharedBufferPool : IDisposable
    {
        #region static members

        private static CallbackHelper.SimpleCallbackDelegate _onError;
        private static nuint _onErrorPtr;
        [ThreadStatic] private static Exception _lastException;
        private static readonly Exception SentinelException = new();

        private static unsafe nuint OnErrorPtr
        {
            get
            {
                if (_onError == null)
                    _onErrorPtr = (nuint)(nint)Marshal.GetFunctionPointerForDelegate(_onError = OnError);

                return _onErrorPtr;
            }
        }

        #endregion

        private readonly Handle _handle;

        /// <summary>
        ///     Creates a new SharedBufferPool with an optional memory limit.
        /// </summary>
        /// <param name="limit">Limit in bytes. 0 means no limit.</param>
        /// <exception cref="Exception"></exception>
        public unsafe SharedBufferPool(nuint limit)
        {
            Mutex* poolPtr = null;
            _lastException = null;
            var success = NativeMethods.unienc_new_shared_buffer_pool(limit, &poolPtr, OnErrorPtr, null);

            if (!success || poolPtr == null)
                throw _lastException ?? new UniEncException(UniencErrorKind.Error, "Failed to create SharedBufferPool");

            _handle = new Handle((nint)poolPtr);
        }

        public unsafe bool TryAlloc(nuint size, out SharedBuffer<SpanWrapper> buffer)
        {
            return TryAlloc(size, out buffer, static (ptr, size) => new SpanWrapper((byte*)ptr, size));
        }

        public unsafe bool TryAlloc<T>(nuint size, out SharedBuffer<T> buffer, Func<nint, nint, T> createValue)
            where T : struct, IDisposable
        {
            using var scope = _handle.GetScope();
            SharedBuffer* bufPtr = null;
            byte* ptr = null;

            _lastException = SentinelException; // ignore exception
            var success = NativeMethods.unienc_shared_buffer_pool_alloc((Mutex*)scope.Handle, size, &bufPtr, &ptr,
                OnErrorPtr, default);

            if (!success || bufPtr == null || ptr == null)
            {
                buffer = default;
                return false;
            }

            buffer = new SharedBuffer<T>((nint)bufPtr, createValue((nint)ptr, (nint)size));
            return true;
        }

        [MonoPInvokeCallback(typeof(CallbackHelper.SimpleCallbackDelegate))]
        private static unsafe void OnError(void* userData, UniencErrorNative error)
        {
            try
            {
                if (_lastException != null)
                    return;

                throw new UniEncException(error.kind, Marshal.PtrToStringAnsi((nint)error.message));
            }
            catch (Exception ex)
            {
                _lastException = ex;
            }
        }

        private class Handle : GeneralHandle
        {
            public Handle(IntPtr handle) : base(handle)
            {
                SetHandle(handle);
            }

            protected override unsafe bool ReleaseHandle()
            {
                NativeMethods.unienc_free_shared_buffer_pool((Mutex*)handle);
                return true;
            }
        }

        public void Dispose()
        {
            _handle?.Dispose();
        }
    }

    public readonly struct SharedBuffer<T> : IDisposable where T : struct, IDisposable
    {
        private readonly Handle _handle;
        private readonly ushort _token;

        public bool IsValid => _handle.IsAlive && _handle.Token == _token;

        public T Value
        {
            get
            {
                if (!IsValid) throw new InvalidOperationException();
                return _handle.GetValue(_token);
            }
        }

        internal SharedBuffer(nint handle, T value)
        {
            _handle = Handle.GetHandle(handle, value);
            _token = _handle.Token;
        }

        public IntPtr MoveOut()
        {
            return _handle.MoveOut(_token);
        }

        public void Dispose()
        {
            _handle?.MoveOutAndRelease(_token);
        }

        private unsafe class Handle : PooledHandle
        {
            private static readonly ConcurrentBag<Handle> Pool = new();

            private T _value;

            private Handle(IntPtr handle) : base(handle)
            {
            }

            public T GetValue(ushort token)
            {
                if (token != Token || !IsAlive) throw new InvalidOperationException();
                if (!IsAlive) throw new InvalidOperationException();
                return _value;
            }

            public static Handle GetHandle(IntPtr handle, T value)
            {
                if (Pool.TryTake(out var bufferHandle))
                    bufferHandle.SetHandleForPooledHandle(handle);
                else
                    bufferHandle = new Handle(handle);

                bufferHandle._value = value;

                return bufferHandle;
            }

            protected override void AddToPool()
            {
                Pool.Add(this);
            }

            protected override void ReleaseHandle(nint handle)
            {
                NativeMethods.unienc_free_shared_buffer((SharedBuffer*)handle);
            }

            protected override void Reset()
            {
                _value.Dispose();
                _value = default;
            }
        }
    }
}
