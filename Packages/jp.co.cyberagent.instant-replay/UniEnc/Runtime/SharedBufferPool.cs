// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AOT;
using UniEnc.Internal;
using UniEnc.Native;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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

        public unsafe bool TryAlloc(nuint size, out SharedBuffer buffer)
        {
            using var scope = _handle.GetScope();
            Native.SharedBuffer* bufPtr = null;
            byte* ptr = null;

            _lastException = SentinelException; // ignore exception
            var success = NativeMethods.unienc_shared_buffer_pool_alloc((Mutex*)scope.Handle, size, &bufPtr, &ptr,
                OnErrorPtr, default);

            if (!success || bufPtr == null || ptr == null)
            {
                buffer = default;
                return false;
            }

            buffer = new SharedBuffer((nint)bufPtr, ptr, (nint)size);
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

    public readonly struct SharedBuffer : IDisposable
    {
        private readonly Handle _handle;
        private readonly ushort _token;

        public bool IsValid => _handle.IsAlive && _handle.Token == _token;

        public NativeArray<byte> NativeArray => _handle.GetNativeArray(_token);

        public Span<byte> Span => _handle.GetSpan(_token);

        internal unsafe SharedBuffer(IntPtr handle, byte* ptr, nint length)
        {
            _handle = Handle.GetHandle(handle, ptr, length);
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

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            private AtomicSafetyHandle? _ash;
#endif
            private nint _length;
            private byte* _ptr;

            private Handle(IntPtr handle) : base(handle)
            {
            }

            public NativeArray<byte> GetNativeArray(ushort token)
            {
                if (token != Token || !IsAlive) throw new InvalidOperationException();
                var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(_ptr, (int)_length,
                    Allocator.None);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref array, _ash ??= AtomicSafetyHandle.Create());
#endif
                return array;
            }

            public Span<byte> GetSpan(ushort token)
            {
                if (token != Token || !IsAlive) throw new InvalidOperationException();
                if (!IsAlive) throw new InvalidOperationException();
                return new Span<byte>(_ptr, (int)_length);
            }

            public static Handle GetHandle(IntPtr handle, byte* ptr, nint length)
            {
                if (Pool.TryTake(out var bufferHandle))
                    bufferHandle.SetHandleForPooledHandle(handle);
                else
                    bufferHandle = new Handle(handle);

                bufferHandle._ptr = ptr;
                bufferHandle._length = length;

                return bufferHandle;
            }

            protected override void AddToPool()
            {
                Pool.Add(this);
            }

            protected override void ReleaseHandle(nint handle)
            {
                NativeMethods.unienc_free_shared_buffer((Native.SharedBuffer*)handle);
            }

            protected override void Reset()
            {
                _ptr = null;
                _length = 0;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (_ash is { } ash)
                {
                    _ash = null;
                    AtomicSafetyHandle.Release(ash);
                }
#endif
            }
        }
    }
}
