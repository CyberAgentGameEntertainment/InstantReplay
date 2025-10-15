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
        // ReSharper disable once ArrangeTypeMemberModifiers
        public delegate void ProvideFrame(Frame frame);

        /// <summary>
        ///     An event that will be invoked when a new frame is provided.
        /// </summary>
        event ProvideFrame OnFrameProvided;

        // ReSharper disable once ArrangeTypeMemberModifiers
        public struct Frame : IDiscreteTemporalData
        {
            public Texture Texture { get; }
            public double Timestamp { get; set; }
            [Obsolete] public bool NeedFlipVertically => DataStartsAtTop;
            public bool DataStartsAtTop { get; }

            public Frame(Texture texture, double timestamp, bool dataStartsAtTop = false)
            {
                Texture = texture;
                Timestamp = timestamp;
                DataStartsAtTop = dataStartsAtTop;
            }
        }
    }
}
