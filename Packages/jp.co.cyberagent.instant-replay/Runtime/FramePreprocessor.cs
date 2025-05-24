// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace InstantReplay
{
    internal class FramePreprocessor : IDisposable
    {
        private readonly int? _maxHeight;
        private readonly int? _maxWidth;
        private RenderTexture _output;

        public FramePreprocessor(int? maxWidth, int? maxHeight)
        {
            _maxWidth = maxWidth;
            _maxHeight = maxHeight;
        }

        public void Dispose()
        {
            if (_output)
            {
                Object.Destroy(_output);
                _output = default;
            }
        }

        public RenderTexture Process(RenderTexture source)
        {
            // scaling

            var scale = 1f;
            if (_maxWidth is { } maxWidth)
                scale = Mathf.Min(scale, maxWidth / (float)source.width);

            if (_maxHeight is { } maxHeight)
                scale = Mathf.Min(scale, maxHeight / (float)source.height);

            var width = (int)(source.width * scale);
            var height = (int)(source.height * scale);

            if (_output == null)
            {
                _output = new RenderTexture(width, height, 0, source.format);
            }
            else if (_output.width != width || _output.height != height)
            {
                _output.Release();
                _output.width = width;
                _output.height = height;
                _output.Create();
            }

            var active = RenderTexture.active;
            if (SystemInfo.graphicsUVStartsAtTop && IsUrp())
                // We need to flip the image vertically on some platforms
                Graphics.Blit(source, _output, new Vector2(1f, -1f), new Vector2(0, 1f));
            else
                Graphics.Blit(source, _output);

            RenderTexture.active = active;
            return _output;
        }

        private static bool IsUrp()
        {
            return GraphicsSettings.currentRenderPipeline;
            // Note: if using Built-in Render Pipeline, GraphicsSettings.currentRenderPipeline will be null.
        }
    }
}
