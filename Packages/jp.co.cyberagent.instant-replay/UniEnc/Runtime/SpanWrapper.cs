// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;

namespace UniEnc
{
    public readonly unsafe struct SpanWrapper : IDisposable
    {
        private readonly byte* _ptr;
        private readonly nint _length;

        public SpanWrapper(byte* ptr, nint length)
        {
            _ptr = ptr;
            _length = length;
        }

        public Span<byte> UnsafeGetSpan()
        {
            return new Span<byte>(_ptr, (int)_length);
        }

        public void Dispose()
        {
        }
    }
}
