// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace InstantReplay
{
    internal readonly struct FrameReadbackRequest<TContext>
    {
        private readonly string _path;
        private readonly NativeArray<byte> _data;
        private readonly GraphicsFormat _format;
        private readonly uint _width;
        private readonly uint _height;
        private readonly Action<FrameReadbackRequest<TContext>, TContext, Exception> _onComplete;
        private readonly TContext _context;

        public FrameReadbackRequest(
            RenderTexture source,
            string path,
            TContext context,
            Action<FrameReadbackRequest<TContext>, TContext, Exception> onComplete)
        {
            _data = default;
            _format = source.graphicsFormat;
            _width = (uint)source.width;
            _height = (uint)source.height;
            _path = path;
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
                    PooledAsyncGPUReadbackCallback<FrameReadbackRequest<TContext>>.Get(OnAsyncGPUReadbackCompleted,
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

                var action = PooledActionOnce<FrameReadbackRequest<TContext>>.Get(Save, frameReq);

                Task.Run(action.Wrapper);
            }
            catch (Exception ex)
            {
                frameReq.UnsafeComplete(ex);
            }
        }

        private static void Save(FrameReadbackRequest<TContext> frameReq)
        {
            try
            {
                var encoded = ImageConversion.EncodeNativeArrayToJPG(
                    frameReq._data,
                    frameReq._format,
                    frameReq._width,
                    frameReq._height);
                frameReq._data.Dispose();

                using var file = File.OpenWrite(frameReq._path);
                unsafe
                {
                    file.Write(new Span<byte>(encoded.GetUnsafePtr(), encoded.Length));
                }

                encoded.Dispose();
            }
            catch (Exception ex)
            {
                frameReq.UnsafeComplete(ex);
                return;
            }

            frameReq.UnsafeComplete();
        }

        private void UnsafeComplete(Exception ex = default)
        {
            _onComplete(this, _context, ex);
        }
    }
}
