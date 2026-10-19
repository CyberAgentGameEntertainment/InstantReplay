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
        ///     everything from the earliest available key frame. The caller owns the returned selection and must dispose
        ///     it, together with every frame read from it.
        /// </summary>
        ValueTask<EncodedFrameSelection> GetFramesForDurationAsync(double? durationSeconds);
    }

    /// <summary>
    ///     Frames selected for muxing, with timestamps rebased so that each stream starts at zero and the codec
    ///     configuration prepended. Each stream is exposed as a sequential reader, so that a buffer kept on disk does not
    ///     have to hold the whole selection in memory.
    /// </summary>
    /// <remarks>
    ///     The two readers are independent and may be consumed concurrently, one by each track of the muxer. Disposing the
    ///     selection disposes both readers and every frame they have not yet returned.
    /// </remarks>
    internal readonly struct EncodedFrameSelection : IDisposable
    {
        public readonly IEncodedFrameReader VideoFrames;
        public readonly IEncodedFrameReader AudioFrames;

        public EncodedFrameSelection(IEncodedFrameReader videoFrames, IEncodedFrameReader audioFrames)
        {
            VideoFrames = videoFrames;
            AudioFrames = audioFrames;
        }

        /// <summary>
        ///     Whether nothing muxable was selected. Both readers are null in that case.
        /// </summary>
        public bool IsEmpty => VideoFrames == null;

        public void Dispose()
        {
            try
            {
                VideoFrames?.Dispose();
            }
            finally
            {
                AudioFrames?.Dispose();
            }
        }
    }

    /// <summary>
    ///     Sequential source of the frames of one stream. Not thread-safe; each instance is consumed by one track.
    /// </summary>
    internal interface IEncodedFrameReader : IDisposable
    {
        /// <summary>
        ///     Reads the next frame. Returns false at the end of the stream. The caller owns the returned frame and must
        ///     dispose it.
        /// </summary>
        bool TryReadNext(out EncodedFrame frame);
    }

    /// <summary>
    ///     Reader over frames that are already in memory. Takes ownership of the frames, and disposes those that have
    ///     not been read when it is disposed.
    /// </summary>
    internal sealed class EncodedFrameArrayReader : IEncodedFrameReader
    {
        private readonly ReadOnlyMemory<EncodedFrame> _frames;
        private int _position;

        public EncodedFrameArrayReader(ReadOnlyMemory<EncodedFrame> frames)
        {
            _frames = frames;
        }

        public bool TryReadNext(out EncodedFrame frame)
        {
            if (_position >= _frames.Length)
            {
                frame = default;
                return false;
            }

            frame = _frames.Span[_position++];
            return true;
        }

        public void Dispose()
        {
            var remaining = _frames.Span[_position..];
            _position = _frames.Length;
            foreach (var frame in remaining) frame.Dispose();
        }
    }
}
