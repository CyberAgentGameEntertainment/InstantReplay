// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading.Tasks.Sources;

namespace InstantReplay
{
    internal class JniTaskSource<TResult> : IValueTaskSource<TResult>, IValueTaskSource
    {
        [ThreadStatic] private static Stack<JniTaskSource<TResult>> _pool;
        private ManualResetValueTaskSourceCore<TResult> _core;

        public short Token => _core.Version;

        void IValueTaskSource.GetResult(short token)
        {
            GetResult(token);
        }

        public TResult GetResult(short token)
        {
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                _core.Reset();
                _pool ??= new Stack<JniTaskSource<TResult>>();
                _pool.Push(this);
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

        public void SetResult(TResult result)
        {
            _core.SetResult(result);
        }

        public void SetException(Exception exception)
        {
            _core.SetException(exception);
        }

        public static JniTaskSource<TResult> Create()
        {
            _pool ??= new Stack<JniTaskSource<TResult>>();
            if (!_pool.TryPop(out var pooled)) pooled = new JniTaskSource<TResult>();
            return pooled;
        }
    }
}
