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

        /// <summary>
        ///     Enables the disk buffer. When set, encoded frames are written to segment files instead of being held in
        ///     memory, which lowers memory pressure and makes the footage leading up to a crash recoverable through
        ///     <see cref="DiskEncodedFrameBufferRecovery" />. <see cref="MaxMemoryUsageBytesForCompressedFrames" /> is not
        ///     used in that case.
        ///     Null, the default, keeps the in-memory buffer. Assign <see cref="DiskBufferOptions.Default" /> to enable the
        ///     disk buffer with its default configuration.
        /// </summary>
        /// <remarks>
        ///     Recording continuously to storage shortens the lifespan of flash memory, so this is intended primarily for
        ///     development and quality-assurance builds.
        /// </remarks>
        public DiskBufferOptions? DiskBuffer { get; set; }

        public static ref readonly RealtimeEncodingOptions Default => ref DefaultValue; 
        private static readonly RealtimeEncodingOptions DefaultValue =
            new()
            {
                VideoOptions = new VideoEncoderOptions
                {
                    Width = 1280,
                    Height = 720,
                    FpsHint = 30,
                    Bitrate = 2500000 // 2.5 Mbps
                },
                AudioOptions = new AudioEncoderOptions
                {
                    SampleRate = 44100,
                    Channels = 2,
                    Bitrate = 128000 // 128 kbps
                },
                MaxMemoryUsageBytesForCompressedFrames = 20 * 1024 * 1024, // 20 MiB
                FixedFrameRate = 30.0,
                VideoInputQueueSize = 5,
                AudioInputQueueSizeSeconds = 1.0
            };
    }
}
