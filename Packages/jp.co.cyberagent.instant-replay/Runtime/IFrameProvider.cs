// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     An abstraction of source of frames for recording.
    /// </summary>
    public interface IFrameProvider : IDisposable
    {
        public delegate void ProvideFrame(Frame frame);

        /// <summary>
        ///     An event that will be invoked when a new frame is provided.
        /// </summary>
        event ProvideFrame OnFrameProvided;

        public readonly struct Frame
        {
            public Texture Texture { get; }
            public double Timestamp { get; }
            public bool NeedFlipVertically { get; }

            public Frame(Texture texture, double timestamp, bool needFlipVertically = false)
            {
                Texture = texture;
                Timestamp = timestamp;
                NeedFlipVertically = needFlipVertically;
            }
        }
    }
}
