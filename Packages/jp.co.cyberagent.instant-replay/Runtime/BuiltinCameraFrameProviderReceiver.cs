using System;
using UnityEngine;

namespace InstantReplay
{
    public class BuiltinCameraFrameProviderReceiver : MonoBehaviour
    {
        public event IFrameProvider.ProvideFrame OnFrameReceived;
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            OnFrameReceived?.Invoke(new IFrameProvider.Frame(source, Time.unscaledTimeAsDouble, dataStartsAtTop: true));
            Graphics.Blit(source, destination);
        }
    }
}
