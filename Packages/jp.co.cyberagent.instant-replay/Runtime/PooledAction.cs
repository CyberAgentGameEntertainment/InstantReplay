// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace InstantReplay
{
    /// <summary>
    ///     A wrapper of delegate that will be pooled after called once.
    /// </summary>
    /// <typeparam name="TContext"></typeparam>
    internal readonly struct PooledActionOnce<TContext> : IDisposable
    {
        private static readonly Stack<Core> Pool = new();

        private readonly ushort _version;
        private readonly Core _core;

        public Action Wrapper
        {
            get
            {
                if (_core._version != _version) throw new InvalidOperationException();
                return _core.Wrapper;
            }
        }

        public static PooledActionOnce<TContext> Get(
            Action<TContext> callback, TContext context)
        {
            if (!Pool.TryPop(out var pooled))
                pooled = new Core();

            pooled.Set(callback, context);
            return new PooledActionOnce<TContext>(pooled);
        }

        public void Dispose()
        {
            if (_core._version != _version) return;
            _core.Release();
            Pool.Push(_core);
        }

        private PooledActionOnce(Core core)
        {
            _version = core._version;
            _core = core;
        }

        private class Core
        {
            private Action<TContext> _callback;
            private TContext _context;
            public ushort _version;

            public Core()
            {
                Wrapper = () =>
                {
                    _callback!(_context);
                    Release();
                };
            }

            public Action Wrapper { get; }

            public void Set(Action<TContext> callback, TContext context)
            {
                _callback = callback;
                _context = context;
            }

            public void Release()
            {
                _callback = default;
                _context = default;
                _version++;
            }
        }
    }
}
