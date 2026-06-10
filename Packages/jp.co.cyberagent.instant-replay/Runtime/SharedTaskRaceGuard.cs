// --------------------------------------------------------------
// Copyright 2026 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace InstantReplay
{
    /// <summary>
    ///     Races short-lived (per-call) <see cref="ValueTask" />s against a single long-lived shared
    ///     <see cref="Task" />, surfacing whichever finishes first for each call.
    /// </summary>
    /// <remarks>
    ///     A naive <c>WhenAny(operation, sharedTask)</c> per call attaches a fresh continuation to the
    ///     long-lived shared task every time. Because that task stays pending for a long time, none of
    ///     those continuations ever fire: their callback objects are never released back to the pool and
    ///     they accumulate without bound on the task's continuation list — an allocation and a leak every
    ///     call.
    ///     This guard subscribes to the shared task <b>exactly once</b> (in the constructor). Per call it
    ///     rents a pooled <see cref="Source" /> and attaches one continuation to the ephemeral operation,
    ///     which always completes and so always releases its callback.
    ///     Multiple operations may be in flight concurrently, so every armed source is tracked in an
    ///     intrusive doubly-linked list (the list nodes are embedded in the pooled <see cref="Source" />,
    ///     so tracking allocates nothing per call). When the shared task completes it drains the whole
    ///     list; when an operation completes it unlinks itself. A single lock serializes list mutations.
    /// </remarks>
    internal sealed class SharedTaskRaceGuard
    {
        private static readonly ConcurrentQueue<Source> Pool = new();
        private readonly object _gate = new();
        private readonly Task _sharedTask;

        // Intrusive doubly-linked list of in-flight races; guarded by _gate.
        private Source _head;
        private bool _sharedTaskDrained;

        public SharedTaskRaceGuard(Task sharedTask)
        {
            _sharedTask = sharedTask ?? throw new ArgumentNullException(nameof(sharedTask));

            // Subscribe to the long-lived task exactly once; it can only complete once. ConfigureAwait(false)
            // so the completion is not forced onto whatever context constructed this guard.
            _sharedTask.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(OnSharedTaskCompleted);
        }

        public ValueTask Race(ValueTask operation)
        {
            // The shared task already finished/faulted: surface it (and never block on an operation whose
            // consumer is gone).
            if (_sharedTask.IsCompleted)
                return new ValueTask(_sharedTask);

            // Operation already done (e.g. encoder had capacity): no need to race or rent anything.
            if (operation.IsCompleted)
                return operation;

            var source = RentSource();
            var version = source.core.Version;
            var selfVersion = source.SelfVersion;

            bool armed;
            lock (_gate)
            {
                if (_sharedTaskDrained)
                {
                    armed = false;
                }
                else
                {
                    source.next = _head;
                    if (_head != null) _head.prev = source;
                    _head = source;
                    source.inList = true;
                    armed = true;
                }
            }

            // Shared task completed in the tiny window before we could enlist: complete from it right away.
            if (!armed)
                source.TryCompleteFromSharedTask(_sharedTask);

            var awaiter = operation.ConfigureAwait(false).GetAwaiter();
            var action =
                PooledActionOnce<(SharedTaskRaceGuard, Source, int,
                    ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter)>.Get(static ctx =>
                {
                    var (guard, source, selfVersion, awaiter) = ctx;

                    guard.Unlink(source);

                    try
                    {
                        // Always observe the operation result, even if the shared task already won — this
                        // releases the operation's pooled state-machine box back to its pool.
                        awaiter.GetResult();
                        if (Interlocked.CompareExchange(ref source.SelfVersion, selfVersion + 1, selfVersion) !=
                            selfVersion) return;
                        source.core.SetResult(false);
                    }
                    catch (Exception ex)
                    {
                        if (Interlocked.CompareExchange(ref source.SelfVersion, selfVersion + 1, selfVersion) !=
                            selfVersion) return;
                        source.core.SetException(ex);
                    }
                }, (this, source, selfVersion, awaiter));

            awaiter.UnsafeOnCompleted(action.Wrapper);

            return new ValueTask(source, version);
        }

        private void OnSharedTaskCompleted()
        {
            Source head;
            lock (_gate)
            {
                _sharedTaskDrained = true;
                head = _head;
                _head = null;
                // Mark every node as no longer in the list so a concurrent Unlink becomes a no-op. The
                // Next links are left intact here so we can walk the snapshot after releasing the lock.
                for (var s = head; s != null; s = s.next)
                    s.inList = false;
            }

            for (var s = head; s != null;)
            {
                // Capture next and detach before completing: completing may hand this source back to the
                // pool and have it re-rented elsewhere.
                var next = s.next;
                s.prev = null;
                s.next = null;
                s.TryCompleteFromSharedTask(_sharedTask);
                s = next;
            }
        }

        private void Unlink(Source source)
        {
            lock (_gate)
            {
                if (!source.inList) return;
                source.inList = false;

                if (source.prev != null) source.prev.next = source.next;
                else if (_head == source) _head = source.next;
                if (source.next != null) source.next.prev = source.prev;

                source.prev = null;
                source.next = null;
            }
        }

        private static Source RentSource()
        {
            if (Pool.TryDequeue(out var source))
                source.core.Reset();
            else
                source = new Source();

            source.prev = null;
            source.next = null;
            source.inList = false;
            return source;
        }

        private sealed class Source : IValueTaskSource
        {
            public ManualResetValueTaskSourceCore<bool> core;
            public bool inList;
            public Source next;

            // Intrusive list links; guarded by the owning guard's _gate.
            public Source prev;

            // Monotonic claim token shared by the operation and shared-task sides; never reset, so a stale
            // continuation from a previous (recycled) round always loses the CompareExchange and bails.
            public int SelfVersion;

            public void GetResult(short token)
            {
                try
                {
                    core.GetResult(token);
                }
                finally
                {
                    Pool.Enqueue(this);
                }
            }

            public ValueTaskSourceStatus GetStatus(short token)
            {
                return core.GetStatus(token);
            }

            public void OnCompleted(Action<object> continuation, object state, short token,
                ValueTaskSourceOnCompletedFlags flags)
            {
                core.OnCompleted(continuation, state, token, flags);
            }

            public void TryCompleteFromSharedTask(Task sharedTask)
            {
                var selfVersion = SelfVersion;
                if (Interlocked.CompareExchange(ref SelfVersion, selfVersion + 1, selfVersion) != selfVersion)
                    return;

                if (sharedTask.IsFaulted)
                {
                    var aggregate = sharedTask.Exception;
                    core.SetException(aggregate?.InnerException ?? (Exception)aggregate ??
                        new Exception("Shared task faulted."));
                }
                else if (sharedTask.IsCanceled)
                {
                    core.SetException(new OperationCanceledException());
                }
                else
                {
                    core.SetResult(false);
                }
            }
        }
    }
}
