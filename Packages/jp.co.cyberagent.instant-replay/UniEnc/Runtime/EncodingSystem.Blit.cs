using System;
using System.Runtime.InteropServices;
using System.Threading;
using AOT;
using UniEnc.Native;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

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
                    return NativeMethods.unienc_is_blit_supported((PlatformEncodingSystem*)_handle.DangerousGetHandle());
                }
            }
        }
    }
}
