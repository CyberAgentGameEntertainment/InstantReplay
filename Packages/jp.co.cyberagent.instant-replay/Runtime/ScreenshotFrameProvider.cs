// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

#if UNITY_2023_1_OR_NEWER
using System.Threading;
#endif
using UnityEngine;
using Object = UnityEngine.Object;

namespace InstantReplay
{
    /// <summary>
    ///     A frame provider that captures the screen using SRP.
    /// </summary>
    public class ScreenshotFrameProvider : IFrameProvider
    {
        private RenderTexture _renderTexture;

        public ScreenshotFrameProvider()
        {
            _renderTexture = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);

#if UNITY_2023_1_OR_NEWER
            _ = EndOfFrameLoop();
            async Awaitable EndOfFrameLoop()
            {
                var ct = _cancelOnDispose.Token;
                do
                {
                    await Awaitable.EndOfFrameAsync(); // passing cancellation token emits too much garbage
                    if (ct.IsCancellationRequested) break;
                    OnEndOfFrame();
                } while (true);
            }
#else
            EventCallbackEntryPoint.EndOfFrame += OnEndOfFrame;
#endif
        }

#if UNITY_2023_1_OR_NEWER
        private readonly CancellationTokenSource _cancelOnDispose = new();
#endif

        public event IFrameProvider.ProvideFrame OnFrameProvided;

        public void Dispose()
        {
#if UNITY_2023_1_OR_NEWER
            _cancelOnDispose.Cancel();
#else
            EventCallbackEntryPoint.EndOfFrame -= OnEndOfFrame;
#endif

            if (_renderTexture)
            {
                Object.Destroy(_renderTexture);
                _renderTexture = default;
            }
        }

        private void OnEndOfFrame()
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
