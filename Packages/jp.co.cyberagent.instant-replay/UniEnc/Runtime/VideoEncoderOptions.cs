using System;

namespace UniEnc
{
    /// <summary>
    ///     Options for configuring video encoding parameters.
    /// </summary>
    public struct VideoEncoderOptions
    {
        /// <summary>
        ///     Width of the video in pixels.
        /// </summary>
        public uint Width { get; set; }

        /// <summary>
        ///     Height of the video in pixels.
        /// </summary>
        public uint Height { get; set; }

        /// <summary>
        ///     Frames per second hint for the encoder.
        /// </summary>
        public uint FpsHint { get; set; }

        /// <summary>
        ///     Target bitrate in bits per second.
        /// </summary>
        public uint Bitrate { get; set; }

        /// <summary>
        ///     Creates a new VideoEncoderOptions with default values.
        /// </summary>
        public static VideoEncoderOptions Default => new()
        {
            Width = 1920,
            Height = 1080,
            FpsHint = 30,
            Bitrate = 10_000_000 // 10 Mbps
        };

        /// <summary>
        ///     Validates the options and throws if invalid.
        /// </summary>
        internal void Validate()
        {
            if (Width == 0 || Height == 0)
                throw new ArgumentException("Video width and height must be greater than 0");

            if (FpsHint == 0)
                throw new ArgumentException("FPS hint must be greater than 0");

            if (Bitrate == 0)
                throw new ArgumentException("Bitrate must be greater than 0");
        }

        /// <summary>
        ///     Converts to native struct for interop.
        /// </summary>
        internal VideoEncoderOptionsNative ToNative()
        {
            Validate();
            return new VideoEncoderOptionsNative
            {
                width = Width,
                height = Height,
                fps_hint = FpsHint,
                bitrate = Bitrate
            };
        }
    }
}
