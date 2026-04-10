using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UniEnc.Unity
{
    public static class VideoEncoderExtensions
    {
        public static ValueTask PushFrameAsync(this VideoEncoder encoder, Texture source, double timestamp,
            bool flipVertically = false)
        {
            var textureHandle = TextureHandle.Alloc(source);
            try
            {
                return encoder.UnsafePushUnityFrameAsync(textureHandle, (uint)source.width,
                    (uint)source.height, source.graphicsFormat, QualitySettings.activeColorSpace == ColorSpace.Gamma,
                    timestamp, flipVertically);
            }
            catch (ObjectDisposedException)
            {
                textureHandle.Free();
                throw;
            }
        }

        public static ValueTask UnsafePushUnityFrameAsync(this VideoEncoder encoder, TextureHandle textureHandle,
            uint width, uint height, GraphicsFormat graphicsFormat, bool isGammaWorkflow, double timestamp,
            bool flipVertically = false)
        {
            return encoder.UnsafePushTextureTokenAsync(textureHandle.value, width, height, (uint)graphicsFormat,
                isGammaWorkflow, timestamp, (nuint)GraphicsEventIssuer.OnIssueGraphicsEventPtr, flipVertically);
        }

        [Obsolete("Use the overload that takes a TextureHandle instead of a raw native texture pointer.", true)]
        public static ValueTask UnsafePushUnityFrameAsync(this VideoEncoder encoder, nint sourceTexturePtr,
            uint width, uint height, GraphicsFormat graphicsFormat, bool isGammaWorkflow, double timestamp,
            bool flipVertically = false)
        {
            throw new NotSupportedException();
        }
    }
}
