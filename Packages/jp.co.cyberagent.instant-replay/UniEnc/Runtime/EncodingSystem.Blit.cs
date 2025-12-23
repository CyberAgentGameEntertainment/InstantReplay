using System;
using UniEnc.Native;

namespace UniEnc
{
    partial class EncodingSystem
    {
        public bool IsBlitSupported()
        {
            lock (_lock)
            {
                _ = _handle ?? throw new ObjectDisposedException(nameof(EncodingSystem));

                unsafe
                {
                    return NativeMethods.unienc_is_blit_supported(
                        (PlatformEncodingSystem*)_handle.DangerousGetHandle());
                }
            }
        }
    }
}
