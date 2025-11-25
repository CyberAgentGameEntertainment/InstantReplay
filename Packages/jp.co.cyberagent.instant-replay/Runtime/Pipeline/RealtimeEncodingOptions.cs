// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UniEnc;

namespace InstantReplay
{
    /// <summary>
    ///     Configuration options for realtime encoding mode.
    /// </summary>
    public struct RealtimeEncodingOptions
    {
        [Obsolete("Use MaxMemoryUsageBytesForCompressedFrames instead.")]
        public long MaxMemoryUsageBytes
        {
            get => MaxMemoryUsageBytesForCompressedFrames;
            set => MaxMemoryUsageBytesForCompressedFrames = value;
        }

        public long MaxMemoryUsageBytesForCompressedFrames { get; set; }
        public int? MaxNumberOfRawFrameBuffers { get; set; }

        public VideoEncoderOptions VideoOptions { get; set; }
        public AudioEncoderOptions AudioOptions { get; set; }

        public double? FixedFrameRate { get; set; }

        public int VideoInputQueueSize { get; set; }

        [Obsolete("Use AudioInputQueueSizeSeconds instead.")]
        public int AudioInputQueueSize
        {
            get => 0;
            set { }
        }

        /// <summary>
        ///     Max queued audio input duration to be buffered before encoding, in seconds.
        /// </summary>
        public double? AudioInputQueueSizeSeconds { get; set; }

        /// <summary>
        ///     If the timestamp reported by the IFrameProvider deviates from the actual time by more than this threshold, the
        ///     frame timestamp is adjusted. This reduces frame timing discrepancies but may cause frames to appear to skip.
        /// </summary>
        public double? VideoLagAdjustmentThreshold { get; set; }

        /// <summary>
        ///     If the timestamp reported by IAudioSampleProvider deviates from the actual time by more than this threshold, the
        ///     audio sample timestamps will be adjusted. This reduces audio misalignment but may introduce noise.
        /// </summary>
        public double? AudioLagAdjustmentThreshold { get; set; }

        public bool ForceReadback { get; set; }
    }
}
