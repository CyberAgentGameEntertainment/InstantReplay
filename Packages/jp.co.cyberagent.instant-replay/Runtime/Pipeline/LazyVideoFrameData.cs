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
        public readonly ValueTask<SharedBuffer> ReadbackTask;
        public readonly int Width;
        public readonly int Height;
        public readonly double Timestamp;

        public LazyVideoFrameData(ValueTask<SharedBuffer> readbackTask, int width, int height, double timestamp)
        {
            ReadbackTask = readbackTask;
            Width = width;
            Height = height;
            Timestamp = timestamp;
        }
    }
}
