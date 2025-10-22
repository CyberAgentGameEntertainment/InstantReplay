// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace InstantReplay
{
    internal static class MonoIOProxy
    {
        private static readonly unsafe delegate*<
            string, // filename
            FileMode, // mode
            FileAccess, // access
            FileShare, // share
            FileOptions, // options
            out int, // error
            IntPtr> OpenDelegate;

        private static readonly unsafe delegate*<
            SafeHandle, // safeHandle
            byte[], // src
            int, // src_offset
            int, // count
            out int, // error 
            int> WriteDelegate;

        private static readonly unsafe delegate*<
            IntPtr, // handle
            out int, // error 
            bool> CloseDelegate;

        private static readonly unsafe delegate*<int, Exception> GetExceptionDelegate;
        private static readonly unsafe delegate*<string, int, Exception> GetExceptionWithPathDelegate;

        static unsafe MonoIOProxy()
        {
            try
            {
                var monoIOType = typeof(FileStream).Assembly.GetType("System.IO.MonoIO");
                var monoIOErrorType = typeof(FileStream).Assembly.GetType("System.IO.MonoIOError");

                OpenDelegate = (delegate*<
                        string,
                        FileMode,
                        FileAccess,
                        FileShare,
                        FileOptions,
                        out int,
                        IntPtr>)
                    monoIOType.GetMethod("Open", BindingFlags.Static | BindingFlags.Public)
                        .GetAotCompatibleFunctionPointer();

                WriteDelegate = (delegate*<
                        SafeHandle, // safeHandle
                        byte[], // src
                        int, // src_offset
                        int, // count
                        out int, // error 
                        int>)
                    monoIOType.GetMethod("Write", BindingFlags.Static | BindingFlags.Public)
                        .GetAotCompatibleFunctionPointer();

                CloseDelegate = (delegate*<
                        IntPtr, // handle
                        out int, // error 
                        bool>)
                    monoIOType.GetMethod("Close", BindingFlags.Static | BindingFlags.Public)
                        .GetAotCompatibleFunctionPointer();

                GetExceptionDelegate = (delegate*<int, Exception>)monoIOType
                    .GetMethod("GetException", new[] { monoIOErrorType }).GetAotCompatibleFunctionPointer();

                GetExceptionWithPathDelegate =
                    (delegate*<string, int, Exception>)monoIOType
                        .GetMethod("GetException", new[] { typeof(string), monoIOErrorType })
                        .GetAotCompatibleFunctionPointer();

                IsSupported = true;
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
                IsSupported = false;
            }
        }

        public static bool IsSupported { get; }

        public static unsafe IntPtr Open(
            string filename,
            FileMode mode,
            FileAccess access,
            FileShare share,
            FileOptions options,
            out int error)
        {
            return OpenDelegate(filename, mode, access, share, options, out error);
        }

        public static unsafe int Write(
            SafeHandle safeHandle,
            byte[] src,
            int src_offset,
            int count,
            out int error)
        {
            return WriteDelegate(safeHandle, src, src_offset, count, out error);
        }

        public static unsafe bool Close(
            IntPtr handle,
            out int error)
        {
            return CloseDelegate(handle, out error);
        }

        public static unsafe Exception GetException(int error)
        {
            return GetExceptionDelegate(error);
        }

        public static unsafe Exception GetException(
            string path,
            int error)
        {
            return GetExceptionWithPathDelegate(path, error);
        }
    }
}
