using System;
using UnityEngine;

namespace InstantReplay.UniversalRP
{
    public class RendererFeatureFrameProvider : IFrameProvider
    {
        private readonly Action<Camera, IFrameProvider.Frame> _frameProvidedHandler;

        public RendererFeatureFrameProvider(Camera camera)
        {
            InstantReplayFrameRenderPass.OnFrameProvided += _frameProvidedHandler = (cam, frame) =>
            {
                if (cam == camera)
                {
                    OnFrameProvided?.Invoke(frame);
                }
            };
        }

        public void Dispose()
        {
            InstantReplayFrameRenderPass.OnFrameProvided -= _frameProvidedHandler;
        }

        public event IFrameProvider.ProvideFrame OnFrameProvided;
    }
}
