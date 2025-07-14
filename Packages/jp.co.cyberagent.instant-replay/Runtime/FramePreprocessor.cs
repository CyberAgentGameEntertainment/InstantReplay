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
        private readonly int? _fixedHeight;
        private readonly int? _fixedWidth;
        private readonly int? _maxHeight;
        private readonly int? _maxWidth;
        private RenderTexture _output;

        private FramePreprocessor(int? maxWidth, int? maxHeight, int? fixedWidth, int? fixedHeight)
        {
            _maxWidth = maxWidth;
            _maxHeight = maxHeight;
            _fixedWidth = fixedWidth;
            _fixedHeight = fixedHeight;
        }

        public void Dispose()
        {
            if (_output)
            {
                Object.Destroy(_output);
                _output = default;
            }
        }


        public static FramePreprocessor WithMaxSize(int? maxWidth, int? maxHeight)
        {
            if (maxWidth is <= 0 || maxHeight is <= 0)
                throw new ArgumentException("Max width and height must be greater than zero.");
            return new FramePreprocessor(maxWidth, maxHeight, null, null);
        }

        public static FramePreprocessor WithFixedSize(int fixedWidth, int fixedHeight)
        {
            if (fixedWidth <= 0 || fixedHeight <= 0)
                throw new ArgumentException("Fixed width and height must be greater than zero.");
            return new FramePreprocessor(null, null, fixedWidth, fixedHeight);
        }

        public RenderTexture Process(Texture source, bool needFlipVertically)
        {
            // scaling

            var scale = 1f;
            if (_maxWidth is { } maxWidth)
                scale = Mathf.Min(scale, maxWidth / (float)source.width);

            if (_maxHeight is { } maxHeight)
                scale = Mathf.Min(scale, maxHeight / (float)source.height);

            var width = _fixedWidth ?? (int)(source.width * scale);
            var height = _fixedHeight ?? (int)(source.height * scale);

            if (_output == null)
            {
                _output = new RenderTexture(width, height, 0, GraphicsFormat.R8G8B8A8_SRGB);
            }
            else if (_output.width != width || _output.height != height)
            {
                _output.Release();
                _output.width = width;
                _output.height = height;
                _output.Create();
            }

            // scale to fit
            var pixelScale = Mathf.Min((float)width / source.width, (float)height / source.height);
            var renderScale = new Vector2(pixelScale * source.width / width, pixelScale * source.height / height);

            var active = RenderTexture.active;
            if (needFlipVertically)
                // We need to flip the image vertically on some platforms
                Graphics.Blit(source, _output, renderScale * new Vector2(1f, -1f), new Vector2(0, 1f));
            else
                Graphics.Blit(source, _output, renderScale, Vector2.zero);

            RenderTexture.active = active;
            return _output;
        }
    }
}
