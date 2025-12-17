// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

namespace UniEnc.Unity
{
    public static class SharedBufferPoolExtensions
    {
        public static bool TryAllocAsNativeArray(this SharedBufferPool self, nuint size,
            out SharedBuffer<NativeArrayWrapper> buffer)
        {
            return self.TryAlloc(size, out buffer, static (ptr, length) => new NativeArrayWrapper(ptr, length));
        }
    }
}
