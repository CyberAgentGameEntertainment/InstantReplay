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
        public long? MaxMemoryUsageBytesForUncompressedFrames { get; set; }

        public VideoEncoderOptions VideoOptions { get; set; }
        public AudioEncoderOptions AudioOptions { get; set; }

        public double? FixedFrameRate { get; set; }

        public int VideoInputQueueSize { get; set; }
        public int AudioInputQueueSize { get; set; }
    }
}
