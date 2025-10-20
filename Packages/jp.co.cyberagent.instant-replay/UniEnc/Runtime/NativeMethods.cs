// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

namespace UniEnc.Native
{
    internal partial struct SendPtr
    {
        public struct T
        {
        }

        private unsafe SendPtr(nint ptr)
        {
            Item1 = (T*)ptr;
        }

        public static implicit operator SendPtr(nint ptr)
        {
            return new SendPtr(ptr);
        }

        public static unsafe implicit operator nint(SendPtr ptr)
        {
            return (nint)ptr.Item1;
        }
    }

    // opaque
    internal struct Mutex
    {
    }

    // opaque
    internal struct SharedBuffer
    {
    }
}

namespace UniEnc
{
    public enum DataKind : byte
    {
        Interpolated,
        Key,
        Metadata
    }
}
