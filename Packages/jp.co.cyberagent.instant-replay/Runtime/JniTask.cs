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
    [AsyncMethodBuilder(typeof(JniTaskAsyncMethodBuilder))]
    internal readonly struct JniTask
    {
        private readonly ValueTask _inner;

        public ValueTask Inner => _inner;

        public ValueTaskAwaiter GetAwaiter()
        {
            return _inner.GetAwaiter();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public JniTask(ValueTask inner)
        {
            _inner = inner;
        }

        public ConfiguredValueTaskAwaitable ConfigureAwait(bool continueOnCapturedContext)
        {
            return _inner.ConfigureAwait(continueOnCapturedContext);
        }
    }
}
