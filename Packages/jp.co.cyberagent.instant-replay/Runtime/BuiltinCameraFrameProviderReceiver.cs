using System;
using UnityEngine;

namespace InstantReplay
{
    public class BuiltinCameraFrameProviderReceiver : MonoBehaviour
    {
        public event IFrameProvider.ProvideFrame OnFrameReceived;
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            // If Image Effect is present, the camera always renders into intermediate RenderTexture and its content is vertically flipped only when SystemInfo.graphicsUVStartsAtTop=true.
            OnFrameReceived?.Invoke(new IFrameProvider.Frame(source, Time.unscaledTimeAsDouble, false));
            Graphics.Blit(source, destination);
        }
    }
}
