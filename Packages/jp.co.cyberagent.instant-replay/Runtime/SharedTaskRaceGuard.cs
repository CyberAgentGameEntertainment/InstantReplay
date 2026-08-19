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
                    // Record the generation this arming belongs to; the drain claims via a CAS from this
                    // exact value, so it can never claim a later generation of a recycled source.
                    source.armVersion = selfVersion;
                    source.next = _head;
                    if (_head != null) _head.prev = source;
                    _head = source;
                    source.inList = true;
                    armed = true;
                }
            }

            // Shared task completed in the tiny window before we could enlist: complete from it right away.
            if (!armed)
                source.TryCompleteFromSharedTask(_sharedTask, selfVersion);

            var awaiter = operation.ConfigureAwait(false).GetAwaiter();
            var action =
                PooledActionOnce<(SharedTaskRaceGuard, Source, int,
                    ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter)>.Get(static ctx =>
                {
                    var (guard, source, selfVersion, awaiter) = ctx;

                    Exception exception = null;
                    try
                    {
                        // Always observe the operation result, even if the shared task already won — this
                        // releases the operation's pooled state-machine box back to its pool.
                        awaiter.GetResult();
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }

                    // Claim this generation before touching the list. If the shared task side already won,
                    // the source may have been handed back to the pool, re-rented and re-armed by another
                    // guard; unlinking it here would rip it out of that guard's list (without its _gate)
                    // and its shared-task drain would then never complete it. Losing the claim also means
                    // there is nothing left to unlink: the winner either drained the list (inList is
                    // false) or never linked this source at all.
                    if (Interlocked.CompareExchange(ref source.SelfVersion, selfVersion + 1, selfVersion) !=
                        selfVersion) return;

                    // We won the claim, so nobody has completed this source yet: it cannot have been
                    // recycled and still belongs to this guard's generation, making Unlink safe.
                    guard.Unlink(source);

                    if (exception == null) source.core.SetResult(false);
                    else source.core.SetException(exception);
                }, (this, source, selfVersion, awaiter));

            awaiter.UnsafeOnCompleted(action.Wrapper);

            return new ValueTask(source, version);
        }

        private void OnSharedTaskCompleted()
        {
            Source claimed;
            lock (_gate)
            {
                _sharedTaskDrained = true;
                claimed = null;
                var s = _head;
                _head = null;
                while (s != null)
                {
                    var next = s.next;
                    // Mark the node as no longer in the list so a concurrent Unlink becomes a no-op.
                    s.inList = false;
                    s.prev = null;
                    s.next = null;

                    // Claim the node's current generation via its arm-time version. The only competing
                    // claim is the operation continuation's CAS from the same baseline, so exactly one
                    // side wins; if it already won it will complete the source itself (its Unlink has to
                    // wait for _gate, so it cannot complete — let alone recycle — the source while we
                    // hold the lock). A recycled source can never be claimed here because its
                    // SelfVersion has moved past armVersion for good.
                    if (Interlocked.CompareExchange(ref s.SelfVersion, s.armVersion + 1, s.armVersion) ==
                        s.armVersion)
                    {
                        // We own the node now; reuse its link to chain the claimed nodes.
                        s.next = claimed;
                        claimed = s;
                    }

                    s = next;
                }
            }

            // Complete outside the lock: SetResult may run the consumer's continuation inline.
            for (var s = claimed; s != null;)
            {
                // Capture next and detach before completing: completing may hand this source back to the
                // pool and have it re-rented elsewhere.
                var next = s.next;
                s.next = null;
                s.CompleteFromSharedTask(_sharedTask);
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
            // The value SelfVersion had when this source was armed; written under the owning guard's
            // _gate while linking and read under the same lock by the drain, which uses it as the CAS
            // baseline for its claim.
            public int armVersion;
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

            public void TryCompleteFromSharedTask(Task sharedTask, int selfVersion)
            {
                if (Interlocked.CompareExchange(ref SelfVersion, selfVersion + 1, selfVersion) != selfVersion)
                    return;

                CompleteFromSharedTask(sharedTask);
            }

            // Only call after winning the generation claim (the SelfVersion CAS) for this source.
            public void CompleteFromSharedTask(Task sharedTask)
            {
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
