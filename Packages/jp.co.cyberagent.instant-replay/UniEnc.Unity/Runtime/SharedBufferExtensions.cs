using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace UniEnc.Unity
{
    public static class SharedBufferPoolExtensions
    {
        public static bool TryAlloc(this SharedBufferPool self, nuint size,
            out SharedBuffer<NativeArrayWrapper> buffer)
        {
            return self.TryAlloc(size, out buffer, static (ptr, length) => new NativeArrayWrapper(ptr, length));
        }
    }

    public readonly struct NativeArrayWrapper : IDisposable
    {
        public readonly NativeArray<byte> Array;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private readonly AtomicSafetyHandle _handle;
#endif
        public unsafe NativeArrayWrapper(nint ptr, nint size)
        {
            var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>((byte*)ptr, (int)size,
                Allocator.None);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref array, _handle = AtomicSafetyHandle.Create());
#endif
            Array = array;
        }

        void IDisposable.Dispose()
        {
            AtomicSafetyHandle.Release(_handle);
        }
    }
}
