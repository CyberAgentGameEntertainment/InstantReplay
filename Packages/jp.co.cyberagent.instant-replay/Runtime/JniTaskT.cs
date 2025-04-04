// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace InstantReplay
{
    /// <summary>
    ///     A task-like object that can be awaited.
    ///     Async method generated with JniTask attaches and detaches the JNI environment automatically whatever thread it
    ///     runs on.
    /// </summary>
    /// <typeparam name="TResult"></typeparam>
    [AsyncMethodBuilder(typeof(JniTaskAsyncMethodBuilder<>))]
    internal readonly struct JniTask<TResult>
    {
        private readonly ValueTask<TResult> _inner;

        public ValueTask<TResult> Inner => _inner;

        public ValueTaskAwaiter<TResult> GetAwaiter()
        {
            return _inner.GetAwaiter();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public JniTask(ValueTask<TResult> inner)
        {
            _inner = inner;
        }

        public ConfiguredValueTaskAwaitable<TResult> ConfigureAwait(bool continueOnCapturedContext)
        {
            return _inner.ConfigureAwait(continueOnCapturedContext);
        }
    }
}
