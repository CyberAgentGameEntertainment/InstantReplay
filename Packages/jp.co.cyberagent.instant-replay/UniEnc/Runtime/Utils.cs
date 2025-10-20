// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Runtime.InteropServices;
using AOT;
using UniEnc.Native;

namespace UniEnc
{
    public static class Utils
    {
        public delegate TRet OnDecodedCallback<in T, out TRet>(ReadOnlySpan<byte> data, nint width, nint height,
            nint pitch, T context);

        // flag to avoid re-entrance
        [ThreadStatic] private static bool _inUse;
        [ThreadStatic] private static Exception _currentException;

        public static TRet DecodeJpeg<T, TRet>(ReadOnlySpan<byte> data, OnDecodedCallback<T, TRet> callback, T context)
        {
            if (_inUse)
                throw new InvalidOperationException("DecodeJpeg cannot be called re-entrantly.");
            _inUse = true;
            try
            {
                var onDecodedPtr = Context<T, TRet>.OnDecodedPtr ??=
                    Marshal.GetFunctionPointerForDelegate(Context<T, TRet>.OnDecodedDelegate ??= OnDecoded);
                _currentException = null;
                Context<T, TRet>.Value = context;

                var callbackHandle = GCHandle.Alloc(callback);
                try
                {
                    unsafe
                    {
                        fixed (byte* dataPtr = data)
                        {
                            NativeMethods.unienc_jpeg_decode(dataPtr, (nuint)data.Length, (nuint)onDecodedPtr,
                                (void*)GCHandle.ToIntPtr(callbackHandle));
                        }
                    }
                }
                finally
                {
                    callbackHandle.Free();
                }

                var ret = Context<T, TRet>.ReturnValue;
                Context<T, TRet>.ReturnValue = default;

                if (_currentException != null)
                    throw _currentException;

                return ret;
            }
            finally
            {
                _inUse = false;
            }

            [MonoPInvokeCallback(typeof(Action<nint, nint, nint, nint, nint, nint>))]
            static unsafe void OnDecoded(nint error, nint data, nint width, nint height, nint pitch, nint userData)
            {
                try
                {
                    if (error != 0)
                        throw new Exception(Marshal.PtrToStringAnsi(error));

                    var callback = GCHandle.FromIntPtr(userData).Target as OnDecodedCallback<T, TRet>;
                    Context<T, TRet>.ReturnValue = callback!(new ReadOnlySpan<byte>((void*)data, (int)(height * pitch)),
                        width, height, pitch, Context<T, TRet>.Value);
                    Context<T, TRet>.Value = default;
                }
                catch (Exception ex)
                {
                    _currentException = ex;
                }
            }
        }

        internal static SafeHandleScope GetScope(this SafeHandle handle)
        {
            return new SafeHandleScope(handle);
        }

        internal readonly struct SafeHandleScope : IDisposable
        {
            public IntPtr Handle { get; }
            private readonly SafeHandle _safeHandle;
            
            public SafeHandleScope(SafeHandle safeHandle)
            {
                var success = false;
                safeHandle.DangerousAddRef(ref success);
                if (!success)
                    throw new ObjectDisposedException(nameof(safeHandle));
            
                Handle = safeHandle.DangerousGetHandle();
                _safeHandle = safeHandle;
            }
            public void Dispose()
            {
                _safeHandle.DangerousRelease();
            }
        }

        private static class Context<T, TRet>
        {
            [ThreadStatic] public static T Value;
            [ThreadStatic] public static TRet ReturnValue;
            public static Action<nint, nint, nint, nint, nint, nint> OnDecodedDelegate;
            public static nint? OnDecodedPtr;
        }
    }
}
