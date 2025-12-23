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
        private static Action<nint, int, nint> _onIssueGraphicsEvent;
        private static nint? _onIssueGraphicsEventPtr;

        private static CommandBuffer _sharedCommandBuffer;

        public static nint OnIssueGraphicsEventPtr =>
            _onIssueGraphicsEventPtr ??=
                Marshal.GetFunctionPointerForDelegate(_onIssueGraphicsEvent ??= OnIssueGraphicsEvent);

        [MonoPInvokeCallback(typeof(Action<nint, int, nint>))]
        private static void OnIssueGraphicsEvent(nint eventFuncPtr, int eventId, nint context)
        {
            try
            {
                if (!PlayerLoopEntryPoint.IsMainThread)
                {
                    // not on main thread
                    if (!GraphicsEventArguments.Pool.TryDequeue(out var dequeued))
                        dequeued = new GraphicsEventArguments();

                    dequeued.EventFuncPtr = eventFuncPtr;
                    dequeued.EventId = eventId;
                    dequeued.Context = context;

                    PlayerLoopEntryPoint.MainThreadContext.Post(static ctx =>
                    {
                        if (ctx is not GraphicsEventArguments args) return;
                        OnIssueGraphicsEvent(args.EventFuncPtr, args.EventId, args.Context);
                        GraphicsEventArguments.Pool.Enqueue(args);
                    }, dequeued);
                }
                else
                {
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

        private class GraphicsEventArguments
        {
            public static readonly ConcurrentQueue<GraphicsEventArguments> Pool = new();
            public nint Context;
            public nint EventFuncPtr;
            public int EventId;
        }
    }
}
