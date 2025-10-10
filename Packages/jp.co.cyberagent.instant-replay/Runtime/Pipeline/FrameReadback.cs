// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace InstantReplay
{
    /// <summary>
    ///     Handles GPU frame readback operations for realtime encoding.
    /// </summary>
    internal static class FrameReadback
    {
        private static readonly Action<AsyncGPUReadbackRequest, ReadbackContext>
            OnAsyncGPUReadbackCompletedDelegate = OnAsyncGPUReadbackCompleted;

        /// <summary>
        ///     Reads back frame data from a RenderTexture asynchronously.
        /// </summary>
        public static ValueTask<NativeArray<byte>> ReadbackFrameAsync(Texture texture)
        {
            if (texture == null)
                throw new ArgumentNullException(nameof(texture));

            // Get pooled context for zero allocation
            var context = ReadbackContext.Rent();

            try
            {
                // Check if format is supported
                if (!SystemInfo.IsFormatSupported(texture.graphicsFormat, FormatUsage.ReadPixels))
                {
                    context.SetException(
                        new ArgumentException(
                            $"GraphicsFormat {texture.graphicsFormat} not supported for readback"));
                    return context.Task;
                }

                // Calculate expected size
                var size = GraphicsFormatUtility.ComputeMipmapSize(texture.width, texture.height,
                    texture.graphicsFormat);
                var nativeArray = new NativeArray<byte>(checked((int)size), Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);

                // Store NativeArray in context for cleanup
                context.NativeArray = nativeArray;

                // Get pooled callback
                var callback = PooledAsyncGPUReadbackCallback<ReadbackContext>.Get(
                    OnAsyncGPUReadbackCompletedDelegate, context);

                // Request asynchronous GPU readback
                AsyncGPUReadback.RequestIntoNativeArray(ref nativeArray, texture, 0, callback.Wrapper);
            }
            catch (Exception ex)
            {
                context.SetException(ex);
            }

            return context.Task;
        }

        private static void OnAsyncGPUReadbackCompleted(AsyncGPUReadbackRequest request, ReadbackContext context)
        {
            try
            {
                if (!request.done)
                {
                    context.SetException(new InvalidOperationException("AsyncGPUReadback has not completed"));
                    return;
                }

                if (request.hasError)
                {
                    context.SetException(new InvalidOperationException("GPU readback failed"));
                    return;
                }

                // Set successful result
                context.SetResult(context.NativeArray);
            }
            catch (Exception ex)
            {
                context.SetException(ex);
            }
        }

        /// <summary>
        ///     Reusable context for GPU readback that implements IValueTaskSource.
        /// </summary>
        private sealed class ReadbackContext : IValueTaskSource<NativeArray<byte>>
        {
            private static readonly ConcurrentQueue<ReadbackContext> Pool = new();

            private ManualResetValueTaskSourceCore<NativeArray<byte>> _core;
            private bool _hasNativeArray;
            private NativeArray<byte> _nativeArray;

            public ValueTask<NativeArray<byte>> Task => new(this, _core.Version);

            public NativeArray<byte> NativeArray
            {
                get => _nativeArray;
                set
                {
                    _nativeArray = value;
                    _hasNativeArray = true;
                }
            }

            public NativeArray<byte> GetResult(short token)
            {
                try
                {
                    return _core.GetResult(token);
                }
                finally
                {
                    // Don't dispose the NativeArray here - let the consumer handle it
                    _hasNativeArray = false;
                    Return();
                }
            }

            public ValueTaskSourceStatus GetStatus(short token)
            {
                return _core.GetStatus(token);
            }

            public void OnCompleted(Action<object> continuation, object state, short token,
                ValueTaskSourceOnCompletedFlags flags)
            {
                _core.OnCompleted(continuation, state, token, flags);
            }

            public static ReadbackContext Rent()
            {
                if (!Pool.TryDequeue(out var context))
                    context = new ReadbackContext();

                context._core.Reset();
                context._hasNativeArray = false;
                return context;
            }

            public void SetResult(NativeArray<byte> result)
            {
                _core.SetResult(result);
            }

            public void SetException(Exception exception)
            {
                // Cleanup NativeArray on exception
                if (_hasNativeArray && _nativeArray.IsCreated)
                {
                    try
                    {
                        _nativeArray.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal errors
                    }

                    _hasNativeArray = false;
                }

                _core.SetException(exception);
            }

            private void Return()
            {
                // Ensure any remaining NativeArray is disposed
                if (_hasNativeArray && _nativeArray.IsCreated)
                    try
                    {
                        _nativeArray.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal errors
                    }

                _nativeArray = default;
                _hasNativeArray = false;
                Pool.Enqueue(this);
            }
        }
    }
}
