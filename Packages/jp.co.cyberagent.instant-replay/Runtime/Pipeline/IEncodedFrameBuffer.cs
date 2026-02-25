// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UniEnc;

namespace InstantReplay
{
    /// <summary>
    ///     Common interface for encoded frame buffers.
    /// </summary>
    internal interface IEncodedFrameBuffer : IDisposable
    {
        bool TryAddVideoFrame(EncodedFrame frame);
        bool TryAddAudioFrame(EncodedFrame frame);

        void GetFramesForDuration(double? durationSeconds,
            out ReadOnlyMemory<EncodedFrame> videoFrames,
            out ReadOnlyMemory<EncodedFrame> audioFrames);
    }
}
