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
        private static readonly int MainTexSt = Shader.PropertyToID("_MainTex_ST");
        private static readonly int Rechannel = Shader.PropertyToID("_Rechannel");
        private readonly int? _fixedHeight;
        private readonly int? _fixedWidth;
        private readonly int? _maxHeight;
        private readonly int? _maxWidth;
        private Material _material;
        private RenderTexture _output;

        private FramePreprocessor(int? maxWidth, int? maxHeight, int? fixedWidth, int? fixedHeight,
            Matrix4x4 rechannelMatrix)
        {
            _maxWidth = maxWidth;
            _maxHeight = maxHeight;
            _fixedWidth = fixedWidth;
            _fixedHeight = fixedHeight;
            _material = new Material(Resources.Load<Shader>("InstantReplayRechannel"));
            _material.SetMatrix(Rechannel, rechannelMatrix);
        }

        public void Dispose()
        {
            if (_output)
            {
                Object.Destroy(_output);
                _output = default;
            }

            if (_material)
            {
                Object.Destroy(_material);
                _material = default;
            }
        }


        public static FramePreprocessor WithMaxSize(int? maxWidth, int? maxHeight, Matrix4x4 rechannelMatrix)
        {
            if (maxWidth is <= 0 || maxHeight is <= 0)
                throw new ArgumentException("Max width and height must be greater than zero.");
            return new FramePreprocessor(maxWidth, maxHeight, null, null, rechannelMatrix);
        }

        public static FramePreprocessor WithFixedSize(int fixedWidth, int fixedHeight, Matrix4x4 rechannelMatrix)
        {
            if (fixedWidth <= 0 || fixedHeight <= 0)
                throw new ArgumentException("Fixed width and height must be greater than zero.");
            return new FramePreprocessor(null, null, fixedWidth, fixedHeight, rechannelMatrix);
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
                _material.SetVector(MainTexSt, new Vector4(renderScale.x, -renderScale.y, 0f, 1f));
            else
                _material.SetVector(MainTexSt, new Vector4(renderScale.x, renderScale.y, 0f, 0f));

            Graphics.Blit(source, _output, _material);

            RenderTexture.active = active;
            return _output;
        }
    }
}
