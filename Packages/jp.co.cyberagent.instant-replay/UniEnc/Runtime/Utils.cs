// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Runtime.InteropServices;
using AOT;

namespace UniEnc
{
    public static class Utils
    {
        public delegate TRet OnDecodedCallback<in T, out TRet>(ReadOnlySpan<byte> data, nint width, nint height,
            nint pitch, T context);

        [ThreadStatic] private static Exception _currentException;

        public static TRet DecodeJpeg<T, TRet>(ReadOnlySpan<byte> data, OnDecodedCallback<T, TRet> callback, T context)
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

            return ret;

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

        private static class Context<T, TRet>
        {
            [ThreadStatic] public static T Value;
            [ThreadStatic] public static TRet ReturnValue;
            public static Action<nint, nint, nint, nint, nint, nint> OnDecodedDelegate;
            public static nint? OnDecodedPtr;
        }
    }
}
