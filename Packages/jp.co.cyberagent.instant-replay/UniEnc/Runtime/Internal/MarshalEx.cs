// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Runtime.InteropServices;

namespace UniEnc
{
    internal static class MarshalEx
    {
        public static unsafe string PtrToStringUTF8(IntPtr ptr)
        {
            #if NETSTANDARD2_1 || NETSTANDARD2_1_OR_GREATER || NETCOREAPP1_1_OR_GREATER
            return Marshal.PtrToStringUTF8(ptr);
            #else
            
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            int length = 0;
            while (Marshal.ReadByte(ptr, length) != 0)
            {
                length++;
            }

            return System.Text.Encoding.UTF8.GetString((byte*)ptr, length);
            #endif
        }
    }
}
