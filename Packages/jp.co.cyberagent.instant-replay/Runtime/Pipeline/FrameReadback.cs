// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using UniEnc;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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
        public static bool TryReadbackFrameAsync(Texture texture, ref SharedBufferPool bufferPool, out ValueTask<SharedBuffer> task)
        {
            if (texture == null)
                throw new ArgumentNullException(nameof(texture));
            
            // Check if format is supported
            if (!SystemInfo.IsFormatSupported(texture.graphicsFormat, FormatUsage.ReadPixels))
            {
                throw new ArgumentException(
                    $"GraphicsFormat {texture.graphicsFormat} not supported for readback");
            }

            // Get pooled context for zero allocation
            var context = ReadbackContext.Rent();

            try
            {
                // Calculate expected size
                var size = GraphicsFormatUtility.ComputeMipmapSize(texture.width, texture.height,
                    texture.graphicsFormat);

                if (!bufferPool.TryAlloc(size, out var buffer))
                {
                    task = default;
                    return false;
                }

                var nativeArray = buffer.NativeArray;

                // Store buffer in context for cleanup
                context.Buffer = buffer;

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

            task = context.Task;
            return true;
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
                context.SetResult(context.Buffer);
            }
            catch (Exception ex)
            {
                context.SetException(ex);
            }
        }

        /// <summary>
        ///     Reusable context for GPU readback that implements IValueTaskSource.
        /// </summary>
        private sealed class ReadbackContext : IValueTaskSource<SharedBuffer>
        {
            private static readonly ConcurrentQueue<ReadbackContext> Pool = new();

            private ManualResetValueTaskSourceCore<SharedBuffer> _core;
            private bool _ownsBuffer;
            private SharedBuffer _buffer;

            public ValueTask<SharedBuffer> Task => new(this, _core.Version);

            public SharedBuffer Buffer
            {
                get => _buffer;
                set
                {
                    _buffer = value;
                    _ownsBuffer = true;
                }
            }

            public SharedBuffer GetResult(short token)
            {
                try
                {
                    return _core.GetResult(token);
                }
                finally
                {
                    // Don't dispose the NativeArray here - let the consumer handle it
                    _ownsBuffer = false;
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
                context._ownsBuffer = false;
                return context;
            }

            public void SetResult(SharedBuffer result)
            {
                _core.SetResult(result);
            }

            public void SetException(Exception exception)
            {
                // Cleanup buffer on exception
                if (_ownsBuffer)
                {
                    try
                    {
                        _buffer.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal errors
                    }

                    _ownsBuffer = false;
                }

                _core.SetException(exception);
            }

            private void Return()
            {
                // Ensure any remaining buffer is disposed
                if (_ownsBuffer)
                    try
                    {
                        _buffer.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal errors
                    }

                _buffer = default;
                _ownsBuffer = false;
                Pool.Enqueue(this);
            }
        }
    }
}
