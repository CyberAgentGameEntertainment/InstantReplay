// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Reflection;

namespace InstantReplay
{
    internal static class MethodInfoExtensions
    {
        /// <summary>
        ///     Gets a managed function pointer of static method compatible with IL2CPP.
        /// </summary>
        /// <param name="method"></param>
        /// <returns></returns>
        public static IntPtr GetAotCompatibleFunctionPointer(this MethodInfo method)
        {
            if (!method.IsStatic) throw new ArgumentException("Method must be static.", nameof(method));

            var handle = method.MethodHandle;
#if !UNITY_EDITOR && ENABLE_IL2CPP
            // IL2CPP 2023.1 and earlier don't support RuntimeMethodHandle.GetFunctionPointer().
            // Also it returns null on icalls until 6000.0.17f. (issue: https://issuetracker.unity3d.com/issues/il2cpp-runtimemethodhandle-dot-getfunctionpointer-returns-0-when-used-on-certain-methods)
            // Instead, we can use RuntimeMethodHandle.Value as managed function pointer.

            // Calling a managed function pointer will be translated by IL2CPP to:
            //
            // (((RuntimeMethod*)managedFuncPtr)->methodPointer)(/* args */, (RuntimeMethod*)managedFuncPtr);
            //
            // RuntimeMethodHandle.Value is a pointer of MethodInfo (RuntimeMethod is alias to MethodInfo).
            // MethodInfo* can be treated as managed function pointer.

            return handle.Value;
#else
            // NOTE: In .NET 5 and later, RuntimeMethodHandle.GetFunctionPointer() will return **unmanaged** function pointer when its method has an [UnmanagedCallersOnly] attribute.
            return handle.GetFunctionPointer();
#endif
        }
    }
}
