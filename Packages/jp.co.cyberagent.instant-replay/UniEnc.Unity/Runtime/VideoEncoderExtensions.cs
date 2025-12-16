using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UniEnc.Unity
{
    public static class VideoEncoderExtensions
    {
        public static ValueTask PushFrameAsync(this VideoEncoder encoder, Texture source, double timestamp)
        {
            return encoder.UnsafePushUnityFrameAsync(source.GetNativeTexturePtr(), (uint)source.width,
                (uint)source.height,
                source.graphicsFormat, QualitySettings.activeColorSpace == ColorSpace.Gamma, timestamp);
        }

        public static ValueTask UnsafePushUnityFrameAsync(this VideoEncoder encoder, nint sourceTexturePtr, uint width,
            uint height, GraphicsFormat graphicsFormat, bool isGammaWorkflow, double timestamp)
        {
            return encoder.UnsafePushUnityFrameAsync(sourceTexturePtr, width, height,
                (uint)graphicsFormat, isGammaWorkflow, timestamp, (nuint)GraphicsEventIssuer.OnIssueGraphicsEventPtr);
        }
    }
}
