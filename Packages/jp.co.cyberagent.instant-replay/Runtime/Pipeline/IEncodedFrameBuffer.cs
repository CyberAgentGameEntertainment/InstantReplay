// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    /// <summary>
    ///     Common interface for encoded frame buffers.
    /// </summary>
    internal interface IEncodedFrameBuffer : IDisposable
    {
        /// <summary>
        ///     Takes ownership of an encoded video frame. Returns false when the frame was not accepted, in which case the
        ///     caller remains responsible for disposing it.
        /// </summary>
        bool TryAddVideoFrame(EncodedFrame frame);

        /// <summary>
        ///     Takes ownership of an encoded audio frame. Returns false when the frame was not accepted, in which case the
        ///     caller remains responsible for disposing it.
        /// </summary>
        bool TryAddAudioFrame(EncodedFrame frame);

        /// <summary>
        ///     Selects the trailing frames covering the specified duration, starting at a key frame. Pass null to select
        ///     everything from the earliest available key frame. The caller owns and must dispose every returned frame.
        /// </summary>
        ValueTask<EncodedFrameSelection> GetFramesForDurationAsync(double? durationSeconds);
    }

    /// <summary>
    ///     Frames selected for muxing, with timestamps rebased so that each stream starts at zero and the codec
    ///     configuration prepended.
    /// </summary>
    internal readonly struct EncodedFrameSelection
    {
        public readonly ReadOnlyMemory<EncodedFrame> VideoFrames;
        public readonly ReadOnlyMemory<EncodedFrame> AudioFrames;

        public EncodedFrameSelection(ReadOnlyMemory<EncodedFrame> videoFrames,
            ReadOnlyMemory<EncodedFrame> audioFrames)
        {
            VideoFrames = videoFrames;
            AudioFrames = audioFrames;
        }
    }
}
