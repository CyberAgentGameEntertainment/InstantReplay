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
        private static readonly int ScaleAndTiling = Shader.PropertyToID("_ScaleAndTiling");
        private static readonly int Rechannel = Shader.PropertyToID("_Rechannel");
        private readonly int? _fixedHeight;
        private readonly int? _fixedWidth;
        private readonly int? _maxHeight;
        private readonly int? _maxWidth;
        private Material _material;

        private FramePreprocessor(int? maxWidth, int? maxHeight, int? fixedWidth, int? fixedHeight,
            Matrix4x4 rechannelMatrix)
        {
            _maxWidth = maxWidth;
            _maxHeight = maxHeight;
            _fixedWidth = fixedWidth;
            _fixedHeight = fixedHeight;
            var shader = Resources.Load<Shader>("InstantReplayPreprocess");
            if (shader == null)
                throw new InvalidOperationException("Shader 'InstantReplayPreprocess' not found in Resources.");
            _material = new Material(shader);
            _material.SetMatrix(Rechannel, rechannelMatrix);
            _material.SetVector(MainTexSt, new Vector4(1f, 1f, 0f, 0f));
        }

        private RenderTexture Output { get; set; }

        public void Dispose()
        {
            if (Output)
            {
                if (Application.isPlaying)
                    Object.Destroy(Output);
                else
                    Object.DestroyImmediate(Output);
                Output = default;
            }

            if (_material)
            {
                if (Application.isPlaying)
                    Object.Destroy(_material);
                else
                    Object.DestroyImmediate(_material);
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

            if (Output == null)
            {
                Output = new RenderTexture(width, height, 0, GraphicsFormat.R8G8B8A8_SRGB);
                Output.filterMode = FilterMode.Bilinear;
                Output.wrapMode = TextureWrapMode.Clamp;
            }
            else if (Output.width != width || Output.height != height)
            {
                Output.Release();
                Output.width = width;
                Output.height = height;
                Output.Create();
            }

            // scale to fit
            var pixelScale = Mathf.Min((float)width / source.width, (float)height / source.height);
            var renderScale = new Vector2(pixelScale * source.width / width, pixelScale * source.height / height);
            // Debug.Log($"source: {source.width}x{source.height}, dest: {width}x{height}, pixelScale: {pixelScale}, renderScale: {renderScale.x}x{renderScale.y}, needFlipVertically: {needFlipVertically}");

            var active = RenderTexture.active;
            if (needFlipVertically)
                _material.SetVector(ScaleAndTiling, new Vector4(1f / renderScale.x, -1f / renderScale.y, 0f, 1f));
            else
                _material.SetVector(ScaleAndTiling, new Vector4(1f / renderScale.x, 1f / renderScale.y, 0f, 0f));

            Graphics.Blit(source, Output, _material);

            RenderTexture.active = active;
            return Output;
        }
    }
}
