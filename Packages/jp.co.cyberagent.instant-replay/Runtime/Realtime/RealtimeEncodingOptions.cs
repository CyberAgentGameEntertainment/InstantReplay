using UniEnc;

namespace InstantReplay
{
    /// <summary>
    ///     Configuration options for realtime encoding mode.
    /// </summary>
    public struct RealtimeEncodingOptions
    {
        public long MaxMemoryUsageBytes { get; set; }

        public VideoEncoderOptions VideoOptions { get; set; }
        public AudioEncoderOptions AudioOptions { get; set; }

        public double TargetFrameRate { get; set; }

        public int VideoInputQueueSize { get; set; }
        public int AudioInputQueueSize { get; set; }
    }
}
