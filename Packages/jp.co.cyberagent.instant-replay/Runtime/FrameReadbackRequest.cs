// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace InstantReplay
{
    internal readonly struct FrameReadbackRequest<TContext>
    {
        private static readonly Action<AsyncGPUReadbackRequest, FrameReadbackRequest<TContext>>
            OnAsyncGPUReadbackCompletedDelegate =
                OnAsyncGPUReadbackCompleted;

        private static readonly Action<FrameReadbackRequest<TContext>> SaveDelegate = Save;

        private readonly string _definiteFullPath;
        private readonly NativeArray<byte> _data;
        private readonly GraphicsFormat _format;
        private readonly uint _width;
        private readonly uint _height;
        private readonly Action<FrameReadbackRequest<TContext>, TContext, Exception> _onComplete;
        private readonly TContext _context;

        public FrameReadbackRequest(
            RenderTexture source,
            string definiteFullPath,
            TContext context,
            Action<FrameReadbackRequest<TContext>, TContext, Exception> onComplete)
        {
            _data = default;
            _format = source.graphicsFormat;
            _width = (uint)source.width;
            _height = (uint)source.height;
            _definiteFullPath = definiteFullPath;
            _onComplete = onComplete;
            _context = context;

            try
            {
                if (!SystemInfo.IsFormatSupported(_format, FormatUsage.ReadPixels))
                    throw new ArgumentException($"GraphicsFormat {_format} not supported for readback");

                var size = GraphicsFormatUtility.ComputeMipmapSize(source.width, source.height, source.graphicsFormat);
                var data = _data = new NativeArray<byte>(checked((int)size), Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);

                var callback =
                    PooledAsyncGPUReadbackCallback<FrameReadbackRequest<TContext>>.Get(
                        OnAsyncGPUReadbackCompletedDelegate,
                        this);

                AsyncGPUReadback.RequestIntoNativeArray(ref data, source, 0, callback.Wrapper);
            }
            catch (Exception ex)
            {
                UnsafeComplete(ex);
            }
        }

        private static void OnAsyncGPUReadbackCompleted(AsyncGPUReadbackRequest req,
            FrameReadbackRequest<TContext> frameReq)
        {
            try
            {
                if (!req.done)
                    throw new InvalidOperationException("AsyncGPUReadback has not completed.");
                if (req.hasError)
                    throw new Exception("AsyncGPUReadback failed.");

                var action = PooledActionOnce<FrameReadbackRequest<TContext>>.Get(SaveDelegate, frameReq);

                // NOTE: every call allocates 40B
                ThreadPool.UnsafeQueueUserWorkItem(static action => (action as Action)!(), action.Wrapper);
            }
            catch (Exception ex)
            {
                frameReq.UnsafeComplete(ex);
            }
        }

        private static void Save(FrameReadbackRequest<TContext> frameReq)
        {
            // run on thread pool
            Profiler.BeginSample("FrameReadbackRequest.Save");
            try
            {
                try
                {
                    var encoded = ImageConversion.EncodeNativeArrayToJPG(
                        frameReq._data,
                        frameReq._format,
                        frameReq._width,
                        frameReq._height);
                    frameReq._data.Dispose();

                    try
                    {
                        var path = frameReq._definiteFullPath;

                        Span<byte> src;
                        unsafe
                        {
                            src = new Span<byte>(encoded.GetUnsafePtr(), encoded.Length);
                        }

                        if (!MonoIOProxy.IsSupported)
                        {
                            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                                4096, FileOptions.SequentialScan);
                            file.Write(src);
                        }
                        else
                        {
                            var handlePtr = MonoIOProxy.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                                FileOptions.SequentialScan, out var error);
                            if (handlePtr == (IntPtr)(-1))
                                throw MonoIOProxy.GetException(path, error);

                            var handle = new SafeFileHandle(handlePtr, false);
                            try
                            {
                                // We use MonoIO (backend of FileStream) directly to bypass FileStream overhead.
                                // MonoIO only accepts managed arrays, not arbitrary pointers and spans.
                                // We need to copy an encoded NativeArray to a managed array. 
                                var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(4096, encoded.Length));
                                try
                                {
                                    while (src.Length > 0)
                                    {
                                        var l = Math.Min(buffer.Length, src.Length);
                                        src[..l].CopyTo(buffer);

                                        var offset = 0;

                                        while (offset < l)
                                        {
                                            var n = MonoIOProxy.Write(handle, buffer, offset, l - offset, out error);
                                            if (handlePtr == (IntPtr)(-1))
                                                throw MonoIOProxy.GetException(path, error);
                                            offset += n;
                                        }

                                        src = src[l..];
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(buffer);
                                }
                            }
                            finally
                            {
                                MonoIOProxy.Close(handle.DangerousGetHandle(), out error);
                                if (error != 0)
                                    throw MonoIOProxy.GetException(path, error);

                                handle.Dispose();
                                handle = null;
                            }
                        }
                    }
                    finally
                    {
                        encoded.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    frameReq.UnsafeComplete(ex);
                    return;
                }

                frameReq.UnsafeComplete();
            }
            finally
            {
                Profiler.EndSample();
            }
        }

        private void UnsafeComplete(Exception ex = default)
        {
            _onComplete(this, _context, ex);
        }
    }
}
