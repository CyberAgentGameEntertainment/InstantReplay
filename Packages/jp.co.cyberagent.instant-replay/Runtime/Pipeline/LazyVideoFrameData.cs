// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System.Threading.Tasks;
using UniEnc;

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
        public readonly ValueTask<BlitTargetHandle> BlitTask;

        public LazyVideoFrameData(ValueTask<SharedBuffer> readbackTask, int width, int height, double timestamp)
        {
            Kind = DataKind.SharedBuffer;
            ReadbackTask = readbackTask;
            Width = width;
            Height = height;
            Timestamp = timestamp;

            BlitTask = default;
        }

        public LazyVideoFrameData(ValueTask<BlitTargetHandle> blitTask, int width, int height, double timestamp)
        {
            Kind = DataKind.BlitTarget;
            BlitTask = blitTask;
            Width = width;
            Height = height;
            Timestamp = timestamp;

            ReadbackTask = default;
        }

        public enum DataKind
        {
            SharedBuffer,
            BlitTarget
        }
    }
}
