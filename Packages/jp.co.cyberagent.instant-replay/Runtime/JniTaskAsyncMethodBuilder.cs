// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal struct JniTaskAsyncMethodBuilder
    {
        public static JniTaskAsyncMethodBuilder Create()
        {
            return new JniTaskAsyncMethodBuilder
            {
                _source = JniTaskSource<bool>.Create()
            };
        }

        private JniTaskSource<bool> _source;

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

        public void SetResult()
        {
            _source.SetResult(false);
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

        public JniTask Task => new(new ValueTask(_source, _source.Token));
    }
}
