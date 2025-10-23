// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
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

            buffer = new SharedBuffer(SharedBufferHandle.GetHandle((nint)bufPtr, ptr, (nint)size));
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

    public struct SharedBuffer : IDisposable
    {
        private SharedBufferHandle _handle;
        private readonly ushort _token;

        public bool IsValid => _handle != null && _handle.Token == _token;

        public NativeArray<byte> NativeArray
        {
            get
            {
                ThrowIfInvalid();
                return _handle.NativeArray;
            }
        }

        public Span<byte> Span
        {
            get
            {
                ThrowIfInvalid();
                return _handle.Span;
            }
        }

        internal SharedBuffer(SharedBufferHandle handle)
        {
            _handle = handle;
            _token = handle.Token;
        }

        private void ThrowIfInvalid()
        {
            if (!IsValid)
                throw new ObjectDisposedException("SharedBuffer has already been moved out or disposed.");
        }

        public IntPtr MoveOut()
        {
            ThrowIfInvalid();

            var handle = _handle;
            _handle = null;
            return handle.MoveOut();
        }

        public unsafe void Dispose()
        {
            if (!IsValid)
                return;

            try
            {
                NativeMethods.unienc_free_shared_buffer((Native.SharedBuffer*)_handle.MoveOut());
            }
            catch
            {
                // ignore
            }
        }
    }

    internal unsafe class SharedBufferHandle : SafeHandle
    {
        private static readonly ConcurrentBag<SharedBufferHandle> Pool = new();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private AtomicSafetyHandle? _ash;
#endif
        private nint _length;

        // 0: normal, 1: pooled, 2: released
        private int _pooled;
        private byte* _ptr;

        private SharedBufferHandle(IntPtr handle) : base(IntPtr.Zero, true)
        {
            SetHandle(handle);
        }

        public NativeArray<byte> NativeArray
        {
            get
            {
                if (_pooled != 0) throw new InvalidOperationException();
                var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(_ptr, (int)_length,
                    Allocator.None);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref array, _ash ??= AtomicSafetyHandle.Create());
#endif
                return array;
            }
        }

        public Span<byte> Span
        {
            get
            {
                if (_pooled != 0) throw new InvalidOperationException();
                return new Span<byte>(_ptr, (int)_length);
            }
        }

        public ushort Token { get; private set; }

        public override bool IsInvalid => handle == IntPtr.Zero && _pooled == 0;

        public static SharedBufferHandle GetHandle(IntPtr handle, byte* ptr, nint length)
        {
            if (Pool.TryTake(out var bufferHandle))
            {
                bufferHandle.SetHandle(handle);
                if (Interlocked.CompareExchange(ref bufferHandle._pooled, 0, 1) != 1)
                    throw new InvalidOperationException();
            }
            else
            {
                bufferHandle = new SharedBufferHandle(handle);
            }

            bufferHandle._ptr = ptr;
            bufferHandle._length = length;

            return bufferHandle;
        }

        public IntPtr MoveOut()
        {
            if (Interlocked.CompareExchange(ref _pooled, 1, 0) != 0)
                throw new InvalidOperationException();

            _ptr = null;
            _length = 0;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (_ash is { } ash)
            {
                _ash = null;
                AtomicSafetyHandle.Release(ash);
            }
#endif

            if (++Token < ushort.MaxValue)
                Pool.Add(this);

            return handle;
        }

        protected override bool ReleaseHandle()
        {
            Token++;

            if (Interlocked.CompareExchange(ref _pooled, 2, 0) == 0)
                NativeMethods.unienc_free_shared_buffer((Native.SharedBuffer*)handle);

            _ptr = null;
            _length = 0;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (_ash is { } ash)
            {
                _ash = null;
                AtomicSafetyHandle.Release(ash);
            }
#endif

            return true;
        }
    }
}
