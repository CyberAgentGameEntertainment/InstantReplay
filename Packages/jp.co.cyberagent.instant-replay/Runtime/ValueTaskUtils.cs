// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace InstantReplay
{
    internal static class ValueTaskUtils
    {
        public static ValueTask WhenAny(ValueTask task1, ValueTask task2)
        {
            return WhenAnySource.Rent(task1, task2);
        }

        private class WhenAnySource : IValueTaskSource
        {
            private static readonly ConcurrentQueue<WhenAnySource> Pool = new();
            private ManualResetValueTaskSourceCore<bool> _core;
            private int _selfVersion;

            public void GetResult(short token)
            {
                _core.GetResult(token);

                Pool.Enqueue(this);
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

            public static ValueTask Rent(ValueTask task1, ValueTask task2)
            {
                if (Pool.TryDequeue(out var source))
                    source._core.Reset();
                else
                    source = new WhenAnySource();

                var version = source._core.Version;
                var selfVersion = source._selfVersion;
                var awaiter1 = task1.GetAwaiter();

                var action1 = PooledActionOnce<(WhenAnySource, int, ValueTaskAwaiter)>.Get(static ctx =>
                {
                    var (source, selfVersion, awaiter1) = ctx;

                    try
                    {
                        awaiter1.GetResult();
                        if (Interlocked.CompareExchange(ref source._selfVersion, selfVersion + 1, selfVersion) !=
                            selfVersion) return;
                        source._core.SetResult(false);
                    }
                    catch (Exception ex)
                    {
                        if (Interlocked.CompareExchange(ref source._selfVersion, selfVersion + 1, selfVersion) !=
                            selfVersion) return;
                        source._core.SetException(ex);
                    }
                }, (source, selfVersion, awaiter1));

                awaiter1.OnCompleted(action1.Wrapper);

                var awaiter2 = task2.GetAwaiter();

                var action2 = PooledActionOnce<(WhenAnySource, int, ValueTaskAwaiter)>.Get(static ctx =>
                {
                    var (source, selfVersion, awaiter2) = ctx;

                    try
                    {
                        awaiter2.GetResult();
                        if (Interlocked.CompareExchange(ref source._selfVersion, selfVersion + 1, selfVersion) !=
                            selfVersion) return;
                        source._core.SetResult(false);
                    }
                    catch (Exception ex)
                    {
                        if (Interlocked.CompareExchange(ref source._selfVersion, selfVersion + 1, selfVersion) !=
                            selfVersion) return;
                        ILogger.LogExceptionCore(ex);
                        source._core.SetException(ex);
                    }
                }, (source, selfVersion, awaiter2));

                awaiter2.OnCompleted(action2.Wrapper);

                return new ValueTask(source, version);
            }
        }
    }
}
