// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace InstantReplay
{
    /// <summary>
    ///     A frame provider that captures the screen using SRP.
    /// </summary>
    public class SrpScreenshotFrameProvider : IFrameProvider
    {
        private RenderTexture _renderTexture;

        public SrpScreenshotFrameProvider()
        {
            _renderTexture = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
            RenderPipelineManager.endContextRendering += EndContextRendering;
        }

        public event IFrameProvider.ProvideFrame OnFrameProvided;

        public void Dispose()
        {
            RenderPipelineManager.endContextRendering -= EndContextRendering;

            if (_renderTexture)
            {
                Object.Destroy(_renderTexture);
                _renderTexture = default;
            }
        }

        private void EndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
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

            OnFrameProvided?.Invoke(new IFrameProvider.Frame(_renderTexture, time, SystemInfo.graphicsUVStartsAtTop));
        }
    }
}
