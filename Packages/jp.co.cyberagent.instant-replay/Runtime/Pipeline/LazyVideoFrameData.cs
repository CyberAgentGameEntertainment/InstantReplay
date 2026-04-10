// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System.Threading.Tasks;
using UniEnc;
using UniEnc.Unity;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace InstantReplay
{
    /// <summary>
    ///     Represents video frame data for processing.
    /// </summary>
    internal readonly struct LazyVideoFrameData
    {
        public readonly DataKind Kind;
        public readonly double Timestamp;
        public readonly int Width;
        public readonly int Height;

        public readonly ValueTask<SharedBuffer<NativeArrayWrapper>> ReadbackTask;
        public readonly Texture BlitSource;
        public readonly GraphicsFormat BlitSourceFormat;
        public readonly bool IsGammaWorkflow;
        public readonly bool FlipVertically;

        public LazyVideoFrameData(ValueTask<SharedBuffer<NativeArrayWrapper>> readbackTask, int width, int height,
            double timestamp)
        {
            Kind = DataKind.SharedBuffer;
            ReadbackTask = readbackTask;
            Width = width;
            Height = height;
            Timestamp = timestamp;

            BlitSource = null;
            BlitSourceFormat = default;
            IsGammaWorkflow = QualitySettings.activeColorSpace == ColorSpace.Gamma;
            FlipVertically = false;
        }

        public LazyVideoFrameData(Texture texture, double timestamp, bool flipVertically = false)
        {
            Kind = DataKind.BlitSource;
            BlitSource = texture;
            BlitSourceFormat = texture.graphicsFormat;
            Timestamp = timestamp;

            ReadbackTask = default;
            Width = texture.width;
            Height = texture.height;
            IsGammaWorkflow = QualitySettings.activeColorSpace == ColorSpace.Gamma;
            FlipVertically = flipVertically;
        }

        public enum DataKind
        {
            SharedBuffer,
            BlitSource
        }
    }
}
