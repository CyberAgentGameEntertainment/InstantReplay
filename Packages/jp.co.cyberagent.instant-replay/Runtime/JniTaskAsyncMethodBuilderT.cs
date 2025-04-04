// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal struct JniTaskAsyncMethodBuilder<T>
    {
        public static JniTaskAsyncMethodBuilder<T> Create()
        {
            return new JniTaskAsyncMethodBuilder<T>
            {
                _source = JniTaskSource<T>.Create()
            };
        }

        private JniTaskSource<T> _source;

        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            using var scope = JniScope.Create();
            stateMachine.MoveNext();
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
            // nop
        }

        public void SetException(Exception exception)
        {
            _source.SetException(exception);
        }

        public void SetResult(T result)
        {
            _source.SetResult(result);
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            awaiter.OnCompleted(PooledActionOnce<TStateMachine>.Get(static stateMachine =>
            {
                using var scope = JniScope.Create();
                stateMachine.MoveNext();
            }, stateMachine).Wrapper);
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            awaiter.OnCompleted(PooledActionOnce<TStateMachine>.Get(static stateMachine =>
            {
                using var scope = JniScope.Create();
                stateMachine.MoveNext();
            }, stateMachine).Wrapper);
        }

        public JniTask<T> Task => new(new ValueTask<T>(_source, _source.Token));
    }
}
