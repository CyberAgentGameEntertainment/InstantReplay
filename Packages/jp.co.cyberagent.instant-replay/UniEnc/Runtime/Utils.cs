// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Runtime.InteropServices;

namespace UniEnc
{
    public static class Utils
    {
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
    }
}
