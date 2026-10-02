// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using UniEnc;

namespace InstantReplay
{
    /// <summary>
    ///     Timestamp and kind of a buffered frame, without its payload.
    /// </summary>
    internal readonly struct EncodedFrameDescriptor
    {
        public readonly double Timestamp;
        public readonly UniencSampleKind Kind;

        public EncodedFrameDescriptor(double timestamp, UniencSampleKind kind)
        {
            Timestamp = timestamp;
            Kind = kind;
        }
    }

    /// <summary>
    ///     Decides which of the buffered frames make up the exported segment. Shared by the in-memory buffer, the
    ///     disk-backed buffer, and crash recovery, so that all three select frames identically.
    /// </summary>
    internal static class EncodedFrameSelector
    {
        /// <summary>
        ///     Finds the index of the first video frame and the first audio frame to export.
        /// </summary>
        /// <param name="video">Video samples in encode order, excluding codec configuration.</param>
        /// <param name="audio">Audio samples in encode order, excluding codec configuration.</param>
        /// <param name="latestVideoTimestamp">Timestamp of the most recent in-order video sample.</param>
        /// <param name="durationSeconds">Requested duration, or null to start at the earliest key frame.</param>
        /// <returns>False when no key frame is available, in which case nothing can be exported.</returns>
        public static bool TrySelect(ReadOnlySpan<EncodedFrameDescriptor> video,
            ReadOnlySpan<EncodedFrameDescriptor> audio, double latestVideoTimestamp, double? durationSeconds,
            out int videoStartIndex, out int audioStartIndex)
        {
            videoStartIndex = -1;
            audioStartIndex = 0;

            if (video.Length == 0) return false;

            // find keyframe
            if (durationSeconds is { } durationSecondsValue)
            {
                // TODO: binary search
                var expectedStartTime = latestVideoTimestamp - durationSecondsValue;
                var minTimespan = double.MaxValue;
                for (var i = 0; i < video.Length; i++)
                {
                    if (video[i].Kind != UniencSampleKind.Key) continue;
                    var timespan = Math.Abs(video[i].Timestamp - expectedStartTime);
                    if (timespan >= minTimespan) continue;
                    minTimespan = timespan;
                    videoStartIndex = i;
                }
            }
            else
            {
                for (var i = 0; i < video.Length; i++)
                {
                    if (video[i].Kind != UniencSampleKind.Key) continue;
                    videoStartIndex = i;
                    break;
                }
            }

            if (videoStartIndex == -1) return false;

            // find audio start index
            if (audio.Length == 0)
            {
                audioStartIndex = 0;
                return true;
            }

            var actualDuration = latestVideoTimestamp - video[videoStartIndex].Timestamp;
            var expectedAudioStartTime = audio[^1].Timestamp - actualDuration;

            var minAudioTimespan = double.MaxValue;
            audioStartIndex = -1;
            for (var i = 0; i < audio.Length; i++)
            {
                var timespan = Math.Abs(audio[i].Timestamp - expectedAudioStartTime);
                if (timespan >= minAudioTimespan) continue;
                minAudioTimespan = timespan;
                audioStartIndex = i;
            }

            return true;
        }

        public static EncodedFrameDescriptor[] ToDescriptors(ReadOnlySpan<EncodedFrame> frames)
        {
            var descriptors = new EncodedFrameDescriptor[frames.Length];
            for (var i = 0; i < frames.Length; i++)
                descriptors[i] = new EncodedFrameDescriptor(frames[i].Timestamp, frames[i].Kind);
            return descriptors;
        }

        public static EncodedFrameDescriptor[] ToDescriptors(IReadOnlyList<DiskBufferScannedRecord> records)
        {
            var descriptors = new EncodedFrameDescriptor[records.Count];
            for (var i = 0; i < records.Count; i++)
                descriptors[i] = new EncodedFrameDescriptor(records[i].Timestamp, records[i].Kind);
            return descriptors;
        }

        /// <summary>
        ///     Rebases the timestamps of the frames so that the first frame starts at zero.
        /// </summary>
        public static void RebaseTimestamps(Span<EncodedFrame> frames)
        {
            if (frames.IsEmpty) return;

            var startTime = frames[0].Timestamp;
            for (var i = 0; i < frames.Length; i++)
            {
                ref var frame = ref frames[i];
                frame = frame.WithTimestamp(frame.Timestamp - startTime);
            }
        }

        /// <summary>
        ///     Returns the frames with the codec configuration prepended, which the muxer requires before any sample.
        /// </summary>
        public static Memory<EncodedFrame> PrependMetadata(Memory<EncodedFrame> frames,
            ReadOnlyMemory<EncodedFrame> metadata)
        {
            if (metadata.Length == 0) return frames;

            var result = new EncodedFrame[frames.Length + metadata.Length];
            metadata.Span.CopyTo(result);
            frames.Span.CopyTo(result.AsSpan(metadata.Length));
            return result.AsMemory();
        }

        /// <summary>
        ///     Disposes every frame in the span, reporting but not propagating failures.
        /// </summary>
        public static void DisposeAll(ReadOnlySpan<EncodedFrame> frames, Action<Exception> onError)
        {
            foreach (var frame in frames)
                try
                {
                    frame.Dispose();
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex);
                }
        }
    }
}
