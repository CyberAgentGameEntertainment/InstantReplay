#nullable enable

using System;
using System.ComponentModel;
using System.Threading;
using UnityEngine;

namespace AndroidBindgen.InternalBindings.java.lang
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class Class
    {
        private static nint __ABG__Class_backingField;

        private static nint p_arrayType;

        public static nint __ABG__Class
        {
            get
            {
                var existing = __ABG__Class_backingField;
                if (existing != 0) return existing;

                var ptr = AndroidJNI.NewGlobalRef(AndroidJNI.FindClass(@"java/lang/Class"));
                existing = Interlocked.CompareExchange(ref __ABG__Class_backingField, ptr, (nint)0);
                if (existing != 0)
                {
                    AndroidJNI.DeleteGlobalRef(ptr);
                    return existing;
                }

                return ptr;
            }
        }

        public static IntPtr arrayType(IntPtr @this)
        {
            if (p_arrayType == 0)
                p_arrayType = AndroidJNI.GetMethodID(__ABG__Class, "arrayType", @"()Ljava/lang/Class;");
            return AndroidJNI.CallObjectMethod(@this, p_arrayType, Span<jvalue>.Empty);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class String
    {
        private static nint __ABG__Class_backingField;

        public static nint __ABG__Class
        {
            get
            {
                var existing = __ABG__Class_backingField;
                if (existing != 0) return existing;

                var ptr = AndroidJNI.NewGlobalRef(AndroidJNI.FindClass(@"java/lang/String"));
                existing = Interlocked.CompareExchange(ref __ABG__Class_backingField, ptr, (nint)0);
                if (existing != 0)
                {
                    AndroidJNI.DeleteGlobalRef(ptr);
                    return existing;
                }

                return ptr;
            }
        }
    }
}
