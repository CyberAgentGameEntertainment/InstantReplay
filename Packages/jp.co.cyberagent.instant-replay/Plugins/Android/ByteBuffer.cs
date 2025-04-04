// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

#nullable enable
using System;
using AndroidBindgen;
using UnityEngine;

#pragma warning disable CS0108
#pragma warning disable CS0162
#pragma warning disable CS0114

namespace java.nio
{
    public static class BindingExtensionsAddendum
    {
        private static nint p___ABG__Class_put_0;

        public static ByteBuffer? putWithoutReadback(this ByteBuffer @this, sbyte[] src, int offset, int length)
        {
            if (p___ABG__Class_put_0 == 0)
                p___ABG__Class_put_0 =
                    AndroidJNI.GetMethodID(ByteBuffer.__ABG__Class, @"put", @"([BII)Ljava/nio/ByteBuffer;");
            Span<jvalue> __ABG__args = stackalloc jvalue[3];
            var __ABG_marshal_0 = AndroidJNI.ToSByteArray(src);
            try
            {
                __ABG__args[0] = new jvalue { l = __ABG_marshal_0 };
                __ABG__args[1] = new jvalue { i = offset };
                __ABG__args[2] = new jvalue { i = length };
                return ByteBuffer.UnsafeFromRawObjectAndDeleteLocalRef(
                    AndroidJNI.CallObjectMethod(@this.GetRawObject(), p___ABG__Class_put_0, __ABG__args));
            }
            finally
            {
                AndroidJNIEx.CheckException();
                // global::AndroidBindgen.AndroidJNIEx.FromSByteArray(__ABG_marshal_0)?.CopyTo(@src, 0);
                AndroidJNI.DeleteLocalRef(__ABG_marshal_0);
            }
        }
    }
}
