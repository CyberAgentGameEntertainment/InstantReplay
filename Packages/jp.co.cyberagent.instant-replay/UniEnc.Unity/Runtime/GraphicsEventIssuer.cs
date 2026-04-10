using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;
using UnityEngine.Rendering;

namespace UniEnc.Unity
{
    internal static class GraphicsEventIssuer
    {
        private static IssueGraphicsEventDelegate _onIssueGraphicsEvent;
        private static nint? _onIssueGraphicsEventPtr;

        private static CommandBuffer _sharedCommandBuffer;

        public static nint OnIssueGraphicsEventPtr =>
            _onIssueGraphicsEventPtr ??=
                Marshal.GetFunctionPointerForDelegate(_onIssueGraphicsEvent ??= OnIssueGraphicsEvent);

        /// <summary>
        ///     Resolves a texture token to the original Texture, then frees the GCHandle.
        ///     Returns null if the texture has been garbage-collected or destroyed.
        /// </summary>
        private static Texture ResolveAndFreeTextureToken(nuint token)
        {
            if (token == 0) return null;
            var handle = GCHandle.FromIntPtr((nint)token);
            var texture = handle.Target as Texture;
            handle.Free();
            return texture;
        }

        [MonoPInvokeCallback(typeof(IssueGraphicsEventDelegate))]
        private static void OnIssueGraphicsEvent(nint eventFuncPtr, int eventId, nint context, nuint textureToken)
        {
            try
            {
                if (!PlayerLoopEntryPoint.IsMainThread)
                {
                    // not on main thread — marshal to main thread
                    if (!GraphicsEventArguments.Pool.TryDequeue(out var dequeued))
                        dequeued = new GraphicsEventArguments();

                    dequeued.EventFuncPtr = eventFuncPtr;
                    dequeued.EventId = eventId;
                    dequeued.Context = context;
                    dequeued.TextureToken = textureToken;

                    PlayerLoopEntryPoint.MainThreadContext.Post(static ctx =>
                    {
                        if (ctx is not GraphicsEventArguments args) return;
                        OnIssueGraphicsEvent(args.EventFuncPtr, args.EventId, args.Context, args.TextureToken);
                        GraphicsEventArguments.Pool.Enqueue(args);
                    }, dequeued);
                }
                else
                {
                    // Resolve the texture token to get the current native texture pointer.
                    var texture = ResolveAndFreeTextureToken(textureToken);

                    if (!texture)
                    {
                        // Texture was destroyed — skip and clean up native context.
                        VideoEncoder.UnsafeReleaseGraphicsEventContext(context);
                        return;
                    }

                    var nativePtr = texture.GetNativeTexturePtr();
                    if (nativePtr == IntPtr.Zero)
                    {
                        VideoEncoder.UnsafeReleaseGraphicsEventContext(context);
                        return;
                    }

                    // Write the resolved native pointer directly into the shared repr(C) context
                    // so the render-thread trampoline on the Rust side can read it.
                    unsafe
                    {
                        ((GraphicsEventContext*)context)->NativeTexturePtr = nativePtr;
                    }

                    _sharedCommandBuffer ??= new CommandBuffer();
                    _sharedCommandBuffer.Clear();
                    _sharedCommandBuffer.IssuePluginEventAndData(eventFuncPtr, eventId, context);
                    Graphics.ExecuteCommandBuffer(_sharedCommandBuffer);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                VideoEncoder.UnsafeReleaseGraphicsEventContext(context);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GraphicsEventContext
        {
            public nint NativeTexturePtr;
            public nint RustContext; // opaque — do not touch from C#
        }

        private delegate void IssueGraphicsEventDelegate(nint eventFuncPtr, int eventId, nint context,
            nuint textureToken);

        private class GraphicsEventArguments
        {
            public static readonly ConcurrentQueue<GraphicsEventArguments> Pool = new();
            public nint Context;
            public nint EventFuncPtr;
            public int EventId;
            public nuint TextureToken;
        }
    }
}
