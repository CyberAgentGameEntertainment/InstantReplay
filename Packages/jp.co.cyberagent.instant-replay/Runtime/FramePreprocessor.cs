// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
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

        public RenderTexture Process(Texture source, bool needFlipVertically)
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
                var format = SystemInfo.IsFormatSupported(source.graphicsFormat, FormatUsage.ReadPixels)
                    ? source.graphicsFormat
                    : GraphicsFormat.R8G8B8A8_SRGB;

                _output = new RenderTexture(width, height, 0, format);
            }
            else if (_output.width != width || _output.height != height)
            {
                _output.Release();
                _output.width = width;
                _output.height = height;
                _output.Create();
            }

            var active = RenderTexture.active;
            if (needFlipVertically)
                // We need to flip the image vertically on some platforms
                Graphics.Blit(source, _output, new Vector2(1f, -1f), new Vector2(0, 1f));
            else
                Graphics.Blit(source, _output);

            RenderTexture.active = active;
            return _output;
        }
    }
}
