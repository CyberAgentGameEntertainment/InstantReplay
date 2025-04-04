#nullable enable

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AndroidBindgen
{
    public static unsafe class AndroidJNIEx
    {
        private static delegate*<AndroidJavaException, string, string, void> _androidJavaExceptionConstructor;

        private static delegate*<AndroidJavaException, string, string, void> AndroidJavaExceptionConstructor =>
            (nint)_androidJavaExceptionConstructor != 0
                ? _androidJavaExceptionConstructor
                : _androidJavaExceptionConstructor =
                    (delegate*<AndroidJavaException, string, string, void>)typeof(AndroidJavaException).GetConstructor(
                            BindingFlags.NonPublic | BindingFlags.Instance,
                            Type.DefaultBinder, new[] { typeof(string), typeof(string) }, null)!.MethodHandle
#if !UNITY_EDITOR && ENABLE_IL2CPP
                        .Value;
#else
                        .GetFunctionPointer();
#endif

        public static bool[]? FromBooleanArray(IntPtr array)
        {
            if (array == IntPtr.Zero) return null;
            try
            {
                return AndroidJNI.FromBooleanArray(array);
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(array);
            }
        }

        public static sbyte[]? FromSByteArray(IntPtr array)
        {
            if (array == IntPtr.Zero) return null;
            try
            {
                return AndroidJNI.FromSByteArray(array);
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(array);
            }
        }

        public static char[]? FromCharArray(IntPtr array)
        {
            if (array == IntPtr.Zero) return null;
            try
            {
                return AndroidJNI.FromCharArray(array);
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(array);
            }
        }

        public static short[]? FromShortArray(IntPtr array)
        {
            if (array == IntPtr.Zero) return null;
            try
            {
                return AndroidJNI.FromShortArray(array);
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(array);
            }
        }

        public static int[]? FromIntArray(IntPtr array)
        {
            if (array == IntPtr.Zero) return null;
            try
            {
                return AndroidJNI.FromIntArray(array);
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(array);
            }
        }

        public static long[]? FromLongArray(IntPtr array)
        {
            if (array == IntPtr.Zero) return null;
            try
            {
                return AndroidJNI.FromLongArray(array);
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(array);
            }
        }

        public static float[]? FromFloatArray(IntPtr array)
        {
            if (array == IntPtr.Zero) return null;
            try
            {
                return AndroidJNI.FromFloatArray(array);
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(array);
            }
        }

        public static double[]? FromDoubleArray(IntPtr array)
        {
            if (array == IntPtr.Zero) return null;
            try
            {
                return AndroidJNI.FromDoubleArray(array);
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(array);
            }
        }

        public static IntPtr[]? FromObjectArray(IntPtr array)
        {
            if (array == IntPtr.Zero) return null;
            try
            {
                return AndroidJNI.FromObjectArray(array);
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(array);
            }
        }

        public static IntPtr ConvertToJNIArray(Array? array)
        {
            if (array == null) return IntPtr.Zero;
            var elementType = array.GetType().GetElementType() ?? throw new ArgumentException(nameof(array));
            if (elementType.IsPrimitive)
            {
                if (elementType == typeof(int))
                    return AndroidJNI.ToIntArray((int[])array);
                if (elementType == typeof(bool))
                    return AndroidJNI.ToBooleanArray((bool[])array);
                if (elementType == typeof(byte))
                {
                    Debug.LogWarning(
                        "AndroidJNIHelper: converting Byte array is obsolete, use SByte array instead");
#pragma warning disable CS0618
                    return AndroidJNI.ToByteArray((byte[])array);
#pragma warning restore CS0618
                }

                if (elementType == typeof(sbyte))
                    return AndroidJNI.ToSByteArray((sbyte[])array);
                if (elementType == typeof(short))
                    return AndroidJNI.ToShortArray((short[])array);
                if (elementType == typeof(long))
                    return AndroidJNI.ToLongArray((long[])array);
                if (elementType == typeof(float))
                    return AndroidJNI.ToFloatArray((float[])array);
                if (elementType == typeof(double))
                    return AndroidJNI.ToDoubleArray((double[])array);
                return elementType == typeof(char) ? AndroidJNI.ToCharArray((char[])array) : IntPtr.Zero;
            }

            if (elementType == typeof(string))
            {
                var strArray = (string[])array;
                var length = array.GetLength(0);
                var num = AndroidJNI.FindClass("java/lang/String");
                var array1 = AndroidJNI.NewObjectArray(length, num, IntPtr.Zero);
                for (var index = 0; index < length; ++index)
                {
                    var localref = AndroidJNI.NewString(strArray[index]);
                    AndroidJNI.SetObjectArrayElement(array1, index, localref);
                    AndroidJNI.DeleteLocalRef(localref);
                }

                AndroidJNI.DeleteLocalRef(num);
                return array1;
            }

            if (typeof(IAndroidJavaObject).IsAssignableFrom(elementType)) // NOTE: accept derived types
            {
                var androidJavaObjectArray = (IAndroidJavaObject?[])array;
                var length = array.GetLength(0);
                var array2 = new IntPtr[length];
                var localref = AndroidJNI.FindClass("java/lang/Object");
                var type = IntPtr.Zero;
                for (var index = 0; index < length; ++index)
                {
                    var element = androidJavaObjectArray[index];
                    if (element != null)
                    {
                        var ptr = element.GetRawObject();
                        array2[index] = ptr;
                        var rawClass = AndroidJNI.GetObjectClass(ptr);
                        if (type == IntPtr.Zero)
                            type = rawClass;
                        else if (type != localref && !AndroidJNI.IsSameObject(type, rawClass))
                            type = localref;
                    }
                    else
                    {
                        array2[index] = IntPtr.Zero;
                    }
                }

                var objectArray = AndroidJNI.ToObjectArray(array2, type);
                AndroidJNI.DeleteLocalRef(localref);
                return objectArray;
            }

            if (elementType.IsSZArray) // NOTE: support jagged arrays
            {
                var length = array.GetLength(0);
                var localref = AndroidJNI.FindClass("java/lang/Object");
                var type = IntPtr.Zero;
                var ptrArray = new IntPtr[length];
                for (var i = 0; i < length; i++)
                    if (array.GetValue(i) is Array element)
                    {
                        var elementPtr = ConvertToJNIArray(element);
                        ptrArray[i] = elementPtr;
                        var rawClass = AndroidJNI.GetObjectClass(elementPtr);
                        if (type == IntPtr.Zero)
                            type = rawClass;
                        else if (type != localref && !AndroidJNI.IsSameObject(type, rawClass))
                            type = localref;
                    }
                    else
                    {
                        ptrArray[i] = IntPtr.Zero;
                    }

                var objectArray = AndroidJNI.ToObjectArray(ptrArray, type);
                AndroidJNI.DeleteLocalRef(localref);
                return objectArray;
            }

            throw new Exception("JNI; Unknown array type '" + elementType + "'");
        }

        public static IntPtr ToBooleanArray(bool[]? array)
        {
            // Unlike other To~~~Array() methods, AndroidJNI.ToBooleanArray() doesn't return 0 but a pointer to an empty array when the input is null.
            if (array == null) return IntPtr.Zero;
            return AndroidJNI.ToBooleanArray(array);
        }

        public static void CheckException()
        {
            var jthrowable = AndroidJNI.ExceptionOccurred();
            if (jthrowable != IntPtr.Zero)
            {
                AndroidJNI.ExceptionClear();
                var jthrowableClass = AndroidJNI.FindClass("java/lang/Throwable");
                var androidUtilLogClass = AndroidJNI.FindClass("android/util/Log");
                try
                {
                    var toStringMethodId =
                        AndroidJNI.GetMethodID(jthrowableClass, "toString", "()Ljava/lang/String;");
                    var getStackTraceStringMethodId = AndroidJNI.GetStaticMethodID(androidUtilLogClass,
                        "getStackTraceString", "(Ljava/lang/Throwable;)Ljava/lang/String;");
                    var exceptionMessage =
                        AndroidJNI.CallStringMethod(jthrowable, toStringMethodId, new jvalue[] { });
                    var jniArgs = new jvalue[1];
                    jniArgs[0].l = jthrowable;
                    var exceptionCallStack =
                        AndroidJNI.CallStaticStringMethod(androidUtilLogClass, getStackTraceStringMethodId, jniArgs);

                    var ex = (AndroidJavaException)RuntimeHelpers.GetUninitializedObject(typeof(AndroidJavaException));
                    AndroidJavaExceptionConstructor(ex, exceptionMessage, exceptionCallStack);

                    throw ex;
                }
                finally
                {
                    if (jthrowable != IntPtr.Zero) AndroidJNI.DeleteLocalRef(jthrowable);
                    if (jthrowableClass != IntPtr.Zero) AndroidJNI.DeleteLocalRef(jthrowableClass);
                    if (androidUtilLogClass != IntPtr.Zero) AndroidJNI.DeleteLocalRef(androidUtilLogClass);
                }
            }
        }
    }
}
