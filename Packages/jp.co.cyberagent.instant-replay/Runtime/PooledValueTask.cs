// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace InstantReplay
{
    /// <summary>
    ///     A <see cref="ValueTask" />-like type whose async state machine box is pooled, so that an
    ///     <c>async PooledValueTask</c> method that suspends does not allocate on the heap every call.
    ///     This is a netstandard2.1 backport of the role played by
    ///     <c>PoolingAsyncValueTaskMethodBuilder</c> (.NET 6+), which is unavailable on Unity.
    /// </summary>
    /// <remarks>
    ///     Apply <c>async PooledValueTask</c> to hot-path methods. Because the
    ///     <see cref="AsyncMethodBuilderAttribute" /> is set on the type (not the method), this works under
    ///     C# 9 / Unity 2022.3 without requiring the C# 10 per-method builder feature.
    ///     The backing box is rented on the first suspension and returned to the pool when the resulting
    ///     <see cref="ValueTask" /> is awaited (i.e. <c>GetResult</c> is called exactly once), matching
    ///     single-consumption ValueTask semantics — the same contract <see cref="ValueTaskUtils" /> relies on.
    /// </remarks>
    [AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder))]
    internal readonly struct PooledValueTask
    {
        private readonly IValueTaskSource _source;
        private readonly Exception _exception;
        private readonly short _token;

        internal PooledValueTask(IValueTaskSource source, short token)
        {
            _source = source;
            _token = token;
            _exception = null;
        }

        private PooledValueTask(Exception exception)
        {
            _source = null;
            _token = 0;
            _exception = exception;
        }

        internal static PooledValueTask FromException(Exception exception)
        {
            return new PooledValueTask(exception);
        }

        /// <summary>
        ///     Converts to a plain <see cref="ValueTask" />. No heap allocation when source-backed
        ///     (the box is reused); the rare synchronous-exception path allocates a faulted task.
        /// </summary>
        public ValueTask AsValueTask()
        {
            if (_source != null) return new ValueTask(_source, _token);
            if (_exception != null) return new ValueTask(Task.FromException(_exception));
            return default;
        }

        public ValueTaskAwaiter GetAwaiter()
        {
            return AsValueTask().GetAwaiter();
        }
    }

    /// <summary>
    ///     Async method builder for <see cref="PooledValueTask" />. Pools the lifted state machine box.
    /// </summary>
    internal struct PooledValueTaskMethodBuilder
    {
        private StateMachineBox _box;
        private Exception _exception;

        public static PooledValueTaskMethodBuilder Create()
        {
            return default;
        }

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            // Run synchronously until the first suspension (or completion).
            stateMachine.MoveNext();
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
            // No-op: the box owns the state machine once lifted.
        }

        public void SetResult()
        {
            // _box == null means the method completed synchronously without ever suspending.
            _box?.SetResult();
        }

        public void SetException(Exception exception)
        {
            if (_box != null)
                _box.SetException(exception);
            else
                _exception = exception;
        }

        public PooledValueTask Task
        {
            get
            {
                if (_box != null)
                    return new PooledValueTask(_box, _box.Version);
                if (_exception != null)
                    return PooledValueTask.FromException(_exception);
                return default;
            }
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
        {
            awaiter.OnCompleted(GetBox(ref stateMachine).MoveNextAction);
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
        {
            awaiter.UnsafeOnCompleted(GetBox(ref stateMachine).MoveNextAction);
        }

        private StateMachineBox GetBox<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            // Reused on subsequent suspensions within the same invocation.
            if (_box != null) return _box;

            var box = StateMachineBox<TStateMachine>.Rent();

            // `this` aliases `stateMachine.<>t__builder`, so assigning _box here writes the back-reference
            // into the state machine *before* we copy it into the box below. From then on SetResult /
            // SetException / further suspensions route through this box.
            _box = box;
            box.StateMachine = stateMachine;
            return box;
        }

        private abstract class StateMachineBox : IValueTaskSource
        {
            // Continuations run inline (RunContinuationsAsynchronously stays false), matching WhenAnySource.
            protected ManualResetValueTaskSourceCore<bool> Core;

            public Action MoveNextAction { get; protected set; }

            public short Version => Core.Version;

            public abstract void GetResult(short token);

            public ValueTaskSourceStatus GetStatus(short token)
            {
                return Core.GetStatus(token);
            }

            public void OnCompleted(Action<object> continuation, object state, short token,
                ValueTaskSourceOnCompletedFlags flags)
            {
                Core.OnCompleted(continuation, state, token, flags);
            }

            public void SetResult()
            {
                Core.SetResult(true);
            }

            public void SetException(Exception exception)
            {
                Core.SetException(exception);
            }
        }

        private sealed class StateMachineBox<TStateMachine> : StateMachineBox
            where TStateMachine : IAsyncStateMachine
        {
            private static readonly ConcurrentQueue<StateMachineBox<TStateMachine>> Pool = new();

            public TStateMachine StateMachine;

            private StateMachineBox()
            {
                MoveNextAction = MoveNext;
            }

            public static StateMachineBox<TStateMachine> Rent()
            {
                return Pool.TryDequeue(out var box) ? box : new StateMachineBox<TStateMachine>();
            }

            private void MoveNext()
            {
                StateMachine.MoveNext();
            }

            public override void GetResult(short token)
            {
                try
                {
                    Core.GetResult(token);
                }
                finally
                {
                    Core.Reset();
                    StateMachine = default;
                    Pool.Enqueue(this);
                }
            }
        }
    }
}
