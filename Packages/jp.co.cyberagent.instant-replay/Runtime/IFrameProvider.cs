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
        public delegate void ProvideFrame(RenderTexture frame, double timestamp);

        /// <summary>
        ///     An event that will be invoked when a new frame is provided.
        /// </summary>
        event ProvideFrame OnFrameProvided;
    }
}
