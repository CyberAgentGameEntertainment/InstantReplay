// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using UniEnc.Internal;
using UniEnc.Native;

namespace UniEnc
{
    public readonly struct BlitTargetHandle : IDisposable
    {
        private readonly Handle _handle;
        private readonly ushort _token;

        public bool IsValid => _handle.IsAlive && _handle.Token == _token;

        public BlitTargetHandle(nint value)
        {
            _handle = Handle.GetHandle(value);
            _token = _handle.Token;
        }

        public nint MoveOut()
        {
            return _handle.MoveOut(_token);
        }

        public void Dispose()
        {
            if (_handle == null) return;
            _handle?.MoveOutAndRelease(_token);
        }

        private class Handle : PooledHandle
        {
            private static readonly ConcurrentBag<Handle> Pool = new();

            private Handle(nint handle) : base(handle)
            {
            }

            public static Handle GetHandle(IntPtr handle)
            {
                if (Pool.TryTake(out var newHandle))
                    newHandle.SetHandleForPooledHandle(handle);
                else
                    newHandle = new Handle(handle);

                return newHandle;
            }

            protected override void AddToPool()
            {
                Pool.Add(this);
            }

            protected override void ReleaseHandle(nint handle)
            {
                unsafe
                {
                    NativeMethods.unienc_free_blit_target(new UniencBlitTargetData((BlitTargetType*)handle));
                }
            }

            protected override void Reset()
            {
            }
        }
    }
}
