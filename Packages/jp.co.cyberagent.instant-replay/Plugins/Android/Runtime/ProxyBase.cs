#nullable enable

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace AndroidBindgen
{
    public abstract unsafe class ProxyBase : AndroidJavaProxy, IAndroidJavaObject
    {
        private static delegate*<object, IntPtr> p_getRawProxy;

        static ProxyBase()
        {
            PrimitiveBoxer<sbyte>.Instance = new SByteBoxer();
            PrimitiveBoxer<char>.Instance = new CharBoxer();
            PrimitiveBoxer<double>.Instance = new DoubleBoxer();
            PrimitiveBoxer<float>.Instance = new FloatBoxer();
            PrimitiveBoxer<int>.Instance = new IntBoxer();
            PrimitiveBoxer<long>.Instance = new LongBoxer();
            PrimitiveBoxer<short>.Instance = new ShortBoxer();
            PrimitiveBoxer<bool>.Instance = new BooleanBoxer();
        }

        protected ProxyBase(AndroidJavaClass javaInterface) : base(javaInterface)
        {
        }

        #region IAndroidJavaObject Members

        public IntPtr GetRawObject()
        {
            if ((nint)p_getRawProxy == 0)
            {
                var method =
                    typeof(AndroidJavaProxy).GetMethod("GetRawProxy", BindingFlags.NonPublic | BindingFlags.Instance);

                var handle = method.MethodHandle;
                if (Application.isEditor)
                    // NOTE: In .NET 5 and later, RuntimeMethodHandle.GetFunctionPointer() will return **unmanaged** function pointer when its method has an [UnmanagedCallersOnly] attribute.
                    p_getRawProxy = (delegate*<object, IntPtr>)handle.GetFunctionPointer();
                else
                    // Unity 2023.1 and earlier don't support RuntimeMethodHandle.GetFunctionPointer() in IL2CPP.
                    // Also, RuntimeMethodHandle.GetFunctionPointer() returns null on some icall extern methods. (bug?)
                    // Calling a managed function pointer will be translated by IL2CPP as:
                    //
                    // (((RuntimeMethod*)managedFuncPtr)->methodPointer)(/* args */, (RuntimeMethod*)managedFuncPtr);
                    //
                    // RuntimeMethodHandle.Value is a pointer of MethodInfo (RuntimeMethod is alias to MethodInfo).
                    // MethodInfo* can be treated as managed function pointer.
                    p_getRawProxy = (delegate*<object, IntPtr>)handle.Value;
            }

            if ((nint)p_getRawProxy == 0) throw new InvalidOperationException();
            return p_getRawProxy(this);
        }

        public virtual void Dispose()
        {
        }

        #endregion

        protected IntPtr CreateInvocationError(Exception ex, bool methodNotFound)
        {
            Span<jvalue> args = stackalloc jvalue[2];
            args[0].j = GCHandle.ToIntPtr(GCHandle.Alloc(ex)).ToInt64();
            args[1].z = methodNotFound;
            return AndroidJNI.CallStaticObjectMethod(p_com_unity3d_player_ReflectionHelper,
                p_com_unity3d_player_ReflectionHelper_createInvocationError, args);
        }

        protected Exception CreateMethodMatchingFailureException(string methodName, Span<IntPtr> argClasses)
        {
            StringBuilder errorBuilder = new();
            errorBuilder.Append("Failed to match method: ");
            errorBuilder.Append(methodName);
            errorBuilder.Append(", parameters: ");
            var isFirst = true;
            foreach (var argClass in argClasses)
            {
                if (!isFirst) errorBuilder.Append(", ");
                isFirst = false;

                errorBuilder.Append(GetBoxedTypeIdentifier(argClass));
            }

            return new InvalidOperationException(errorBuilder.ToString());
        }

        protected static string GetBoxedTypeIdentifier(IntPtr @class)
        {
            return AndroidJNI.CallStringMethod(@class, p_java_lang_Class_getName, Span<jvalue>.Empty);
        }

        protected static bool IsArray(IntPtr @class)
        {
            return AndroidJNI.CallBooleanMethod(@class, p_java_lang_Class_isArray, Span<jvalue>.Empty);
        }

        protected static bool IsAssignableFrom(IntPtr classPtr, IntPtr fromClassPtr)
        {
            Span<jvalue> args = stackalloc jvalue[1];
            args[0].l = fromClassPtr;
            return AndroidJNI.CallBooleanMethod(classPtr, p_java_lang_Class_isAssignableFrom, args);
        }

        protected IntPtr GetArrayElementType(IntPtr arrayClass)
        {
            return AndroidJNI.CallObjectMethod(arrayClass, p_java_lang_Class_getComponentType, Span<jvalue>.Empty);
        }

        protected IntPtr GetArrayElement(IntPtr array, int index)
        {
            Span<jvalue> args = stackalloc jvalue[2];
            args[0].l = array;
            args[1].i = index;
            return AndroidJNI.CallStaticObjectMethod(p_java_lang_reflect_Array,
                p_java_lang_reflect_Array_getArrayElement, args);
        }

        public sealed override IntPtr Invoke(string methodName, IntPtr javaArgs)
        {
            if (TryInvokeDefaults(methodName, javaArgs, out var result, out var arrayLen))
                return result;

            Span<nint> args = stackalloc nint[arrayLen];
            Span<nint> argClasses = stackalloc nint[arrayLen];

            for (var i = 0; i < arrayLen; ++i)
            {
                args[i] = AndroidJNI.GetObjectArrayElement(javaArgs, i);
                argClasses[i] = AndroidJNI.GetObjectClass(args[i]);
            }

            Exception? error = null;
            try
            {
                if (InvokeCore(methodName, args, argClasses, out result)) return result;
            }
            catch (Exception ex) when (ex is not InvocationErrorException)
            {
                error = ex;
            }

            StringBuilder errorBuilder = new();
            errorBuilder.Append(GetType());
            errorBuilder.Append(".");
            errorBuilder.Append(methodName);
            errorBuilder.Append("(");
            for (var i = 0; i < argClasses.Length; ++i)
            {
                if (i != 0) errorBuilder.Append(",");
                errorBuilder.Append(GetBoxedTypeIdentifier(argClasses[i]));
            }

            errorBuilder.Append(")");

            if (error != null)
            {
                Exception thrown;
                try
                {
                    throw new TargetInvocationException(errorBuilder.ToString(), error);
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }

                return CreateInvocationError(thrown, false);
            }

            var innerException = new Exception($"No such proxy method: {errorBuilder}");
            return CreateInvocationError(innerException, true);
        }

        protected abstract bool InvokeCore(string methodName, ReadOnlySpan<nint> args, ReadOnlySpan<nint> argClasses,
            out IntPtr result);

        protected bool TryInvokeDefaults(string methodName, IntPtr javaArgs, out IntPtr result,
            out int arrayLength)
        {
            arrayLength = 0;
            if (javaArgs != IntPtr.Zero)
                arrayLength = AndroidJNI.GetArrayLength(javaArgs);

            if (arrayLength == 1 && methodName == "equals")
            {
                nint o = AndroidJNI.GetObjectArrayElement(javaArgs, 0);
                var obj = o == IntPtr.Zero
                    ? null
                    : new AndroidJavaObject(o);
                result = AndroidJNIHelper.Box(equals(obj));
                return true;
            }

            if (arrayLength == 0 && methodName == "hashCode")
            {
                result = AndroidJNIHelper.Box(hashCode());
                return true;
            }

            result = IntPtr.Zero;
            return false;
        }

        protected string UnboxString(IntPtr value)
        {
            return AndroidJNI.CallStringMethod(value, p_java_lang_String_toString, Span<jvalue>.Empty);
        }

        protected IntPtr BoxString(string? value)
        {
            return value != null ? AndroidJNI.NewString(value) : IntPtr.Zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nint GetMethodID(ref GlobalJavaObjectRef? classPtr, ref nint methodPtr, string @class,
            string name,
            string signature)
        {
            classPtr ??= new GlobalJavaObjectRef(AndroidJNI.FindClass(@class));
            if (methodPtr == 0) methodPtr = AndroidJNI.GetMethodID(classPtr, name, signature);
            return methodPtr;
        }

        protected T Unbox<T>(IntPtr boxed) where T : unmanaged
        {
            return PrimitiveBoxer<T>.Instance!.Unbox(boxed);
        }

        protected IntPtr Box<T>(T value) where T : unmanaged
        {
            return PrimitiveBoxer<T>.Instance!.Box(value);
        }

        protected static void ArrayCopy(IntPtr src, int srcPos, IntPtr dest, int destPos, int length)
        {
            var system = AndroidJNI.FindClass("java/lang/System");
            var arraycopy =
                AndroidJNI.GetStaticMethodID(system, "arraycopy", "(Ljava/lang/Object;ILjava/lang/Object;II)V");
            Span<jvalue> args = stackalloc jvalue[5];
            args[0].l = src;
            args[1].i = srcPos;
            args[2].l = dest;
            args[3].i = destPos;
            args[4].i = length;

            AndroidJNI.CallStaticVoidMethod(system, arraycopy, args);
        }

        #region Nested type: BooleanBoxer

        private class BooleanBoxer : PrimitiveBoxer<bool>
        {
            public override string ClassName => "java/lang/Boolean";
            public override string UnboxName => "booleanValue";
            public override string UnboxSignature => "()Z";
            public override string BoxSignature => "(Z)Ljava/lang/Boolean;";

            public override jvalue GetJValue(bool value)
            {
                return new jvalue { z = value };
            }

            public override bool CallMethod(IntPtr instance, nint methodId, Span<jvalue> args)
            {
                return AndroidJNI.CallBooleanMethod(instance, methodId, args);
            }
        }

        #endregion

        #region Nested type: CharBoxer

        private class CharBoxer : PrimitiveBoxer<char>
        {
            public override string ClassName => "java/lang/Character";
            public override string UnboxName => "charValue";
            public override string UnboxSignature => "()C";
            public override string BoxSignature => "(C)Ljava/lang/Character;";

            public override jvalue GetJValue(char value)
            {
                return new jvalue { c = value };
            }

            public override char CallMethod(IntPtr instance, nint methodId, Span<jvalue> args)
            {
                return AndroidJNI.CallCharMethod(instance, methodId, args);
            }
        }

        #endregion

        #region Nested type: DoubleBoxer

        private class DoubleBoxer : PrimitiveBoxer<double>
        {
            public override string ClassName => "java/lang/Double";
            public override string UnboxName => "doubleValue";
            public override string UnboxSignature => "()D";
            public override string BoxSignature => "(D)Ljava/lang/Double;";

            public override jvalue GetJValue(double value)
            {
                return new jvalue { d = value };
            }

            public override double CallMethod(IntPtr instance, nint methodId, Span<jvalue> args)
            {
                return AndroidJNI.CallDoubleMethod(instance, methodId, args);
            }
        }

        #endregion

        #region Nested type: FloatBoxer

        private class FloatBoxer : PrimitiveBoxer<float>
        {
            public override string ClassName => "java/lang/Float";
            public override string UnboxName => "floatValue";
            public override string UnboxSignature => "()F";
            public override string BoxSignature => "(F)Ljava/lang/Float;";

            public override jvalue GetJValue(float value)
            {
                return new jvalue { f = value };
            }

            public override float CallMethod(IntPtr instance, nint methodId, Span<jvalue> args)
            {
                return AndroidJNI.CallFloatMethod(instance, methodId, args);
            }
        }

        #endregion

        #region Nested type: IntBoxer

        private class IntBoxer : PrimitiveBoxer<int>
        {
            public override string ClassName => "java/lang/Integer";
            public override string UnboxName => "intValue";
            public override string UnboxSignature => "()I";
            public override string BoxSignature => "(I)Ljava/lang/Integer;";

            public override jvalue GetJValue(int value)
            {
                return new jvalue { i = value };
            }

            public override int CallMethod(IntPtr instance, nint methodId, Span<jvalue> args)
            {
                return AndroidJNI.CallIntMethod(instance, methodId, args);
            }
        }

        #endregion

        #region Nested type: LongBoxer

        private class LongBoxer : PrimitiveBoxer<long>
        {
            public override string ClassName => "java/lang/Long";
            public override string UnboxName => "longValue";
            public override string UnboxSignature => "()J";
            public override string BoxSignature => "(J)Ljava/lang/Long;";

            public override jvalue GetJValue(long value)
            {
                return new jvalue { j = value };
            }

            public override long CallMethod(IntPtr instance, nint methodId, Span<jvalue> args)
            {
                return AndroidJNI.CallLongMethod(instance, methodId, args);
            }
        }

        #endregion

        #region Nested type: PrimitiveBoxer

        private abstract class PrimitiveBoxer<T> where T : unmanaged
        {
            private GlobalJavaObjectRef? p_class;
            private nint p_unbox;
            private nint p_valueOf;
            public static PrimitiveBoxer<T>? Instance { get; set; }
            public abstract string ClassName { get; }
            public abstract string UnboxName { get; }
            public abstract string UnboxSignature { get; }
            public abstract string BoxSignature { get; }
            public abstract jvalue GetJValue(T value);
            public abstract T CallMethod(IntPtr instance, nint methodId, Span<jvalue> args);


            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static IntPtr Box(ref GlobalJavaObjectRef? classPtr, ref nint methodPtr, string @class,
                string signature,
                jvalue value)
            {
                if (classPtr == null) classPtr = new GlobalJavaObjectRef(AndroidJNI.FindClass(@class));
                if (methodPtr == 0) methodPtr = AndroidJNI.GetStaticMethodID(classPtr, "valueOf", signature);
                Span<jvalue> args = stackalloc jvalue[1];
                args[0] = value;
                return AndroidJNI.CallStaticObjectMethod(classPtr, methodPtr, args);
            }

            public IntPtr Box(T value)
            {
                return Box(ref p_class, ref p_valueOf, ClassName, BoxSignature, GetJValue(value));
            }

            public T Unbox(IntPtr boxed)
            {
                var methodId = GetMethodID(ref p_class, ref p_unbox, ClassName, UnboxName, UnboxSignature);
                return CallMethod(boxed, methodId, Span<jvalue>.Empty);
            }
        }

        #endregion

        #region Nested type: SByteBoxer

        private class SByteBoxer : PrimitiveBoxer<sbyte>
        {
            public override string ClassName => "java/lang/Byte";
            public override string UnboxName => "byteValue";
            public override string UnboxSignature => "()B";
            public override string BoxSignature => "(B)Ljava/lang/Byte;";

            public override jvalue GetJValue(sbyte value)
            {
                return new jvalue { b = value };
            }

            public override sbyte CallMethod(IntPtr instance, nint methodId, Span<jvalue> args)
            {
                return AndroidJNI.CallSByteMethod(instance, methodId, args);
            }
        }

        #endregion

        #region Nested type: ShortBoxer

        private class ShortBoxer : PrimitiveBoxer<short>
        {
            public override string ClassName => "java/lang/Short";
            public override string UnboxName => "shortValue";
            public override string UnboxSignature => "()S";
            public override string BoxSignature => "(S)Ljava/lang/Short;";

            public override jvalue GetJValue(short value)
            {
                return new jvalue { s = value };
            }

            public override short CallMethod(IntPtr instance, nint methodId, Span<jvalue> args)
            {
                return AndroidJNI.CallShortMethod(instance, methodId, args);
            }
        }

        #endregion

        #region com/unity3d/player/ReflectionHelper

        private static readonly GlobalJavaObjectRef p_com_unity3d_player_ReflectionHelper =
            new(AndroidJNI.FindClass("com/unity3d/player/ReflectionHelper"));

        private static readonly nint p_com_unity3d_player_ReflectionHelper_createInvocationError =
            AndroidJNI.GetStaticMethodID(p_com_unity3d_player_ReflectionHelper, "createInvocationError",
                "(JZ)Ljava/lang/Object;");

        #endregion

        #region java/lang/String

        private static readonly GlobalJavaObjectRef p_java_lang_String = new(AndroidJNI.FindClass("java/lang/String"));

        private static readonly nint p_java_lang_String_toString =
            AndroidJNI.GetMethodID(p_java_lang_String, "toString", "()Ljava/lang/String;");

        #endregion

        #region java/lang/reflect/String

        private static readonly GlobalJavaObjectRef p_java_lang_reflect_Array =
            new(AndroidJNI.FindClass("java/lang/reflect/Array"));

        private static readonly nint p_java_lang_reflect_Array_getArrayElement =
            AndroidJNI.GetStaticMethodID(p_java_lang_reflect_Array, "get", "(Ljava/lang/Object;I)Ljava/lang/Object;");

        #endregion

        #region java/lang/Class

        private static readonly GlobalJavaObjectRef p_java_lang_Class = new(AndroidJNI.FindClass("java/lang/Class"));

        private static readonly nint p_java_lang_Class_getName =
            AndroidJNI.GetMethodID(p_java_lang_Class, "getName", "()Ljava/lang/String;");

        private static readonly nint p_java_lang_Class_isArray =
            AndroidJNI.GetMethodID(p_java_lang_Class, "isArray", "()Z");

        private static readonly nint p_java_lang_Class_isAssignableFrom =
            AndroidJNI.GetMethodID(p_java_lang_Class, "isAssignableFrom", "(Ljava/lang/Class;)Z");

        private static readonly nint p_java_lang_Class_getComponentType = p_java_lang_Class_getComponentType =
            AndroidJNI.GetMethodID(p_java_lang_Class, "getComponentType", "()Ljava/lang/Class;");

        #endregion
    }
}
