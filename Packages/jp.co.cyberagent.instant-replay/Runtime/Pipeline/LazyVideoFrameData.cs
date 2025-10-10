// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Represents video frame data for processing.
    /// </summary>
    internal readonly struct LazyVideoFrameData
    {
        public readonly ValueTask<NativeArray<byte>> ReadbackTask;
        public readonly int Width;
        public readonly int Height;
        public readonly double Timestamp;

        public LazyVideoFrameData(ValueTask<NativeArray<byte>> readbackTask, int width, int height, double timestamp)
        {
            ReadbackTask = readbackTask;
            Width = width;
            Height = height;
            Timestamp = timestamp;
        }
    }
}
