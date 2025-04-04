// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine.Rendering;

namespace InstantReplay
{
    /// <summary>
    ///     A wrapper of delegate for AsyncGPUReadback callback that will be pooled after called once.
    /// </summary>
    /// <typeparam name="TContext"></typeparam>
    internal readonly struct PooledAsyncGPUReadbackCallback<TContext> : IDisposable
    {
        private static readonly Stack<Core> Pool = new();

        private readonly ushort _version;
        private readonly Core _core;

        public Action<AsyncGPUReadbackRequest> Wrapper
        {
            get
            {
                if (_core._version != _version) throw new InvalidOperationException();
                return _core.Wrapper;
            }
        }

        public static PooledAsyncGPUReadbackCallback<TContext> Get(
            Action<AsyncGPUReadbackRequest, TContext> callback, TContext context)
        {
            if (!Pool.TryPop(out var pooled))
                pooled = new Core();

            pooled.Set(callback, context);
            return new PooledAsyncGPUReadbackCallback<TContext>(pooled);
        }

        public void Dispose()
        {
            if (_core._version != _version) return;
            _core.Release();
            Pool.Push(_core);
        }

        private PooledAsyncGPUReadbackCallback(Core core)
        {
            _version = core._version;
            _core = core;
        }

        private class Core
        {
            private Action<AsyncGPUReadbackRequest, TContext> _callback;
            private TContext _context;
            public ushort _version;

            public Core()
            {
                Wrapper = request =>
                {
                    _callback!(request, _context);
                    Release();
                };
            }

            public Action<AsyncGPUReadbackRequest> Wrapper { get; }

            public void Set(Action<AsyncGPUReadbackRequest, TContext> callback, TContext context)
            {
                _callback = callback;
                _context = context;
            }

            public bool Release()
            {
                _callback = default;
                _context = default;
                _version++;
                return true;
            }
        }
    }
}
