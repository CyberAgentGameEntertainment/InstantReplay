// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using UnityEngine;
using Object = UnityEngine.Object;

namespace InstantReplay
{
    /// <summary>
    ///     A frame provider that captures the screen using Built-in Render Pipeline.
    /// </summary>
    internal class BrpScreenshotFrameProvider : IFrameProvider
    {
        private RenderTexture _renderTexture;

        public BrpScreenshotFrameProvider()
        {
            _renderTexture = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
            Camera.onPostRender += EndContextRendering;
        }

        public event IFrameProvider.ProvideFrame OnFrameProvided;

        public void Dispose()
        {
            Camera.onPostRender -= EndContextRendering;

            if (_renderTexture)
            {
                Object.Destroy(_renderTexture);
                _renderTexture = null;
            }
        }

        private void EndContextRendering(Camera camera)
        {
            if (camera != Camera.main)
            {
                return;
            }

            var time = Time.unscaledTimeAsDouble;

            var width = Screen.width;
            var height = Screen.height;

            if (!_renderTexture) return;

            if (_renderTexture.width != width || _renderTexture.height != height)
            {
                _renderTexture.Release();
                _renderTexture.width = width;
                _renderTexture.height = height;
                _renderTexture.Create();
            }

            ScreenCapture.CaptureScreenshotIntoRenderTexture(_renderTexture);

            OnFrameProvided?.Invoke(_renderTexture, time);
        }
    }
}
