// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System.Threading.Tasks;
using UniEnc;
using UnityEngine;

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

        public readonly ValueTask<SharedBuffer> ReadbackTask;
        public readonly Texture BlitSource;
        public readonly nint NativeBlitSourceHandle;

        public LazyVideoFrameData(ValueTask<SharedBuffer> readbackTask, int width, int height, double timestamp)
        {
            Kind = DataKind.SharedBuffer;
            ReadbackTask = readbackTask;
            Width = width;
            Height = height;
            Timestamp = timestamp;

            BlitSource = null;
            NativeBlitSourceHandle = default;
        }

        public LazyVideoFrameData(Texture texture, double timestamp)
        {
            Kind = DataKind.BlitSource;
            BlitSource = texture;
            NativeBlitSourceHandle = texture.GetNativeTexturePtr();
            Timestamp = timestamp;

            ReadbackTask = default;
            Width = texture.width;
            Height = texture.height;
        }

        public enum DataKind
        {
            SharedBuffer,
            BlitSource
        }
    }
}
