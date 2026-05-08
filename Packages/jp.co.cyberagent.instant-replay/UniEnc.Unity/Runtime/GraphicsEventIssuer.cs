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

        private static readonly ConcurrentQueue<GraphicsEventArguments> PendingEvents = new();

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
            // Always queue and drain at PostLateUpdate. Posting via SynchronizationContext
            // would resume in EarlyUpdate, where GetNativeTexturePtr stalls the main thread
            // waiting for the previous frame's GPU work to complete.
            if (!GraphicsEventArguments.Pool.TryDequeue(out var args))
                args = new GraphicsEventArguments();

            args.EventFuncPtr = eventFuncPtr;
            args.EventId = eventId;
            args.Context = context;
            args.TextureToken = textureToken;

            PendingEvents.Enqueue(args);
        }

        internal static void FlushPendingEvents()
        {
            while (PendingEvents.TryDequeue(out var args))
            {
                try
                {
                    ProcessEvent(args.EventFuncPtr, args.EventId, args.Context, args.TextureToken);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    VideoEncoder.UnsafeReleaseGraphicsEventContext(args.Context);
                }
                finally
                {
                    args.EventFuncPtr = default;
                    args.EventId = default;
                    args.Context = default;
                    args.TextureToken = default;
                    GraphicsEventArguments.Pool.Enqueue(args);
                }
            }
        }

        private static void ProcessEvent(nint eventFuncPtr, int eventId, nint context, nuint textureToken)
        {
            var texture = ResolveAndFreeTextureToken(textureToken);

            if (!texture)
            {
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

            _sharedCommandBuffer ??= new CommandBuffer
            {
                name = "UniEnc.Unity.GraphicsEventIssuer"
            };
            _sharedCommandBuffer.Clear();
            _sharedCommandBuffer.IssuePluginEventAndData(eventFuncPtr, eventId, context);
            Graphics.ExecuteCommandBuffer(_sharedCommandBuffer);
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
