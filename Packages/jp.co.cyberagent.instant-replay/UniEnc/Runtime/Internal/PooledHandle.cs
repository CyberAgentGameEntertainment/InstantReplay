// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace UniEnc
{
    internal abstract class PooledHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        // 0: normal, 1: pooled, 2: released
        private int _pooled;

        protected PooledHandle(nint handle) : base(true)
        {
            SetHandle(handle);
        }

        public ushort Token { get; private set; }
        public bool IsAlive => _pooled == 0;

        protected sealed override bool ReleaseHandle()
        {
            Token++;

            if (Interlocked.CompareExchange(ref _pooled, 2, 0) == 0)
                ReleaseHandle(handle);

            Reset();
            return true;
        }

        public IntPtr MoveOut(ushort token)
        {
            if (token != Token) throw new InvalidOperationException();
            if (Interlocked.CompareExchange(ref _pooled, 1, 0) != 0)
                throw new InvalidOperationException();

            Reset();

            if (++Token < ushort.MaxValue)
                AddToPool();

            return handle;
        }

        protected abstract void AddToPool();
        protected abstract void ReleaseHandle(nint handle);
        protected abstract void Reset();

        protected void SetHandleForPooledHandle(nint handle)
        {
            SetHandle(handle);
            if (Interlocked.CompareExchange(ref _pooled, 0, 1) != 1)
                throw new InvalidOperationException();
        }

        public bool MoveOutAndRelease(ushort token)
        {
            if (token != Token || _pooled != 0) return false;
            ReleaseHandle(MoveOut(token));
            return true;
        }
    }
}
