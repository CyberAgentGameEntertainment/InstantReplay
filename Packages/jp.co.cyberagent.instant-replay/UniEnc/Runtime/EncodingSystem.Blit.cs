using System;
using System.Threading.Tasks;
using UniEnc.Internal;
using UniEnc.Native;
using UnityEngine;
using UnityEngine.Rendering;

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
                    return NativeMethods.unienc_is_blit_supported((void*)_handle.DangerousGetHandle());
                }
            }
        }

        public unsafe ValueTask<BlitTargetHandle> BlitAsync(CommandBuffer cmd, Texture source, uint destWidth,
            uint destHeight, bool flipVertically)
        {
            lock (_lock)
            {
                _ = _handle ?? throw new ObjectDisposedException(nameof(EncodingSystem));

                void* eventFuncPtr = null;
                uint eventId = 0;
                void* eventDataPtr = null;
                var context = CallbackHelper.DataCallbackContext<BlitTargetHandle>.Rent();
                var contextHandle = CallbackHelper.CreateSendPtr(context);
                var task = context.Task;

                {
                    using var runtime = RuntimeWrapper.GetScope();
                    var success = NativeMethods.unienc_new_blit_closure(
                        runtime.Runtime,
                        (void*)_handle.DangerousGetHandle(),
                        (void*)source.GetNativeTexturePtr(),
                        destWidth,
                        destHeight,
                        flipVertically,
                        QualitySettings.activeColorSpace == ColorSpace.Gamma,
                        &eventFuncPtr,
                        &eventId,
                        &eventDataPtr,
                        CallbackHelper.GetBlitTargetDataCallbackPtr(),
                        contextHandle);

                    if (!success || eventFuncPtr == null)
                    {
                        if (task.IsCompleted)
                            task.GetAwaiter().GetResult(); // throws if there was an error
                        throw new UniEncException(UniencErrorKind.Error, "Failed to create blit closure");
                    }

                    cmd.IssuePluginEventAndData((nint)eventFuncPtr, unchecked((int)eventId), (nint)eventDataPtr);
                }

                return task;
            }
        }
    }
}
